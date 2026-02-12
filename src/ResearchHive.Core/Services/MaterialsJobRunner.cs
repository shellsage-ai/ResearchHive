using ResearchHive.Core.Models;
using System.Text;

namespace ResearchHive.Core.Services;

/// <summary>
/// Materials Explorer: properties → ranked candidates with safety labels and citations
/// </summary>
public class MaterialsJobRunner
{
    private readonly SessionManager _sessionManager;
    private readonly RetrievalService _retrievalService;
    private readonly LlmService _llmService;
    private readonly ResearchJobRunner _researchRunner;

    public MaterialsJobRunner(SessionManager sessionManager, RetrievalService retrievalService,
        LlmService llmService, ResearchJobRunner researchRunner)
    {
        _sessionManager = sessionManager;
        _retrievalService = retrievalService;
        _llmService = llmService;
        _researchRunner = researchRunner;
    }

    public async Task<ResearchJob> RunAsync(string sessionId, MaterialsQuery query, CancellationToken ct = default)
    {
        var db = _sessionManager.GetSessionDb(sessionId);

        var prompt = BuildQueryDescription(query);
        var job = new ResearchJob
        {
            SessionId = sessionId,
            Type = JobType.Materials,
            Prompt = prompt,
            State = JobState.Planning
        };
        db.SaveJob(job);
        AddReplay(job, "start", "Materials Search Started", prompt);

        try
        {
            // 1. Research materials
            job.State = JobState.Searching;
            db.SaveJob(job);

            var researchJob = await _researchRunner.RunAsync(sessionId,
                $"materials and compounds with properties: {prompt}", JobType.Research, 5, ct);

            var evidence = await _retrievalService.HybridSearchAsync(sessionId, prompt, 15, ct);

            // 2. Generate candidates
            job.State = JobState.Drafting;
            db.SaveJob(job);

            var candidatePrompt = $@"You are a materials scientist. Given these target properties and evidence, 
suggest 5 ranked candidate materials/compounds.

For each candidate provide:
- Name
- Category (metal/polymer/ceramic/composite/additive/other)
- Fit rationale
- Key properties (matching the targets)
- Safety environment (desk/ventilated/fume hood/pro lab)
- Minimum PPE needed
- Known hazards
- Disposal notes
- DIY feasibility (easy/moderate/difficult/professional only)
- Testing checklist

Target Properties: {string.Join(", ", query.PropertyTargets.Select(kv => $"{kv.Key}: {kv.Value}"))}
Filters: {string.Join(", ", query.Filters.Select(kv => $"{kv.Key}: {kv.Value}"))}
Include: {string.Join(", ", query.IncludeMaterials)}
Avoid: {string.Join(", ", query.AvoidMaterials.Concat(query.AvoidHazards))}

Evidence:
{string.Join("\n", evidence.Take(5).Select(e => $"- {e.Chunk.Text[..Math.Min(200, e.Chunk.Text.Length)]}..."))}

Format each as:
### Rank N: [Name]
Category: ...
Fit: ...
Properties: key1=val1; key2=val2
Environment: ...
PPE: ...
Hazards: ...
Disposal: ...
DIY: ...
Tests: test1; test2; test3";

            var candidateResponse = await _llmService.GenerateAsync(candidatePrompt, ct: ct);
            var candidates = ParseCandidates(candidateResponse, sessionId, job.Id);

            // Create citations for candidates
            foreach (var candidate in candidates)
            {
                var matchingEvidence = evidence
                    .Where(e => e.Chunk.Text.Contains(candidate.Name, StringComparison.OrdinalIgnoreCase) ||
                                candidate.FitRationale.Split(' ').Any(w =>
                                    w.Length > 4 && e.Chunk.Text.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    .Take(2);

                foreach (var ev in matchingEvidence)
                {
                    var citation = new Citation
                    {
                        SessionId = sessionId,
                        JobId = job.Id,
                        Type = ev.SourceType == "snapshot" ? CitationType.WebSnapshot : CitationType.File,
                        SourceId = ev.SourceId,
                        ChunkId = ev.Chunk.Id,
                        Excerpt = ev.Chunk.Text[..Math.Min(150, ev.Chunk.Text.Length)],
                        Label = $"[{candidate.CitationIds.Count + 1}]"
                    };
                    db.SaveCitation(citation);
                    candidate.CitationIds.Add(citation.Id);
                }

                db.SaveMaterialCandidate(candidate);

                if (candidate.Safety != null)
                    db.SaveSafetyAssessment(candidate.Safety);

                AddReplay(job, "candidate", $"Candidate: {candidate.Name}",
                    $"Rank: {candidate.Rank}\nFit Score: {candidate.FitScore:F2}\n{candidate.FitRationale}");
            }

            // 3. Generate export
            var exportContent = GenerateMaterialsExport(query, candidates);
            var exportPath = Path.Combine(
                _sessionManager.GetSession(sessionId)!.WorkspacePath, "Exports",
                $"{job.Id}_materials.md");
            await File.WriteAllTextAsync(exportPath, exportContent, ct);

            var report = new Report
            {
                SessionId = sessionId, JobId = job.Id, ReportType = "materials",
                Title = $"Materials Explorer - {prompt}", Content = exportContent,
                FilePath = exportPath
            };
            db.SaveReport(report);

            job.FullReport = exportContent;
            job.ExecutiveSummary = $"# Materials Explorer\n\n{prompt}\n\n" +
                $"Found {candidates.Count} candidates. Top: {candidates.FirstOrDefault()?.Name ?? "N/A"}";
            job.State = JobState.Completed;
            job.CompletedUtc = DateTime.UtcNow;
            db.SaveJob(job);

            AddReplay(job, "complete", "Materials Search Complete", $"Found {candidates.Count} candidates");
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

    private static List<MaterialCandidate> ParseCandidates(string response, string sessionId, string jobId)
    {
        var candidates = new List<MaterialCandidate>();
        var sections = response.Split("### Rank", StringSplitOptions.RemoveEmptyEntries);

        int rank = 1;
        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section)) continue;

            var name = ExtractValue(section, ":", "\n")?.Trim('[', ']', ' ') ?? $"Material {rank}";
            var envStr = ExtractValue(section, "Environment:", "\n");

            var candidate = new MaterialCandidate
            {
                SessionId = sessionId,
                JobId = jobId,
                Name = name,
                Category = ExtractValue(section, "Category:", "\n") ?? "Other",
                FitRationale = ExtractValue(section, "Fit:", "\n") ?? "Matches target properties",
                DiyFeasibility = ExtractValue(section, "DIY:", "\n") ?? "Moderate",
                FitScore = 1.0 - (rank - 1) * 0.15,
                Rank = rank,
                Safety = new SafetyAssessment
                {
                    SessionId = sessionId,
                    SourceId = name,
                    RecommendedEnvironment = envStr ?? "Ventilated area",
                    MinimumPPE = ParseList(ExtractValue(section, "PPE:", "\n")),
                    Hazards = ParseList(ExtractValue(section, "Hazards:", "\n")),
                    DisposalNotes = ParseList(ExtractValue(section, "Disposal:", "\n")),
                    Level = envStr?.Contains("pro lab", StringComparison.OrdinalIgnoreCase) == true ? SafetyLevel.High :
                            envStr?.Contains("fume hood", StringComparison.OrdinalIgnoreCase) == true ? SafetyLevel.Medium :
                            SafetyLevel.Low
                },
                TestChecklist = ParseList(ExtractValue(section, "Tests:", "\n"))
            };

            var propsStr = ExtractValue(section, "Properties:", "\n");
            if (propsStr != null)
            {
                foreach (var prop in propsStr.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = prop.Split('=', 2);
                    if (parts.Length == 2)
                        candidate.Properties[parts[0].Trim()] = parts[1].Trim();
                }
            }

            candidates.Add(candidate);
            rank++;
        }

        // Ensure minimum candidates
        while (candidates.Count < 3)
        {
            candidates.Add(new MaterialCandidate
            {
                SessionId = sessionId,
                JobId = jobId,
                Name = $"Candidate Material {candidates.Count + 1}",
                Category = "Composite",
                FitRationale = "General-purpose material matching partial requirements",
                DiyFeasibility = "Moderate",
                FitScore = 0.5,
                Rank = candidates.Count + 1,
                Safety = new SafetyAssessment
                {
                    SessionId = sessionId,
                    SourceId = $"Candidate {candidates.Count + 1}",
                    RecommendedEnvironment = "Ventilated area",
                    MinimumPPE = new() { "Safety glasses", "Gloves" },
                    Hazards = new() { "Review SDS before handling" },
                    DisposalNotes = new() { "Follow local regulations" },
                    Level = SafetyLevel.Low
                },
                TestChecklist = new() { "Verify physical properties", "Test chemical compatibility", "Assess long-term stability" }
            });
        }

        return candidates;
    }

    private static string? ExtractValue(string text, string start, string end)
    {
        var idx = text.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        idx += start.Length;
        var endIdx = text.IndexOf(end, idx);
        return endIdx > idx ? text[idx..endIdx].Trim() : text[idx..].Trim();
    }

    private static List<string> ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new() { "Not specified" };
        return value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    private static string BuildQueryDescription(MaterialsQuery query)
    {
        var parts = new List<string>();
        if (query.PropertyTargets.Any())
            parts.Add($"Properties: {string.Join(", ", query.PropertyTargets.Select(kv => $"{kv.Key}={kv.Value}"))}");
        if (query.Filters.Any())
            parts.Add($"Filters: {string.Join(", ", query.Filters.Select(kv => $"{kv.Key}={kv.Value}"))}");
        if (query.AvoidMaterials.Any())
            parts.Add($"Avoid: {string.Join(", ", query.AvoidMaterials)}");
        return string.Join("; ", parts);
    }

    private static string GenerateMaterialsExport(MaterialsQuery query, List<MaterialCandidate> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Materials Explorer Results");
        sb.AppendLine($"\n## Query");
        foreach (var kv in query.PropertyTargets)
            sb.AppendLine($"- **{kv.Key}:** {kv.Value}");

        sb.AppendLine("\n## Ranked Candidates\n");
        sb.AppendLine("| Rank | Material | Category | Fit Score | Safety | DIY |");
        sb.AppendLine("|------|----------|----------|-----------|--------|-----|");
        foreach (var c in candidates)
        {
            sb.AppendLine($"| {c.Rank} | {c.Name} | {c.Category} | {c.FitScore:F2} | " +
                $"{c.Safety?.Level.ToString() ?? "?"} | {c.DiyFeasibility} |");
        }

        foreach (var c in candidates)
        {
            sb.AppendLine($"\n### #{c.Rank}: {c.Name}");
            sb.AppendLine($"**Category:** {c.Category}");
            sb.AppendLine($"**Fit Rationale:** {c.FitRationale}");
            if (c.Properties.Any())
            {
                sb.AppendLine("**Properties:**");
                foreach (var kv in c.Properties)
                    sb.AppendLine($"  - {kv.Key}: {kv.Value}");
            }
            sb.AppendLine($"\n**Safety:**");
            sb.AppendLine($"  - Environment: {c.Safety?.RecommendedEnvironment ?? "N/A"}");
            sb.AppendLine($"  - PPE: {string.Join(", ", c.Safety?.MinimumPPE ?? new())}");
            sb.AppendLine($"  - Hazards: {string.Join(", ", c.Safety?.Hazards ?? new())}");
            sb.AppendLine($"  - Disposal: {string.Join(", ", c.Safety?.DisposalNotes ?? new())}");
            sb.AppendLine($"\n**DIY Feasibility:** {c.DiyFeasibility}");
            sb.AppendLine("**Test Checklist:**");
            foreach (var t in c.TestChecklist)
                sb.AppendLine($"  - [ ] {t}");
        }

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
