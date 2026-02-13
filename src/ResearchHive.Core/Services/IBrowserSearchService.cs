namespace ResearchHive.Core.Services;

/// <summary>
/// Abstraction for web search (DuckDuckGo, Google, etc.).
/// Enables mocking in tests without real browser automation.
/// </summary>
public interface IBrowserSearchService
{
    Task<List<string>> SearchAsync(string query, string engineName, string urlTemplate, string? timeRange = null, CancellationToken ct = default);
}
