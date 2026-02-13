using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ResearchHive.Core.Services;

/// <summary>
/// Researches complementary projects for a RepoProfile by searching the web
/// for tools/libraries that fill identified gaps. Enforces a minimum of 5 complements.
/// Enriches GitHub URLs with real descriptions before LLM evaluation.
/// </summary>
public class ComplementResearchService
{
    private readonly BrowserSearchService _searchService;
    private readonly LlmService _llmService;
    private readonly HttpClient _http;

    /// <summary>Minimum number of complement suggestions to produce.</summary>
    public const int MinimumComplements = 5;

    public ComplementResearchService(BrowserSearchService searchService, LlmService llmService, AppSettings settings)
    {
        _searchService = searchService;
        _llmService = llmService;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ResearchHive", "1.0"));
        if (!string.IsNullOrEmpty(settings.GitHubPat))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.GitHubPat);
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
            var query = $"{profile.PrimaryLanguage} {topic} library github stars:>100";
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

        // Enrich GitHub URLs with real descriptions before LLM evaluation
        var enrichedResults = new List<(string topic, List<(string url, string description)> entries)>();
        foreach (var (topic, urls) in searchResults)
        {
            var entries = new List<(string url, string description)>();
            foreach (var url in urls)
            {
                var desc = await EnrichGitHubUrlAsync(url, ct);
                entries.Add((url, desc));
            }
            enrichedResults.Add((topic, entries));
        }

        // Build LLM prompt to evaluate the discovered projects
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"I'm analyzing the GitHub repo **{profile.Owner}/{profile.Name}** ({profile.PrimaryLanguage}).");
        sb.AppendLine($"Purpose: {profile.Description}");
        sb.AppendLine();
        sb.AppendLine("I found these potential complementary projects from web search.");
        sb.AppendLine("Each URL includes a description where available — use these descriptions for accuracy.");
        sb.AppendLine("Do NOT invent or hallucinate project names or descriptions. Only use information provided here.");
        sb.AppendLine();
        foreach (var (topic, entries) in enrichedResults)
        {
            sb.AppendLine($"### Topic: {topic}");
            foreach (var (url, desc) in entries)
            {
                if (!string.IsNullOrEmpty(desc))
                    sb.AppendLine($"  - {url} — {desc}");
                else
                    sb.AppendLine($"  - {url}");
            }
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
            "Be specific about what each adds. Ensure diversity across categories. " +
            "IMPORTANT: Use ONLY the project names and descriptions provided in the search results. " +
            "Do NOT invent project names that don't appear in the URLs. " +
            "If a URL is from GitHub, derive the project name from the URL path (e.g., github.com/owner/repo → repo).",
            ct: ct);

        complements = ParseComplements(response);
        return complements;
    }

    /// <summary>
    /// Fetches a short description for GitHub URLs via the GitHub API.
    /// Returns empty string for non-GitHub URLs or on failure.
    /// </summary>
    private async Task<string> EnrichGitHubUrlAsync(string url, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return string.Empty;
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return string.Empty;

            // Extract owner/repo from path: /owner/repo[/...]
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) return string.Empty;
            var owner = segments[0];
            var repo = segments[1];

            var apiUrl = $"https://api.github.com/repos/{owner}/{repo}";
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return string.Empty;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var parts = new List<string>();
            if (root.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
            {
                var desc = descEl.GetString();
                if (!string.IsNullOrWhiteSpace(desc)) parts.Add(desc);
            }
            if (root.TryGetProperty("stargazers_count", out var starsEl))
                parts.Add($"{starsEl.GetInt32():N0} stars");
            if (root.TryGetProperty("license", out var licEl) && licEl.ValueKind == JsonValueKind.Object &&
                licEl.TryGetProperty("spdx_id", out var spdxEl) && spdxEl.ValueKind == JsonValueKind.String)
            {
                var spdx = spdxEl.GetString();
                if (!string.IsNullOrWhiteSpace(spdx) && spdx != "NOASSERTION") parts.Add($"License: {spdx}");
            }

            return string.Join(" | ", parts);
        }
        catch
        {
            return string.Empty; // non-fatal
        }
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
