using ResearchHive.Core.Models;
using System.Text;
using System.Text.Json;

namespace ResearchHive.Core.Services;

/// <summary>
/// Discovery Studio: generates idea cards with novelty checks, scoring, and export
/// </summary>
public class DiscoveryJobRunner
{
    private readonly SessionManager _sessionManager;
    private readonly RetrievalService _retrievalService;
    private readonly LlmService _llmService;
    private readonly ResearchJobRunner _researchRunner;

    public DiscoveryJobRunner(SessionManager sessionManager, RetrievalService retrievalService,
        LlmService llmService, ResearchJobRunner researchRunner)
    {
        _sessionManager = sessionManager;
        _retrievalService = retrievalService;
        _llmService = llmService;
        _researchRunner = researchRunner;
    }

    public async Task<ResearchJob> RunAsync(string sessionId, string problem, string constraints = "",
        int ideaCount = 3, CancellationToken ct = default)
    {
        var db = _sessionManager.GetSessionDb(sessionId);

        var job = new ResearchJob
        {
            SessionId = sessionId,
            Type = JobType.Discovery,
            Prompt = problem,
            State = JobState.Planning,
            TargetSourceCount = ideaCount
        };
        db.SaveJob(job);
        AddReplay(job, "start", "Discovery Started", $"Problem: {problem}\nConstraints: {constraints}");

        try
        {
            // 1. Problem framing + known map
            job.State = JobState.Searching;
            db.SaveJob(job);

            // Research existing knowledge
            var existingJob = await _researchRunner.RunAsync(sessionId, 
                $"existing solutions and approaches for: {problem}", JobType.Research, 3, ct);

            var evidenceResults = await _retrievalService.HybridSearchAsync(sessionId, problem, 10, ct);
            var knownMap = BuildKnownMap(evidenceResults);
            AddReplay(job, "known_map", "Known Map Built", knownMap);

            // 2. Generate idea cards
            job.State = JobState.Drafting;
            db.SaveJob(job);

            var ideaPrompt = $@"Generate {ideaCount} novel idea cards for this problem. For each idea, provide:
- Title
- Hypothesis  
- Mechanism (how it would work)
- Minimal Safe Test Plan
- Risks
- Falsification criteria (what would prove it wrong)

Problem: {problem}
Constraints: {constraints}

Known approaches (avoid duplicating these):
{knownMap}

Format each idea as:
### Idea N: [Title]
**Hypothesis:** ...
**Mechanism:** ...  
**Test Plan:** ...
**Risks:** ...
**Falsification:** ...";

            var ideaResponse = await _llmService.GenerateAsync(ideaPrompt, ct: ct);
            var ideaCards = ParseIdeaCards(ideaResponse, sessionId, job.Id);

            // 3. Novelty sanity-check for each card — parallel for speed
            var noveltyTasks = ideaCards.Select(async card =>
            {
                // Search for prior art
                var priorArtResults = await _retrievalService.HybridSearchAsync(sessionId, card.Hypothesis, 5, ct);
                
                if (priorArtResults.Any())
                {
                    card.NearestPriorArt = priorArtResults.First().Chunk.Text.Length > 300
                        ? priorArtResults.First().Chunk.Text[..300] + "..." 
                        : priorArtResults.First().Chunk.Text;
                    card.NoveltyCheck = priorArtResults.First().Score > 0.7
                        ? "LOW NOVELTY - Similar approach found in existing literature"
                        : priorArtResults.First().Score > 0.4
                            ? "MODERATE NOVELTY - Related work exists but approach differs"
                            : "HIGH NOVELTY - No closely matching prior art found";

                    card.PriorArtCitationIds = priorArtResults
                        .Select(r => r.Chunk.Id)
                        .Take(3)
                        .ToList();
                }
                else
                {
                    card.NoveltyCheck = "HIGH NOVELTY - No prior art found in indexed sources";
                }

                // 4. Score card
                card.ScoreBreakdown = new Dictionary<string, double>
                {
                    ["Novelty"] = card.NoveltyCheck?.Contains("HIGH") == true ? 0.9 :
                                  card.NoveltyCheck?.Contains("MODERATE") == true ? 0.6 : 0.3,
                    ["Feasibility"] = string.IsNullOrEmpty(card.MinimalTestPlan) ? 0.3 : 0.7,
                    ["Safety"] = card.Risks.Count > 3 ? 0.4 : 0.8,
                    ["Testability"] = string.IsNullOrEmpty(card.Falsification) ? 0.3 : 0.8,
                    ["Impact"] = 0.7, // Default moderate impact
                };
                card.Score = card.ScoreBreakdown.Values.Average();
            }).ToList();

            await Task.WhenAll(noveltyTasks);

            // Save cards and replay entries after parallel completion
            foreach (var card in ideaCards)
            {
                AddReplay(job, "novelty", $"Novelty Check: {card.Title}", card.NoveltyCheck ?? "");
                db.SaveIdeaCard(card);
                AddReplay(job, "idea", $"Idea Card: {card.Title}", 
                    $"Score: {card.Score:F2}\nHypothesis: {card.Hypothesis}");
            }

            // 5. Generate export
            var exportContent = GenerateDiscoveryExport(problem, constraints, knownMap, ideaCards);
            var exportPath = Path.Combine(
                _sessionManager.GetSession(sessionId)!.WorkspacePath, "Exports",
                $"{job.Id}_discovery.md");
            await File.WriteAllTextAsync(exportPath, exportContent, ct);

            var report = new Report
            {
                SessionId = sessionId, JobId = job.Id, ReportType = "discovery",
                Title = $"Discovery Studio - {problem}", Content = exportContent,
                FilePath = exportPath
            };
            db.SaveReport(report);

            job.FullReport = exportContent;
            job.ExecutiveSummary = $"# Discovery: {problem}\n\nGenerated {ideaCards.Count} ideas. " +
                $"Top: {ideaCards.OrderByDescending(c => c.Score).FirstOrDefault()?.Title ?? "N/A"}";
            job.State = JobState.Completed;
            job.CompletedUtc = DateTime.UtcNow;
            db.SaveJob(job);

            AddReplay(job, "complete", "Discovery Complete", $"Generated {ideaCards.Count} idea cards");
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

    private List<IdeaCard> ParseIdeaCards(string response, string sessionId, string jobId)
    {
        var cards = new List<IdeaCard>();
        var sections = response.Split("### Idea", StringSplitOptions.RemoveEmptyEntries);

        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section)) continue;

            var card = new IdeaCard
            {
                SessionId = sessionId,
                JobId = jobId,
                Title = ExtractField(section, ":", "\n") ?? $"Idea {cards.Count + 1}",
                Hypothesis = ExtractField(section, "Hypothesis:", "\n") ?? "Hypothesis to be refined",
                Mechanism = ExtractField(section, "Mechanism:", "\n") ?? "Mechanism to be detailed",
                MinimalTestPlan = ExtractField(section, "Test Plan:", "\n") ?? "Test plan to be developed",
                Falsification = ExtractField(section, "Falsification:", "\n") ?? "Criteria to be defined",
            };

            var risks = ExtractField(section, "Risks:", "\n");
            card.Risks = risks?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Trim()).ToList() ?? new List<string> { "To be assessed" };

            cards.Add(card);
        }

        // Ensure we have at least the requested number
        while (cards.Count < 3)
        {
            cards.Add(new IdeaCard
            {
                SessionId = sessionId,
                JobId = jobId,
                Title = $"Generated Idea {cards.Count + 1}",
                Hypothesis = "Novel hypothesis exploring alternative approaches",
                Mechanism = "Mechanism leveraging existing knowledge gaps",
                MinimalTestPlan = "1. Define metrics, 2. Small-scale test, 3. Evaluate results",
                Risks = new() { "Feasibility uncertain", "May require iteration" },
                Falsification = "Compare results against baseline metrics"
            });
        }

        return cards;
    }

    private static string? ExtractField(string text, string start, string end)
    {
        var idx = text.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        idx += start.Length;
        var endIdx = text.IndexOf(end, idx);
        return endIdx > idx ? text[idx..endIdx].Trim().Trim('*') : text[idx..].Trim().Trim('*');
    }

    private static string BuildKnownMap(List<RetrievalResult> results)
    {
        var sb = new StringBuilder();
        foreach (var r in results.Take(5))
        {
            sb.AppendLine($"- {r.Chunk.Text.Substring(0, Math.Min(200, r.Chunk.Text.Length))}...");
        }
        return sb.Length > 0 ? sb.ToString() : "No existing knowledge indexed yet.";
    }

    private static string GenerateDiscoveryExport(string problem, string constraints,
        string knownMap, List<IdeaCard> cards)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Discovery Studio Export");
        sb.AppendLine($"\n## Problem\n{problem}");
        if (!string.IsNullOrEmpty(constraints))
            sb.AppendLine($"\n## Constraints\n{constraints}");
        sb.AppendLine($"\n## Known Map\n{knownMap}");
        sb.AppendLine("\n## Idea Cards");

        foreach (var card in cards.OrderByDescending(c => c.Score))
        {
            sb.AppendLine($"\n### {card.Title} (Score: {card.Score:F2})");
            sb.AppendLine($"**Hypothesis:** {card.Hypothesis}");
            sb.AppendLine($"**Mechanism:** {card.Mechanism}");
            sb.AppendLine($"**Test Plan:** {card.MinimalTestPlan}");
            sb.AppendLine($"**Risks:** {string.Join(", ", card.Risks)}");
            sb.AppendLine($"**Falsification:** {card.Falsification}");
            sb.AppendLine($"**Novelty:** {card.NoveltyCheck ?? "Not assessed"}");
            if (card.ScoreBreakdown.Any())
            {
                sb.AppendLine("**Score Breakdown:**");
                foreach (var kv in card.ScoreBreakdown)
                    sb.AppendLine($"  - {kv.Key}: {kv.Value:F1}");
            }
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
