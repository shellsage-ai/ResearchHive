using ResearchHive.Core.Models;
using System.Text;

namespace ResearchHive.Core.Services;

/// <summary>
/// Programming Research + IP Studio: approach matrix, IP summary, design-around options
/// </summary>
public class ProgrammingJobRunner
{
    private readonly SessionManager _sessionManager;
    private readonly RetrievalService _retrievalService;
    private readonly LlmService _llmService;
    private readonly ResearchJobRunner _researchRunner;

    public ProgrammingJobRunner(SessionManager sessionManager, RetrievalService retrievalService,
        LlmService llmService, ResearchJobRunner researchRunner)
    {
        _sessionManager = sessionManager;
        _retrievalService = retrievalService;
        _llmService = llmService;
        _researchRunner = researchRunner;
    }

    public async Task<ResearchJob> RunAsync(string sessionId, string problem, CancellationToken ct = default)
    {
        var db = _sessionManager.GetSessionDb(sessionId);

        var job = new ResearchJob
        {
            SessionId = sessionId,
            Type = JobType.ProgrammingIP,
            Prompt = problem,
            State = JobState.Planning
        };
        db.SaveJob(job);
        AddReplay(job, "start", "Programming Research Started", $"Problem: {problem}");

        try
        {
            // 1. Research approaches
            job.State = JobState.Searching;
            db.SaveJob(job);

            var researchJob = await _researchRunner.RunAsync(sessionId,
                $"software engineering approaches for: {problem}", JobType.Research, 5, ct);

            var evidence = await _retrievalService.HybridSearchAsync(sessionId, problem, 15, ct);

            // 2. Generate approach matrix
            job.State = JobState.Drafting;
            db.SaveJob(job);

            var matrixPrompt = $@"You are a senior software engineer. For this problem, generate an approach matrix with 3-4 approaches. For each approach provide:
- Name
- Description
- Pros (semicolon-separated)  
- Cons (semicolon-separated)
- Complexity (Low/Medium/High)
- Maturity (Emerging/Established/Mature)
- License concern level (None/Low/Medium/High)

Problem: {problem}

Evidence from research:
{string.Join("\n", evidence.Take(5).Select(e => $"- {e.Chunk.Text[..Math.Min(200, e.Chunk.Text.Length)]}..."))}

Format each approach as:
### [Name]
Description: ...
Pros: ...
Cons: ...
Complexity: ...
Maturity: ...
License: ...";

            var matrixResponse = await _llmService.GenerateAsync(matrixPrompt, ct: ct);

            var approaches = ParseApproaches(matrixResponse, sessionId);
            var citations = db.GetCitations(researchJob.Id);

            // Create citations for the approach entries
            foreach (var approach in approaches)
            {
                var matchingEvidence = evidence
                    .Where(e => e.Chunk.Text.Contains(approach.Name, StringComparison.OrdinalIgnoreCase) ||
                                approach.Description.Split(' ').Any(w =>
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
                        Label = $"[{approach.CitationIds.Count + 1}]"
                    };
                    db.SaveCitation(citation);
                    approach.CitationIds.Add(citation.Id);
                }
            }

            // 3. IP/License analysis
            foreach (var approach in approaches)
            {
                approach.IpInfo = new IpAssessment
                {
                    SessionId = sessionId,
                    SourceId = approach.Id,
                    LicenseSignal = approach.Evaluation.TryGetValue("License", out var lic) ? lic : "Unknown",
                    UncertaintyLevel = "Moderate",
                    RiskFlags = DetermineRiskFlags(approach),
                    DesignAroundOptions = GenerateDesignArounds(approach),
                    Notes = "This is not legal advice. Surface signals for awareness."
                };
                db.SaveIpAssessment(approach.IpInfo);
            }

            // 4. Select recommended approach
            var recommended = approaches.OrderByDescending(a =>
                (a.Evaluation.TryGetValue("Complexity", out var c) && c == "Low" ? 3 : c == "Medium" ? 2 : 1) +
                (a.Evaluation.TryGetValue("Maturity", out var m) && m == "Mature" ? 3 : m == "Established" ? 2 : 1) +
                (a.IpInfo?.LicenseSignal?.Contains("None") == true ? 3 : 1)
            ).First();
            recommended.IsRecommended = true;

            // 5. Generate implementation plan
            var planPrompt = $@"Generate an original implementation plan (not copying any external code) for:

Problem: {problem}
Approach: {recommended.Name} - {recommended.Description}

The plan should:
- Use standards/specs and permissive libraries where possible
- Be implementable from first principles
- Include architecture decisions
- Reference specific standards/specs where applicable

Do NOT reproduce any verbatim code from external sources.";

            var implementationPlan = await _llmService.GenerateAsync(planPrompt, ct: ct);

            // 6. Build result
            var result = new ProgrammingResearchResult
            {
                SessionId = sessionId,
                JobId = job.Id,
                ApproachMatrix = approaches,
                RecommendedApproach = recommended.Name,
                Rationale = recommended.Rationale,
                ImplementationPlan = implementationPlan,
                IpSummary = approaches.Where(a => a.IpInfo != null).Select(a => a.IpInfo!).ToList(),
                DesignAroundOptions = approaches.SelectMany(a => a.IpInfo?.DesignAroundOptions ?? new()).Distinct().ToList()
            };

            // 7. Generate export
            var exportContent = GenerateProgrammingExport(problem, result);
            var exportPath = Path.Combine(
                _sessionManager.GetSession(sessionId)!.WorkspacePath, "Exports",
                $"{job.Id}_programming_ip.md");
            await File.WriteAllTextAsync(exportPath, exportContent, ct);

            var report = new Report
            {
                SessionId = sessionId, JobId = job.Id, ReportType = "programming_ip",
                Title = $"Programming Research + IP - {problem}",
                Content = exportContent, FilePath = exportPath
            };
            db.SaveReport(report);

            job.FullReport = exportContent;
            job.ExecutiveSummary = $"# Programming Research: {problem}\n\nRecommended: {recommended.Name}\n{recommended.Rationale}";
            job.MostSupportedView = $"Recommended approach: {recommended.Name}\n\n{recommended.Description}\n\n{recommended.Rationale}";
            job.CredibleAlternatives = string.Join("\n\n", approaches.Where(a => !a.IsRecommended)
                .Select(a => $"**{a.Name}:** {a.Description}"));
            job.State = JobState.Completed;
            job.CompletedUtc = DateTime.UtcNow;
            db.SaveJob(job);

            AddReplay(job, "complete", "Programming Research Complete",
                $"Generated approach matrix with {approaches.Count} approaches");
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

    private static List<ApproachEntry> ParseApproaches(string response, string sessionId)
    {
        var approaches = new List<ApproachEntry>();
        var sections = response.Split("###", StringSplitOptions.RemoveEmptyEntries);

        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section)) continue;

            var lines = section.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var name = lines.FirstOrDefault()?.Trim().Trim('[', ']') ?? $"Approach {approaches.Count + 1}";

            var approach = new ApproachEntry
            {
                Name = name,
                Description = ExtractValue(section, "Description:"),
                Evaluation = new Dictionary<string, string>
                {
                    ["Pros"] = ExtractValue(section, "Pros:"),
                    ["Cons"] = ExtractValue(section, "Cons:"),
                    ["Complexity"] = ExtractValue(section, "Complexity:"),
                    ["Maturity"] = ExtractValue(section, "Maturity:"),
                    ["License"] = ExtractValue(section, "License:")
                },
                Rationale = $"Recommended based on {name}'s balance of complexity, maturity, and license safety."
            };

            approaches.Add(approach);
        }

        // Ensure minimum approaches
        while (approaches.Count < 3)
        {
            approaches.Add(new ApproachEntry
            {
                Name = approaches.Count == 1 ? "Standards-based approach" :
                       approaches.Count == 2 ? "First-principles approach" : "Clean-room approach",
                Description = "An approach built from standards and specifications without external dependencies",
                Evaluation = new()
                {
                    ["Pros"] = "No IP concerns; fully original",
                    ["Cons"] = "Higher initial development effort",
                    ["Complexity"] = "Medium",
                    ["Maturity"] = "Established",
                    ["License"] = "None"
                },
                Rationale = "Safe approach with no IP concerns"
            });
        }

        return approaches;
    }

    private static string ExtractValue(string text, string key)
    {
        var idx = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "Not specified";
        idx += key.Length;
        var endIdx = text.IndexOf('\n', idx);
        return endIdx > idx ? text[idx..endIdx].Trim() : text[idx..].Trim();
    }

    private static List<string> DetermineRiskFlags(ApproachEntry approach)
    {
        var flags = new List<string>();
        var license = approach.Evaluation.TryGetValue("License", out var lic) ? lic : "";

        if (license.Contains("High") || license.Contains("GPL") || license.Contains("AGPL"))
            flags.Add("Copyleft license may require derivative work disclosure");
        if (license.Contains("Medium") || license.Contains("LGPL"))
            flags.Add("License has conditions that need compliance review");
        if (license.Contains("Unknown"))
            flags.Add("License terms unclear - recommend review before use");
        if (approach.Description.Contains("proprietary", StringComparison.OrdinalIgnoreCase))
            flags.Add("Proprietary technology may have usage restrictions");

        return flags;
    }

    private static List<string> GenerateDesignArounds(ApproachEntry approach)
    {
        return new List<string>
        {
            "Implement from published standards/specifications (e.g., RFCs, ISO, W3C)",
            "Use permissive-licensed substitutes (MIT, Apache 2.0, BSD)",
            "Build from first principles using public domain algorithms",
            "Clean-room implementation: document requirements, implement independently",
            "Consider standards-compliant open source implementations"
        };
    }

    private static string GenerateProgrammingExport(string problem, ProgrammingResearchResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Programming Research + IP Report");
        sb.AppendLine($"\n## Problem\n{problem}");

        sb.AppendLine("\n## Approach Matrix\n");
        sb.AppendLine("| Approach | Complexity | Maturity | License | Recommended |");
        sb.AppendLine("|----------|-----------|----------|---------|-------------|");
        foreach (var a in result.ApproachMatrix)
        {
            sb.AppendLine($"| {a.Name} | {a.Evaluation.GetValueOrDefault("Complexity", "?")} | " +
                $"{a.Evaluation.GetValueOrDefault("Maturity", "?")} | " +
                $"{a.Evaluation.GetValueOrDefault("License", "?")} | {(a.IsRecommended ? "✓" : "")} |");
        }

        foreach (var a in result.ApproachMatrix)
        {
            sb.AppendLine($"\n### {a.Name}{(a.IsRecommended ? " ⭐ RECOMMENDED" : "")}");
            sb.AppendLine(a.Description);
            sb.AppendLine($"\n**Pros:** {a.Evaluation.GetValueOrDefault("Pros", "N/A")}");
            sb.AppendLine($"**Cons:** {a.Evaluation.GetValueOrDefault("Cons", "N/A")}");
        }

        sb.AppendLine("\n## Recommended Approach");
        sb.AppendLine($"**{result.RecommendedApproach}**\n{result.Rationale}");

        sb.AppendLine("\n## Implementation Plan");
        sb.AppendLine(result.ImplementationPlan);

        sb.AppendLine("\n## IP / License Summary");
        foreach (var ip in result.IpSummary)
        {
            sb.AppendLine($"\n### {ip.SourceId}");
            sb.AppendLine($"- **License Signal:** {ip.LicenseSignal}");
            sb.AppendLine($"- **Uncertainty:** {ip.UncertaintyLevel}");
            if (ip.RiskFlags.Any())
                sb.AppendLine($"- **Risk Flags:** {string.Join("; ", ip.RiskFlags)}");
        }

        sb.AppendLine("\n## Design-Around Options");
        foreach (var opt in result.DesignAroundOptions)
            sb.AppendLine($"- {opt}");

        sb.AppendLine("\n---\n*Note: This is not legal advice. IP/license signals are surfaced for awareness. Consult legal counsel for formal IP review.*");

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
