using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;

namespace ResearchHive.Core.Services;

/// <summary>
/// Hybrid retrieval engine combining:
/// 1. BM25 keyword search (via FTS5)
/// 2. Semantic search (embeddings with candidate pre-filtering)
/// 3. Heuristic boosters (exact phrase, title match, recency)
/// 4. Reciprocal Rank Fusion (RRF) for lane merging
/// </summary>
public class RetrievalService
{
    private readonly SessionManager _sessionManager;
    private readonly EmbeddingService _embeddingService;
    private readonly AppSettings _settings;

    /// <summary>RRF constant — controls how much lower-ranked results are discounted.</summary>
    private const float RrfK = 60f;

    public RetrievalService(SessionManager sessionManager, EmbeddingService embeddingService, AppSettings settings)
    {
        _sessionManager = sessionManager;
        _embeddingService = embeddingService;
        _settings = settings;
    }

    public async Task<List<RetrievalResult>> HybridSearchAsync(string sessionId, string query, int topK = 0, CancellationToken ct = default)
        => await HybridSearchAsync(sessionId, query, sourceTypeFilter: null, topK, ct);

    /// <summary>
    /// Hybrid search with optional source-type filter (e.g. "repo_code", "repo_doc").
    /// When a filter is set, only chunks matching one of the supplied source types are returned.
    /// </summary>
    public async Task<List<RetrievalResult>> HybridSearchAsync(string sessionId, string query, IReadOnlyList<string>? sourceTypeFilter, int topK = 0, CancellationToken ct = default)
    {
        if (topK <= 0) topK = _settings.DefaultTopK;
        var db = _sessionManager.GetSessionDb(sessionId);

        // ── Lane 1: BM25 keyword search (FTS5 native scoring) ──
        var bm25Results = new List<(Chunk chunk, float score)>();
        try
        {
            var ftsHits = db.SearchChunksFtsBm25(query, topK * 4);
            // Normalize BM25 scores to 0-1 range
            var maxBm25 = ftsHits.Count > 0 ? ftsHits.Max(h => h.bm25Score) : 1f;
            if (maxBm25 <= 0) maxBm25 = 1f;
            bm25Results = ftsHits.Select(h => (h.chunk, h.bm25Score / maxBm25)).ToList();
            if (sourceTypeFilter != null)
                bm25Results = bm25Results.Where(r => sourceTypeFilter.Contains(r.chunk.SourceType)).ToList();
        }
        catch
        {
            // FTS can fail on special characters; try legacy method
            try
            {
                var ftsChunks = db.SearchChunksFts(query, topK * 2);
                for (int i = 0; i < ftsChunks.Count; i++)
                    bm25Results.Add((ftsChunks[i], 1.0f - (i / (float)(ftsChunks.Count + 1))));
                if (sourceTypeFilter != null)
                    bm25Results = bm25Results.Where(r => sourceTypeFilter.Contains(r.Item1.SourceType)).ToList();
            }
            catch { /* Both failed — continue with semantic only */ }
        }

        // ── Lane 2: Semantic search (pre-filtered candidates, NOT all chunks) ──
        var semanticResults = new List<(Chunk chunk, float score)>();
        var queryEmbedding = await _embeddingService.GetEmbeddingAsync(query, ct);
        if (queryEmbedding != null)
        {
            // Get candidate chunks: FTS hits + chunks from the same sources
            var candidateSourceIds = bm25Results.Select(r => r.chunk.SourceId).Distinct().ToList();
            var candidates = bm25Results.Select(r => r.chunk).ToList();

            // Also get sibling chunks from matched sources (expands context)
            if (candidateSourceIds.Count > 0)
            {
                var siblingChunks = db.GetChunksBySourceIds(candidateSourceIds);
                var existingIds = new HashSet<string>(candidates.Select(c => c.Id));
                foreach (var sc in siblingChunks)
                {
                    if (!existingIds.Contains(sc.Id))
                    {
                        candidates.Add(sc);
                        existingIds.Add(sc.Id);
                    }
                }
            }

            // If we have very few candidates, fall back to all chunks (small sessions)
            if (candidates.Count < topK * 2)
            {
                candidates = db.GetAllChunks();
            }

            semanticResults = candidates
                .Where(c => c.Embedding != null)
                .Where(c => sourceTypeFilter == null || sourceTypeFilter.Contains(c.SourceType))
                .Select(c => (chunk: c, score: EmbeddingService.CosineSimilarity(queryEmbedding, c.Embedding)))
                .OrderByDescending(x => x.score)
                .Take(topK * 3)
                .ToList();
        }

        // ── Lane 3: Heuristic boosters ──
        var heuristicScores = new Dictionary<string, float>();
        var queryLower = query.ToLowerInvariant();
        var queryTerms = queryLower.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 3).ToHashSet();

        var allCandidates = bm25Results.Select(r => r.chunk)
            .Concat(semanticResults.Select(r => r.chunk))
            .DistinctBy(c => c.Id)
            .ToList();

        foreach (var chunk in allCandidates)
        {
            float boost = 0;

            // Exact phrase match bonus (case-insensitive without allocating lowered copy)
            if (chunk.Text.Contains(queryLower, StringComparison.OrdinalIgnoreCase))
                boost += 0.5f;

            // Term density — what fraction of query terms appear in this chunk
            var termsHit = queryTerms.Count(t => chunk.Text.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (queryTerms.Count > 0)
                boost += 0.3f * (termsHit / (float)queryTerms.Count);

            // First chunk of a source (title/header area) gets a bonus
            if (chunk.ChunkIndex == 0)
                boost += 0.15f;

            if (boost > 0)
                heuristicScores[chunk.Id] = boost;
        }

        // ── Reciprocal Rank Fusion (RRF) merge ──
        var rrfScores = new Dictionary<string, (Chunk chunk, float score)>();

        // Add BM25 lane
        for (int rank = 0; rank < bm25Results.Count; rank++)
        {
            var (chunk, _) = bm25Results[rank];
            var rrfScore = 1f / (RrfK + rank + 1);
            rrfScores[chunk.Id] = (chunk, rrfScore);
        }

        // Add semantic lane
        for (int rank = 0; rank < semanticResults.Count; rank++)
        {
            var (chunk, _) = semanticResults[rank];
            var rrfScore = 1f / (RrfK + rank + 1);
            if (rrfScores.TryGetValue(chunk.Id, out var existing))
                rrfScores[chunk.Id] = (chunk, existing.score + rrfScore);
            else
                rrfScores[chunk.Id] = (chunk, rrfScore);
        }

        // Add heuristic boosts
        foreach (var (chunkId, boost) in heuristicScores)
        {
            if (rrfScores.TryGetValue(chunkId, out var existing))
                rrfScores[chunkId] = (existing.chunk, existing.score + boost * 0.01f); // Scale down
        }

        return rrfScores.Values
            .OrderByDescending(x => x.score)
            .Take(topK)
            .Select(x => new RetrievalResult
            {
                Chunk = x.chunk,
                Score = x.score,
                SourceId = x.chunk.SourceId,
                SourceType = x.chunk.SourceType
            })
            .ToList();
    }

    public List<RetrievalResult> KeywordSearch(string sessionId, string query, int topK = 0)
    {
        if (topK <= 0) topK = _settings.DefaultTopK;
        var db = _sessionManager.GetSessionDb(sessionId);

        try
        {
            var hits = db.SearchChunksFtsBm25(query, topK);
            var maxBm25 = hits.Count > 0 ? hits.Max(h => h.bm25Score) : 1f;
            if (maxBm25 <= 0) maxBm25 = 1f;
            return hits.Select(h => new RetrievalResult
            {
                Chunk = h.chunk,
                Score = h.bm25Score / maxBm25,
                SourceId = h.chunk.SourceId,
                SourceType = h.chunk.SourceType
            }).ToList();
        }
        catch
        {
            return new List<RetrievalResult>();
        }
    }

    /// <summary>
    /// Searches within a report's markdown content by splitting into sections
    /// and ranking by embedding similarity. Returns the most relevant sections
    /// as synthetic RetrievalResults for Q&A context building.
    /// </summary>
    public async Task<List<RetrievalResult>> SearchReportContentAsync(string reportContent, string query, int topK = 5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reportContent) || string.IsNullOrWhiteSpace(query))
            return new List<RetrievalResult>();

        // Split report by ## headings into sections
        var sections = SplitReportIntoSections(reportContent);
        if (sections.Count == 0) return new List<RetrievalResult>();

        // Embed the query
        var queryEmbedding = await _embeddingService.GetEmbeddingAsync(query, ct);
        if (queryEmbedding == null)
        {
            // Fallback: keyword matching
            var queryTerms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 3).ToHashSet();

            return sections
                .Select(s =>
                {
                    var lower = s.Text.ToLowerInvariant();
                    var hits = queryTerms.Count(t => lower.Contains(t));
                    return new RetrievalResult
                    {
                        Chunk = new Chunk { Id = s.Heading, Text = s.Text, SourceType = "report" },
                        Score = queryTerms.Count > 0 ? (float)hits / queryTerms.Count : 0,
                        SourceType = "report"
                    };
                })
                .OrderByDescending(r => r.Score)
                .Take(topK)
                .ToList();
        }

        // Embed each section and rank by cosine similarity
        var scored = new List<(ReportSection section, float score)>();
        foreach (var section in sections)
        {
            var sectionEmbedding = await _embeddingService.GetEmbeddingAsync(section.Text, ct);
            if (sectionEmbedding != null)
            {
                var sim = EmbeddingService.CosineSimilarity(queryEmbedding, sectionEmbedding);
                scored.Add((section, sim));
            }
        }

        return scored
            .OrderByDescending(s => s.score)
            .Take(topK)
            .Select(s => new RetrievalResult
            {
                Chunk = new Chunk { Id = s.section.Heading, Text = s.section.Text, SourceType = "report" },
                Score = s.score,
                SourceType = "report"
            })
            .ToList();
    }

    /// <summary>
    /// Splits markdown report content into sections by ## headings.
    /// </summary>
    internal static List<ReportSection> SplitReportIntoSections(string markdown)
    {
        var sections = new List<ReportSection>();
        var lines = markdown.Split('\n');
        string currentHeading = "Introduction";
        var currentContent = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("## "))
            {
                // Save previous section if it has content
                var text = currentContent.ToString().Trim();
                if (text.Length > 20)
                    sections.Add(new ReportSection { Heading = currentHeading, Text = text });

                currentHeading = line.TrimStart()[3..].Trim();
                currentContent.Clear();
            }
            else
            {
                currentContent.AppendLine(line);
            }
        }

        // Save last section
        var lastText = currentContent.ToString().Trim();
        if (lastText.Length > 20)
            sections.Add(new ReportSection { Heading = currentHeading, Text = lastText });

        return sections;
    }
}

public class ReportSection
{
    public string Heading { get; set; } = "";
    public string Text { get; set; } = "";
}

public class RetrievalResult
{
    public Chunk Chunk { get; set; } = new();
    public float Score { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
}
