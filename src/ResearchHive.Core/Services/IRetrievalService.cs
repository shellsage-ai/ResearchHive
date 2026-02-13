using ResearchHive.Core.Models;

namespace ResearchHive.Core.Services;

/// <summary>
/// Abstraction for hybrid retrieval (BM25 + semantic search + RRF fusion).
/// Enables mocking in tests without a real vector store.
/// </summary>
public interface IRetrievalService
{
    Task<List<RetrievalResult>> HybridSearchAsync(string sessionId, string query, int topK = 0, CancellationToken ct = default);
    Task<List<RetrievalResult>> HybridSearchAsync(string sessionId, string query, IReadOnlyList<string>? sourceTypeFilter, int topK = 0, CancellationToken ct = default);
    Task<List<RetrievalResult>> HybridSearchAsync(string sessionId, string query, IReadOnlyList<string>? sourceTypeFilter, string? sourceIdFilter, int topK = 0, CancellationToken ct = default);
}
