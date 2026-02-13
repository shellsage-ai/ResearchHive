using ResearchHive.Core.Models;

namespace ResearchHive.Core.Services;

/// <summary>
/// Researches complementary projects for a RepoProfile by searching the web
/// for tools/libraries that fill identified gaps. Enforces a minimum of 5 complements.
/// </summary>
public class ComplementResearchService
{
    private readonly BrowserSearchService _searchService;
    private readonly LlmService _llmService;

    /// <summary>Minimum number of complement suggestions to produce.</summary>
    public const int MinimumComplements = 5;

    public ComplementResearchService(BrowserSearchService searchService, LlmService llmService)
    {
        _searchService = searchService;
        _llmService = llmService;
    }

    public async Task<List<ComplementProject>> ResearchAsync(RepoProfile profile, CancellationToken ct = default)
    {
        var complements = new List<ComplementProject>();

        // Build search queries — use gaps if available, plus general improvement queries
        var searchTopics = new List<string>();
        foreach (var gap in profile.Gaps.Take(8))
            searchTopics.Add(gap);

        // If fewer than MinimumComplements gaps, add general improvement categories
        if (searchTopics.Count < MinimumComplements)
        {
            var generalCategories = new[]
            {
                "testing and quality assurance",
                "performance monitoring and observability",
                "security scanning and vulnerability detection",
                "documentation generation",
                "CI/CD and deployment automation",
                "code analysis and linting",
                "dependency management"
            };
            foreach (var cat in generalCategories)
            {
                if (searchTopics.Count >= MinimumComplements + 2) break; // search a few extra for redundancy
                if (!searchTopics.Any(s => s.Contains(cat.Split(' ')[0], StringComparison.OrdinalIgnoreCase)))
                    searchTopics.Add(cat);
            }
        }

        if (searchTopics.Count == 0) return complements;

        var searchResults = new List<(string topic, List<string> urls)>();
        foreach (var topic in searchTopics)
        {
            if (ct.IsCancellationRequested) break;
            var query = $"best {profile.PrimaryLanguage} {topic} library tool for {profile.Description}";
            try
            {
                var urls = await _searchService.SearchAsync(query, "DuckDuckGo",
                    "https://duckduckgo.com/?q={query}", ct: ct);
                searchResults.Add((topic, urls.Take(5).ToList()));
            }
            catch
            {
                // Fall through — search failures are non-fatal
            }
        }

        if (searchResults.Count == 0) return complements;

        // Build LLM prompt to evaluate the discovered projects
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"I'm analyzing the GitHub repo **{profile.Owner}/{profile.Name}** ({profile.PrimaryLanguage}).");
        sb.AppendLine($"Purpose: {profile.Description}");
        sb.AppendLine();
        sb.AppendLine("I found these potential complementary projects from web search:");
        sb.AppendLine();
        foreach (var (topic, urls) in searchResults)
        {
            sb.AppendLine($"### Topic: {topic}");
            foreach (var url in urls)
                sb.AppendLine($"  - {url}");
            sb.AppendLine();
        }
        sb.AppendLine($"Identify at least {MinimumComplements} complementary projects from the URLs above, ranked by relevance.");
        sb.AppendLine("Pick the BEST option per topic. If multiple topics yield the same project, pick additional alternatives.");
        sb.AppendLine("For each, respond in this exact format:");
        sb.AppendLine();
        sb.AppendLine("## Complement");
        sb.AppendLine("- Name: <project name>");
        sb.AppendLine("- Url: <project url>");
        sb.AppendLine("- Purpose: <one sentence>");
        sb.AppendLine("- WhatItAdds: <what it specifically adds to the target repo>");
        sb.AppendLine("- Category: <Testing|Performance|Security|Documentation|Monitoring|DevOps|UI|DataAccess|Other>");
        sb.AppendLine("- License: <license if known, else Unknown>");
        sb.AppendLine("- Maturity: <Mature|Growing|Early|Unknown>");

        var response = await _llmService.GenerateAsync(sb.ToString(),
            $"You are a software ecosystem analyst. Identify at least {MinimumComplements} complementary projects ranked by relevance. " +
            "Be specific about what each adds. Ensure diversity across categories.",
            ct: ct);

        complements = ParseComplements(response);
        return complements;
    }

    private static List<ComplementProject> ParseComplements(string response)
    {
        var results = new List<ComplementProject>();
        ComplementProject? current = null;

        foreach (var rawLine in response.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("## Complement", StringComparison.OrdinalIgnoreCase))
            {
                if (current != null && !string.IsNullOrEmpty(current.Name))
                    results.Add(current);
                current = new ComplementProject();
                continue;
            }
            if (current == null) continue;

            if (line.StartsWith("- Name:", StringComparison.OrdinalIgnoreCase))
                current.Name = line[7..].Trim();
            else if (line.StartsWith("- Url:", StringComparison.OrdinalIgnoreCase))
                current.Url = line[6..].Trim();
            else if (line.StartsWith("- Purpose:", StringComparison.OrdinalIgnoreCase))
                current.Purpose = line[10..].Trim();
            else if (line.StartsWith("- WhatItAdds:", StringComparison.OrdinalIgnoreCase))
                current.WhatItAdds = line[13..].Trim();
            else if (line.StartsWith("- Category:", StringComparison.OrdinalIgnoreCase))
                current.Category = line[11..].Trim();
            else if (line.StartsWith("- License:", StringComparison.OrdinalIgnoreCase))
                current.License = line[10..].Trim();
            else if (line.StartsWith("- Maturity:", StringComparison.OrdinalIgnoreCase))
                current.Maturity = line[11..].Trim();
        }
        if (current != null && !string.IsNullOrEmpty(current.Name))
            results.Add(current);

        return results;
    }
}
