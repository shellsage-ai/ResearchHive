using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ResearchHive.Core.Services;

/// <summary>
/// Searches GitHub for repositories matching a query using the public GitHub Search API.
/// Returns structured results suitable for one-click scanning and fusion.
/// Rate limit: 10 requests/minute unauthenticated, 30 requests/minute with token.
/// </summary>
public class GitHubDiscoveryService
{
    private readonly HttpClient _http;
    private readonly ILogger<GitHubDiscoveryService>? _logger;

    /// <summary>Maximum results to return per search.</summary>
    public const int MaxResults = 20;

    public GitHubDiscoveryService(ILogger<GitHubDiscoveryService>? logger = null)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ResearchHive", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// Search GitHub repositories matching a query. Sorted by relevance (stars tiebreaker).
    /// Returns up to <see cref="MaxResults"/> results.
    /// </summary>
    public async Task<List<DiscoveryResult>> SearchAsync(string query, string? language = null, int minStars = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();

        var q = query.Trim();
        if (!string.IsNullOrWhiteSpace(language))
            q += $" language:{language}";
        if (minStars > 0)
            q += $" stars:>={minStars}";

        var url = $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(q)}&sort=stars&order=desc&per_page={MaxResults}";

        try
        {
            _logger?.LogInformation("GitHub Discovery: searching \"{Query}\"", q);
            var response = await _http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("GitHub Search API returned {Status}", response.StatusCode);
                return new();
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.GetProperty("items");

            var results = new List<DiscoveryResult>();
            foreach (var item in items.EnumerateArray())
            {
                results.Add(new DiscoveryResult
                {
                    FullName = item.GetProperty("full_name").GetString() ?? "",
                    Description = item.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String
                        ? desc.GetString() ?? "" : "",
                    HtmlUrl = item.GetProperty("html_url").GetString() ?? "",
                    Stars = item.GetProperty("stargazers_count").GetInt32(),
                    Forks = item.TryGetProperty("forks_count", out var fk) ? fk.GetInt32() : 0,
                    Language = item.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String
                        ? lang.GetString() ?? "" : "",
                    UpdatedAt = item.TryGetProperty("updated_at", out var upd)
                        ? DateTime.TryParse(upd.GetString(), out var dt) ? dt : DateTime.MinValue
                        : DateTime.MinValue,
                    Topics = item.TryGetProperty("topics", out var topics) && topics.ValueKind == JsonValueKind.Array
                        ? topics.EnumerateArray().Select(t => t.GetString() ?? "").Where(t => t.Length > 0).ToList()
                        : new(),
                    IsArchived = item.TryGetProperty("archived", out var arch) && arch.GetBoolean(),
                    License = item.TryGetProperty("license", out var lic) && lic.ValueKind == JsonValueKind.Object
                        && lic.TryGetProperty("spdx_id", out var spdx) && spdx.ValueKind == JsonValueKind.String
                        ? spdx.GetString() ?? "" : "",
                });
            }

            _logger?.LogInformation("GitHub Discovery: {Count} results for \"{Query}\"", results.Count, q);
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger?.LogWarning(ex, "GitHub Discovery search failed");
            return new();
        }
    }
}

/// <summary>A repository found via GitHub Search API.</summary>
public class DiscoveryResult
{
    public string FullName { get; set; } = "";
    public string Description { get; set; } = "";
    public string HtmlUrl { get; set; } = "";
    public int Stars { get; set; }
    public int Forks { get; set; }
    public string Language { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public List<string> Topics { get; set; } = new();
    public bool IsArchived { get; set; }
    public string License { get; set; } = "";

    /// <summary>Human-friendly "last updated" label.</summary>
    public string UpdatedAgo
    {
        get
        {
            if (UpdatedAt == DateTime.MinValue) return "";
            var diff = DateTime.UtcNow - UpdatedAt;
            if (diff.TotalDays > 365) return $"{(int)(diff.TotalDays / 365)}y ago";
            if (diff.TotalDays > 30) return $"{(int)(diff.TotalDays / 30)}mo ago";
            if (diff.TotalDays > 1) return $"{(int)diff.TotalDays}d ago";
            return "today";
        }
    }
}
