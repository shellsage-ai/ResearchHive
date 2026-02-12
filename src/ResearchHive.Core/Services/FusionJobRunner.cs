using ResearchHive.Core.Models;
using System.Text;

namespace ResearchHive.Core.Services;

/// <summary>
/// Idea Fusion Engine: combines inputs into new proposals with provenance mapping
/// </summary>
public class FusionJobRunner
{
    private readonly SessionManager _sessionManager;
    private readonly RetrievalService _retrievalService;
    private readonly LlmService _llmService;

    public FusionJobRunner(SessionManager sessionManager, RetrievalService retrievalService, LlmService llmService)
    {
        _sessionManager = sessionManager;
        _retrievalService = retrievalService;
        _llmService = llmService;
    }

    public async Task<ResearchJob> RunAsync(string sessionId, FusionRequest request, CancellationToken ct = default)
    {
        var db = _sessionManager.GetSessionDb(sessionId);

        var job = new ResearchJob
        {
            SessionId = sessionId,
            Type = JobType.Fusion,
            Prompt = request.Prompt,
            State = JobState.Planning
        };
        db.SaveJob(job);
        AddReplay(job, "start", "Fusion Started", $"Mode: {request.Mode}\nPrompt: {request.Prompt}");

        try
        {
            // 1. Gather input sources
            job.State = JobState.Searching;
            db.SaveJob(job);

            var inputTexts = new List<(string sourceId, string text)>();

            // Get reports from the session
            var sessionReports = db.GetReports();
            foreach (var rpt in sessionReports)
            {
                if (request.InputSourceIds.Count == 0 || request.InputSourceIds.Contains(rpt.Id) ||
                    request.InputSourceIds.Contains(rpt.JobId))
                {
                    inputTexts.Add((rpt.Id, rpt.Content));
                }
            }

            // Get notebook entries
            var notes = db.GetNotebookEntries();
            foreach (var note in notes)
            {
                if (request.InputSourceIds.Count == 0 || request.InputSourceIds.Contains(note.Id))
                {
                    inputTexts.Add((note.Id, note.Content));
                }
            }

            // Also use retrieval for topic-relevant evidence
            var evidence = await _retrievalService.HybridSearchAsync(sessionId, request.Prompt, 10, ct);
            foreach (var e in evidence)
            {
                inputTexts.Add((e.SourceId, e.Chunk.Text));
            }

            AddReplay(job, "gather", "Inputs Gathered", $"Found {inputTexts.Count} input sources");

            // 2. Fuse
            job.State = JobState.Drafting;
            db.SaveJob(job);

            var modeInstruction = request.Mode switch
            {
                FusionMode.Blend => "Blend the inputs into a cohesive proposal that combines the best elements.",
                FusionMode.CrossApply => "Cross-apply concepts from one input domain to another to find novel combinations.",
                FusionMode.Substitute => "Identify elements that could be substituted with safer or more accessible alternatives.",
                FusionMode.Optimize => "Optimize the combined approach for efficiency, safety, and practicality.",
                _ => "Combine the inputs into a coherent proposal."
            };

            var fusionPrompt = $@"You are a research fusion engine. {modeInstruction}

Prompt: {request.Prompt}

Input Sources:
{string.Join("\n---\n", inputTexts.Select((t, i) => $"[Source {i + 1}: {t.sourceId}]\n{t.text[..Math.Min(2000, t.text.Length)]}..."))}

Requirements:
1. Produce a fused proposal
2. For each claim in the proposal, indicate which input source it derives from (provenance)
3. Flag any safety concerns
4. Flag any IP/licensing concerns if software is involved
5. Clearly label hypotheses vs evidence-backed claims

Format:
## Fused Proposal
[Your proposal with inline source references like (Source 1), (Source 2)]

## Provenance Map
- [Claim 1]: Source N
- [Claim 2]: Source M

## Safety Notes
[Any safety concerns]

## IP Notes
[Any IP concerns, or 'None identified']";

            var fusionResponse = await _llmService.GenerateAsync(fusionPrompt, ct: ct);

            // 3. Parse and create result
            var fusionResult = new FusionResult
            {
                SessionId = sessionId,
                JobId = job.Id,
                Mode = request.Mode,
                Proposal = ExtractSection(fusionResponse, "Fused Proposal") ?? fusionResponse,
            };

            // Parse provenance map
            var provenanceSection = ExtractSection(fusionResponse, "Provenance Map");
            if (provenanceSection != null)
            {
                var lines = provenanceSection.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        var claim = parts[0].Trim().TrimStart('-', ' ', '[').TrimEnd(']');
                        var source = parts[1].Trim();
                        fusionResult.ProvenanceMap[claim] = source;
                    }
                }
            }

            // Create citations
            foreach (var input in inputTexts.Take(10))
            {
                var citation = new Citation
                {
                    SessionId = sessionId,
                    JobId = job.Id,
                    Type = CitationType.File,
                    SourceId = input.sourceId,
                    Excerpt = input.text[..Math.Min(150, input.text.Length)],
                    Label = $"[Source: {input.sourceId[..Math.Min(8, input.sourceId.Length)]}]"
                };
                db.SaveCitation(citation);
                fusionResult.CitationIds.Add(citation.Id);
            }

            // Parse safety notes
            var safetySection = ExtractSection(fusionResponse, "Safety Notes");
            if (!string.IsNullOrWhiteSpace(safetySection) && !safetySection.Contains("None"))
            {
                fusionResult.SafetyNotes = new SafetyAssessment
                {
                    SessionId = sessionId,
                    SourceId = job.Id,
                    Notes = safetySection,
                    Level = safetySection.Contains("high", StringComparison.OrdinalIgnoreCase) ? SafetyLevel.High :
                            safetySection.Contains("medium", StringComparison.OrdinalIgnoreCase) ? SafetyLevel.Medium :
                            SafetyLevel.Low
                };
            }

            // Parse IP notes
            var ipSection = ExtractSection(fusionResponse, "IP Notes");
            if (!string.IsNullOrWhiteSpace(ipSection) && !ipSection.Contains("None identified"))
            {
                fusionResult.IpNotes = new IpAssessment
                {
                    SessionId = sessionId,
                    SourceId = job.Id,
                    Notes = ipSection,
                    UncertaintyLevel = "Moderate"
                };
            }

            db.SaveFusionResult(fusionResult);
            AddReplay(job, "fusion", "Fusion Complete",
                $"Mode: {request.Mode}\nProvenance entries: {fusionResult.ProvenanceMap.Count}");

            // 4. Generate export
            var exportContent = GenerateFusionExport(request, fusionResult);
            var exportPath = Path.Combine(
                _sessionManager.GetSession(sessionId)!.WorkspacePath, "Exports",
                $"{job.Id}_fusion.md");
            await File.WriteAllTextAsync(exportPath, exportContent, ct);

            var report = new Report
            {
                SessionId = sessionId,
                JobId = job.Id,
                ReportType = "fusion",
                Title = $"Idea Fusion - {request.Prompt}",
                Content = exportContent,
                FilePath = exportPath
            };
            db.SaveReport(report);

            job.FullReport = exportContent;
            job.ExecutiveSummary = $"# Idea Fusion ({request.Mode})\n\n{request.Prompt}\n\n" +
                $"Fused {inputTexts.Count} sources with {fusionResult.ProvenanceMap.Count} provenance points.";
            job.State = JobState.Completed;
            job.CompletedUtc = DateTime.UtcNow;
            db.SaveJob(job);

            AddReplay(job, "complete", "Fusion Export Ready", "Report exported successfully");
            db.SaveJob(job);

            return job;
        }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.ErrorMessage = ex.Message;
            db.SaveJob(job);
            return job;
        }
    }

    private static string? ExtractSection(string text, string sectionName)
    {
        var pattern = $"## {sectionName}";
        var idx = text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = idx + pattern.Length;
        var nextSection = text.IndexOf("\n## ", start);
        return nextSection > 0 ? text[start..nextSection].Trim() : text[start..].Trim();
    }

    private static string GenerateFusionExport(FusionRequest request, FusionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Idea Fusion Export");
        sb.AppendLine($"\n**Mode:** {result.Mode}");
        sb.AppendLine($"**Prompt:** {request.Prompt}");
        sb.AppendLine($"**Generated:** {result.CreatedUtc:yyyy-MM-dd HH:mm UTC}");

        sb.AppendLine("\n## Fused Proposal");
        sb.AppendLine(result.Proposal);

        sb.AppendLine("\n## Provenance Map");
        foreach (var kv in result.ProvenanceMap)
            sb.AppendLine($"- **{kv.Key}** ← {kv.Value}");

        if (result.SafetyNotes != null)
        {
            sb.AppendLine("\n## Safety Notes");
            sb.AppendLine($"- Level: {result.SafetyNotes.Level}");
            sb.AppendLine($"- {result.SafetyNotes.Notes}");
        }

        if (result.IpNotes != null)
        {
            sb.AppendLine("\n## IP Notes");
            sb.AppendLine(result.IpNotes.Notes);
        }

        sb.AppendLine("\n## Citations");
        foreach (var cid in result.CitationIds)
            sb.AppendLine($"- {cid}");

        return sb.ToString();
    }

    private static void AddReplay(ResearchJob job, string type, string title, string description)
    {
        job.ReplayEntries.Add(new ReplayEntry
        {
            Order = job.ReplayEntries.Count + 1,
            Title = title, Description = description, EntryType = type
        });
    }
}
