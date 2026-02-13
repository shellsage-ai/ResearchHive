using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ResearchHive.Core.Services;

/// <summary>
/// Researches complementary projects for a RepoProfile by searching the web
/// for tools/libraries that fill identified gaps. Enforces a minimum of 5 complements.
/// Enriches GitHub URLs with real descriptions before LLM evaluation.
/// Uses ModelTier.Mini for the complement evaluation call (routine ranking task).
/// </summary>
public class ComplementResearchService
{
    private readonly IBrowserSearchService _searchService;
    private readonly ILlmService _llmService;
    private readonly HttpClient _http;

    /// <summary>Minimum number of complement suggestions to produce.</summary>
    public const int MinimumComplements = 5;

    /// <summary>Number of web search calls in the last ResearchAsync invocation.</summary>
    public int LastSearchCallCount { get; private set; }

    /// <summary>Number of GitHub API enrichment calls in the last ResearchAsync invocation.</summary>
    public int LastEnrichCallCount { get; private set; }

    /// <summary>Duration of the LLM evaluation call in the last ResearchAsync invocation (ms).</summary>
    public long LastLlmDurationMs { get; private set; }

    public ComplementResearchService(IBrowserSearchService searchService, ILlmService llmService, AppSettings settings)
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
        LastSearchCallCount = 0;
        LastEnrichCallCount = 0;
        LastLlmDurationMs = 0;

        // Build search queries — use gaps if available, plus general improvement queries
        var searchTopics = new List<string>();
        foreach (var gap in profile.Gaps.Take(8))
            searchTopics.Add(gap);

        // Ensure category diversity — even if gaps exist, always inject underrepresented categories
        // so complements span security, CI/CD, observability, docs, etc.
        var diverseCategories = new[]
        {
            "testing and quality assurance",
            "performance monitoring and observability",
            "security scanning and vulnerability detection",
            "documentation generation",
            "CI/CD and deployment automation",
            "code analysis and linting",
            "dependency management",
            "containerization and deployment",
            "developer experience tooling"
        };

        var existingCategoryWords = searchTopics.SelectMany(t => t.ToLowerInvariant().Split(' ')).ToHashSet();
        foreach (var cat in diverseCategories)
        {
            if (searchTopics.Count >= MinimumComplements + 4) break; // search extra for diversity
            var catKeyword = cat.Split(' ')[0];
            if (!existingCategoryWords.Contains(catKeyword))
            {
                searchTopics.Add(cat);
                existingCategoryWords.Add(catKeyword);
            }
        }

        if (searchTopics.Count == 0) return complements;

        // Parallel web searches — each topic is an independent web query
        var searchSemaphore = new SemaphoreSlim(4); // Limit concurrent web searches for courtesy
        var searchTasks = searchTopics.Select(async topic =>
        {
            if (ct.IsCancellationRequested) return ((string topic, List<string> urls)?)null;
            await searchSemaphore.WaitAsync(ct);
            try
            {
                var query = $"{profile.PrimaryLanguage} {topic} library github stars:>100";
                var urls = await _searchService.SearchAsync(query, "DuckDuckGo",
                    "https://duckduckgo.com/?q={query}", ct: ct);
                return ((string, List<string>)?)(topic, urls.Take(5).ToList());
            }
            catch
            {
                return null; // Fall through — search failures are non-fatal
            }
            finally
            {
                searchSemaphore.Release();
            }
        }).ToList();

        var searchTaskResults = await Task.WhenAll(searchTasks);
        var searchResults = searchTaskResults.Where(r => r != null).Select(r => r!.Value).ToList();
        LastSearchCallCount = searchResults.Count;

        if (searchResults.Count == 0) return complements;

        // Enrich GitHub URLs with real descriptions before LLM evaluation (all topics in parallel)
        var enrichTasks = searchResults.Select(async sr =>
        {
            var urlTasks = sr.Item2.Select(async url => (url, desc: await EnrichGitHubUrlAsync(url, ct))).ToList();
            var entries = (await Task.WhenAll(urlTasks)).ToList();
            return (topic: sr.Item1, entries);
        }).ToList();

        var enrichedResults = (await Task.WhenAll(enrichTasks)).ToList();
        LastEnrichCallCount = enrichedResults.Sum(r => r.entries.Count);

        // Use JSON-structured prompt for reliable parsing (especially helps Ollama)
        var jsonPrompt = RepoScannerService.BuildJsonComplementPrompt(profile, enrichedResults, MinimumComplements);

        var llmSw = Stopwatch.StartNew();
        var response = await _llmService.GenerateJsonAsync(jsonPrompt,
            $"You are a software ecosystem analyst. Return a valid JSON object with a 'complements' array. " +
            $"Include at least {MinimumComplements} complementary projects ranked by relevance. " +
            "Ensure diversity across categories. Use ONLY projects from the URLs provided.",
            tier: ModelTier.Mini,
            ct: ct);
        llmSw.Stop();
        LastLlmDurationMs = llmSw.ElapsedMilliseconds;

        // Try JSON parsing first (preferred), fall back to markdown parsing
        complements = RepoScannerService.ParseJsonComplements(response);
        if (complements.Count == 0)
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
