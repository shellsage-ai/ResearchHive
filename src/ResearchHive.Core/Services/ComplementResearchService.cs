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
    public const int MinimumComplements = 8;

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
        foreach (var gap in profile.Gaps.Take(6))
            searchTopics.Add(gap);

        // Inject DOMAIN-AWARE topics derived from what the project actually does.
        // Proven capabilities reveal the project's domain — suggest tools that extend it.
        var domainTopics = InferDomainSearchTopics(profile);
        foreach (var dt in domainTopics)
        {
            if (searchTopics.Count >= MinimumComplements + 4) break;
            if (!searchTopics.Any(t => t.Contains(dt.Split(' ')[0], StringComparison.OrdinalIgnoreCase)))
                searchTopics.Add(dt);
        }

        // Ensure category diversity — dynamically derived from project characteristics,
        // filtered by inapplicable concepts, instead of a static hardcoded list
        var diverseCategories = InferDiverseCategories(profile);

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
        // Store enrichment results for post-scan metadata injection
        var enrichmentCache = new Dictionary<string, GitHubEnrichmentResult>(StringComparer.OrdinalIgnoreCase);
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // dedup

        var enrichTasks = searchResults.Select(async sr =>
        {
            var urlTasks = sr.Item2.Select(async url =>
            {
                var enrichment = await EnrichGitHubUrlAsync(url, ct);
                if (enrichment != null)
                    lock (enrichmentCache) { enrichmentCache[url] = enrichment; }
                var desc = enrichment?.ToDescriptionString() ?? "";
                return (url, desc);
            }).ToList();
            var entries = (await Task.WhenAll(urlTasks)).ToList();
            return (topic: sr.Item1, entries);
        }).ToList();

        var enrichedResults = (await Task.WhenAll(enrichTasks)).ToList();
        LastEnrichCallCount = enrichedResults.Sum(r => r.entries.Count);

        // Dedup URLs across topics — keep first occurrence only
        var dedupedResults = new List<(string topic, List<(string url, string desc)> entries)>();
        foreach (var (topic, entries) in enrichedResults)
        {
            var uniqueEntries = new List<(string url, string desc)>();
            foreach (var (url, desc) in entries)
            {
                var normalizedUrl = NormalizeGitHubUrl(url);
                if (seenUrls.Add(normalizedUrl))
                    uniqueEntries.Add((url, desc));
            }
            if (uniqueEntries.Count > 0)
                dedupedResults.Add((topic, uniqueEntries));
        }

        // Use JSON-structured prompt for reliable parsing (especially helps Ollama)
        var jsonPrompt = RepoScannerService.BuildJsonComplementPrompt(profile, dedupedResults, MinimumComplements);

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

        // Inject GitHub API metadata onto parsed complements from enrichment cache
        foreach (var comp in complements)
        {
            var normalized = NormalizeGitHubUrl(comp.Url);
            // Try exact URL first, then normalized
            GitHubEnrichmentResult? enrichment = null;
            if (enrichmentCache.TryGetValue(comp.Url, out var e1))
                enrichment = e1;
            else if (enrichmentCache.TryGetValue(normalized, out var e2))
                enrichment = e2;
            else
            {
                // Fuzzy match: find enrichment whose URL normalizes to the same repo
                enrichment = enrichmentCache.Values.FirstOrDefault(
                    er => NormalizeGitHubUrl(er.Url).Equals(normalized, StringComparison.OrdinalIgnoreCase));
            }

            if (enrichment != null)
            {
                comp.Stars = enrichment.Stars;
                comp.IsArchived = enrichment.IsArchived;
                comp.LastPushed = enrichment.LastPushed;
                comp.RepoLanguage = enrichment.Language;
                if (string.IsNullOrEmpty(comp.License) && !string.IsNullOrEmpty(enrichment.License))
                    comp.License = enrichment.License;
            }
        }

        return complements;
    }

    /// <summary>Normalize a GitHub URL to https://github.com/owner/repo for dedup/matching.</summary>
    internal static string NormalizeGitHubUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return url;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return url;
        return $"https://github.com/{segments[0]}/{segments[1]}".ToLowerInvariant();
    }

    /// <summary>
    /// Table-driven domain-specific search topics from the project's proven capabilities,
    /// domain tags, deployment target, and architecture. Works for ANY project type.
    /// Instead of hardcoded if-blocks for specific domains, uses a keyword→topic table
    /// that naturally extends to any capability set.
    /// </summary>
    internal static List<string> InferDomainSearchTopics(RepoProfile profile)
    {
        var topics = new List<string>();
        var capText = profile.FactSheet != null
            ? string.Join(" ", profile.FactSheet.ProvenCapabilities.Select(c => c.Capability)).ToLowerInvariant()
            : string.Join(" ", profile.Strengths).ToLowerInvariant();
        var domainTags = profile.FactSheet?.DomainTags ?? new List<string>();
        var deployTarget = profile.FactSheet?.DeploymentTarget?.ToLowerInvariant() ?? "";
        var appType = profile.FactSheet?.AppType?.ToLowerInvariant() ?? "";

        // Table-driven: (keyword triggers, topic suggestions)
        // Each rule: if ANY keyword matches capText/domainTags, add the topics
        var domainTopicRules = new (string[] triggers, string[] topics)[]
        {
            // AI / LLM
            (new[] { "llm", "ai provider", "embedding", "cloud ai" },
             new[] { "AI agent orchestration framework", "local LLM inference runtime", "prompt engineering evaluation toolkit" }),

            // RAG / vector / embedding
            (new[] { "rag", "vector", "semantic search", "retrieval", "embedding" },
             new[] { "vector database client library", "document chunking text splitting" }),

            // Web scraping / research
            (new[] { "citation", "scraping", "crawl", "playwright", "selenium" },
             new[] { "HTML parsing web scraping library", "structured data extraction" }),

            // Desktop UI
            (new[] { "wpf", "winforms", "avalonia", "maui", "desktop ui", "gtk", "qt" },
             new[] { "data visualization charting library", "UI component toolkit" }),

            // Resilience
            (new[] { "circuit breaker", "retry", "rate limit", "resilience" },
             new[] { "advanced fault tolerance resilience patterns" }),

            // Logging
            (new[] { "logging", "structured log" },
             new[] { "log aggregation dashboard", "structured logging sink" }),

            // Data / DB
            (new[] { "database", "sqlite", "postgres", "mongodb", "data access" },
             new[] { "database migration tool", "data access optimization" }),

            // API / web  
            (new[] { "api", "rest", "graphql", "grpc", "web api" },
             new[] { "API documentation generation", "API client SDK generator" }),

            // Security
            (new[] { "auth", "encryption", "dpapi", "security", "vault" },
             new[] { "secret management vault", "security scanning tool" }),

            // Testing
            (new[] { "test", "xunit", "nunit", "jest", "pytest" },
             new[] { "test coverage reporting", "mutation testing", "property-based testing" }),

            // Real-time
            (new[] { "websocket", "signalr", "socket.io", "chat", "real-time" },
             new[] { "real-time messaging library", "event streaming platform" }),

            // Data pipeline / ETL
            (new[] { "etl", "data pipeline", "spark", "airflow" },
             new[] { "data transformation library", "workflow orchestration" }),

            // Gaming
            (new[] { "game", "unity", "godot", "graphics" },
             new[] { "game physics engine", "game asset management" }),

            // IoT
            (new[] { "iot", "mqtt", "sensor", "embedded" },
             new[] { "IoT device management", "time-series database" }),

            // Finance
            (new[] { "finance", "trading", "market data", "portfolio" },
             new[] { "financial data feed library", "backtesting framework" }),

            // E-commerce
            (new[] { "e-commerce", "payment", "stripe", "checkout" },
             new[] { "payment processing library", "inventory management" }),
        };

        var combinedText = capText + " " + string.Join(" ", domainTags).ToLowerInvariant() +
                           " " + deployTarget + " " + appType;

        foreach (var (triggers, topicSuggestions) in domainTopicRules)
        {
            if (triggers.Any(t => combinedText.Contains(t)))
            {
                foreach (var t in topicSuggestions)
                {
                    if (!topics.Any(existing => existing.Contains(t.Split(' ')[0], StringComparison.OrdinalIgnoreCase)))
                        topics.Add(t);
                }
            }
        }

        return topics;
    }

    /// <summary>
    /// Generate dynamic diverse category topics based on the project's actual characteristics.
    /// Instead of a static list of 5 categories, derives relevant categories from
    /// what the project IS and what it's MISSING.
    /// </summary>
    internal static List<string> InferDiverseCategories(RepoProfile profile)
    {
        var categories = new List<string>();
        var absentCaps = profile.FactSheet?.ConfirmedAbsent?.Select(c => c.Capability.ToLowerInvariant()).ToList()
                         ?? new List<string>();
        var missingFiles = profile.FactSheet?.DiagnosticFilesMissing ?? new List<string>();
        var inapplicable = new HashSet<string>(
            profile.FactSheet?.InapplicableConcepts ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        // Table-driven: category topics to consider, with keywords for filtering
        var potentialCategories = new (string topic, string[] excludeIfInapplicable)[]
        {
            ("performance profiling and benchmarking", new[] { "benchmark" }),
            ("security scanning and vulnerability detection", new[] { "security" }),
            ("code analysis and linting", new[] { "linting" }),
            ("developer experience tooling", Array.Empty<string>()),
            ("error tracking and crash reporting", Array.Empty<string>()),
            ("configuration management", Array.Empty<string>()),
            ("documentation generation", new[] { "documentation" }),
            ("continuous integration testing", new[] { "containerization" }),
            ("dependency update automation", Array.Empty<string>()),
        };

        foreach (var (topic, excludeKeys) in potentialCategories)
        {
            // Skip if the topic addresses an inapplicable concept
            if (excludeKeys.Any(k => inapplicable.Contains(k))) continue;
            categories.Add(topic);
        }

        return categories;
    }

    /// <summary>
    /// Fetches structured metadata for GitHub URLs via the GitHub API.
    /// Returns null for non-GitHub URLs or on failure.
    /// </summary>
    internal async Task<GitHubEnrichmentResult?> EnrichGitHubUrlAsync(string url, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;

            // Extract owner/repo from path: /owner/repo[/...]
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) return null;
            var owner = segments[0];
            var repo = segments[1];

            var apiUrl = $"https://api.github.com/repos/{owner}/{repo}";
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = new GitHubEnrichmentResult { Url = url };

            if (root.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
                result.Description = descEl.GetString() ?? "";

            if (root.TryGetProperty("stargazers_count", out var starsEl))
                result.Stars = starsEl.GetInt32();

            if (root.TryGetProperty("license", out var licEl) && licEl.ValueKind == JsonValueKind.Object &&
                licEl.TryGetProperty("spdx_id", out var spdxEl) && spdxEl.ValueKind == JsonValueKind.String)
            {
                var spdx = spdxEl.GetString();
                if (!string.IsNullOrWhiteSpace(spdx) && spdx != "NOASSERTION") result.License = spdx;
            }

            if (root.TryGetProperty("archived", out var archivedEl) && archivedEl.ValueKind == JsonValueKind.True)
                result.IsArchived = true;

            if (root.TryGetProperty("pushed_at", out var pushedEl) && pushedEl.ValueKind == JsonValueKind.String)
            {
                if (DateTime.TryParse(pushedEl.GetString(), out var pushed))
                    result.LastPushed = pushed;
            }

            if (root.TryGetProperty("language", out var langEl) && langEl.ValueKind == JsonValueKind.String)
                result.Language = langEl.GetString() ?? "";

            return result;
        }
        catch
        {
            return null; // non-fatal
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
