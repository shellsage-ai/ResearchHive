using ResearchHive.Core.Models;

namespace ResearchHive.Core.Services;

/// <summary>
/// Researches complementary projects for a RepoProfile by searching the web
/// for tools/libraries that fill identified gaps.
/// </summary>
public class ComplementResearchService
{
    private readonly BrowserSearchService _searchService;
    private readonly LlmService _llmService;

    public ComplementResearchService(BrowserSearchService searchService, LlmService llmService)
    {
        _searchService = searchService;
        _llmService = llmService;
    }

    public async Task<List<ComplementProject>> ResearchAsync(RepoProfile profile, CancellationToken ct = default)
    {
        var complements = new List<ComplementProject>();
        if (profile.Gaps.Count == 0) return complements;

        // Group gaps into categories and search for each
        var gapBatches = profile.Gaps.Take(8).ToList(); // Limit to avoid too many searches

        var searchResults = new List<(string gap, List<string> urls)>();
        foreach (var gap in gapBatches)
        {
            if (ct.IsCancellationRequested) break;
            var query = $"best {profile.PrimaryLanguage} {gap} library tool for {profile.Description}";
            try
            {
                var urls = await _searchService.SearchAsync(query, "DuckDuckGo",
                    "https://duckduckgo.com/?q={query}", ct: ct);
                searchResults.Add((gap, urls.Take(5).ToList()));
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
        foreach (var (gap, urls) in searchResults)
        {
            sb.AppendLine($"### Gap: {gap}");
            foreach (var url in urls)
                sb.AppendLine($"  - {url}");
            sb.AppendLine();
        }
        sb.AppendLine("For each gap, identify the BEST complementary project from the URLs above (pick one per gap).");
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
            "You are a software ecosystem analyst. Identify the best complementary project for each gap. Be specific about what each adds.",
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
