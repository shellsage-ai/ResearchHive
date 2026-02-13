using ResearchHive.Core.Configuration;
using ResearchHive.Core.Data;
using ResearchHive.Core.Models;

namespace ResearchHive.Core.Services;

/// <summary>
/// Orchestrates the global Hive Mind memory — promotes session chunks to global store,
/// retrieves cross-session knowledge, and answers questions using RAG over all sessions.
/// </summary>
public class GlobalMemoryService
{
    private readonly GlobalDb _globalDb;
    private readonly SessionManager _sessionManager;
    private readonly EmbeddingService _embeddingService;
    private readonly LlmService _llmService;
    private readonly AppSettings _settings;

    private const float RrfK = 60f;

    public GlobalMemoryService(
        GlobalDb globalDb,
        SessionManager sessionManager,
        EmbeddingService embeddingService,
        LlmService llmService,
        AppSettings settings)
    {
        _globalDb = globalDb;
        _sessionManager = sessionManager;
        _embeddingService = embeddingService;
        _llmService = llmService;
        _settings = settings;
    }

    // ── Promote session chunks to global store ──

    /// <summary>
    /// Promotes the top chunks from a session to the global store.
    /// Chunks keep their embeddings; tags are enriched with session context.
    /// </summary>
    public void PromoteSessionChunks(string sessionId, string? domainPack = null, int maxChunks = 100)
    {
        var db = _sessionManager.GetSessionDb(sessionId);
        var chunks = db.GetAllChunks();

        // Take the most valuable chunks — those with embeddings, sorted by chunk index (earlier = more context)
        var candidates = chunks
            .Where(c => c.Embedding != null && !string.IsNullOrEmpty(c.Text))
            .OrderBy(c => c.SourceType == "report" ? 0 : c.SourceType == "repo_doc" ? 1 : c.SourceType == "repo_code" ? 2 : 3)
            .ThenBy(c => c.ChunkIndex)
            .Take(maxChunks)
            .ToList();

        var globalChunks = candidates.Select(c => new GlobalChunk
        {
            Id = $"promo_{sessionId}_{c.Id}",
            SessionId = sessionId,
            SourceType = c.SourceType,
            DomainPack = domainPack,
            Text = c.Text,
            Embedding = c.Embedding,
            Tags = new List<string> { $"session:{sessionId}", $"source:{c.SourceType}" },
            PromotedUtc = DateTime.UtcNow
        }).ToList();

        _globalDb.SaveChunksBatch(globalChunks);
    }

    /// <summary>
    /// Promotes specific chunks (e.g. from a completed job's report) to global store.
    /// </summary>
    public void PromoteChunks(IReadOnlyList<Chunk> chunks, string sessionId, string? jobId = null, string? domainPack = null, string? repoUrl = null)
    {
        var globalChunks = chunks
            .Where(c => !string.IsNullOrEmpty(c.Text))
            .Select(c => new GlobalChunk
            {
                Id = $"promo_{sessionId}_{c.Id}",
                SessionId = sessionId,
                JobId = jobId,
                SourceType = c.SourceType,
                RepoUrl = repoUrl,
                DomainPack = domainPack,
                Text = c.Text,
                Embedding = c.Embedding,
                Tags = new List<string> { $"session:{sessionId}", $"source:{c.SourceType}" },
                PromotedUtc = DateTime.UtcNow
            }).ToList();

        _globalDb.SaveChunksBatch(globalChunks);
    }

    // ── Strategy extraction ──

    /// <summary>
    /// After a job completes, extract a reusable strategy and store it in the global memory.
    /// Inspired by ReasoningBank: distill "what worked / what to avoid" from the job outcome.
    /// </summary>
    public async Task ExtractAndSaveStrategyAsync(string sessionId, ResearchJob job, string? domainPack = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(job.FullReport) && string.IsNullOrEmpty(job.ExecutiveSummary))
            return;

        var reportExcerpt = job.FullReport?.Length > 3000 ? job.FullReport[..3000] : job.FullReport ?? "";
        var summary = job.ExecutiveSummary ?? "";

        var prompt = $@"Analyze this completed research job and extract a reusable strategy.

Job prompt: {job.Prompt}
Executive summary: {summary}
Report excerpt: {reportExcerpt}
Outcome: {job.State}

Extract a concise strategy document with:
1. **Task Pattern** — What kind of task was this? (one line)
2. **What Worked** — Specific approaches, tools, or methods that produced good results.
3. **What To Avoid** — Pitfalls, dead ends, or approaches that failed or were inefficient.
4. **Key Insight** — The single most important takeaway for similar future tasks.
5. **Reusable Template** — A brief template or checklist for tackling similar tasks.

Keep it under 500 words. Be specific and actionable — reference actual findings, not generalities.";

        var systemPrompt = "You are a research methodology expert. Extract reusable strategies from completed research jobs. Be concrete and specific.";

        try
        {
            var response = await _llmService.GenerateWithMetadataAsync(prompt, systemPrompt, maxTokens: 800, ct: ct);
            if (string.IsNullOrWhiteSpace(response.Text)) return;

            var embedding = await _embeddingService.GetEmbeddingAsync(response.Text, ct);

            var strategy = new GlobalChunk
            {
                Id = $"strategy_{job.Id}",
                SessionId = sessionId,
                JobId = job.Id,
                SourceType = "strategy",
                DomainPack = domainPack,
                Text = response.Text,
                Embedding = embedding,
                Tags = new List<string> { $"session:{sessionId}", $"job:{job.Id}", "strategy", $"outcome:{job.State}" },
                PromotedUtc = DateTime.UtcNow
            };

            _globalDb.SaveChunk(strategy);
        }
        catch
        {
            // Strategy extraction is best-effort — don't fail the job
        }
    }

    // ── Hive Mind Q&A (cross-session RAG) ──

    /// <summary>
    /// Ask a question across all sessions using the global memory store.
    /// Combines BM25 keyword search + semantic search + LLM generation.
    /// </summary>
    public async Task<string> AskHiveMindAsync(string question, MemoryScope scope = MemoryScope.HiveMind, string? domainPackFilter = null, int topK = 10, CancellationToken ct = default)
    {
        // Build scope filters
        string? sourceTypeFilter = scope switch
        {
            MemoryScope.ThisRepo => "repo_code",
            _ => null
        };

        // ── BM25 keyword search ──
        var bm25Results = new List<(GlobalChunk chunk, float score)>();
        try
        {
            var ftsHits = _globalDb.SearchFtsBm25(question, sourceTypeFilter, domainPackFilter, topK * 4);
            var maxBm25 = ftsHits.Count > 0 ? ftsHits.Max(h => h.bm25) : 1f;
            if (maxBm25 <= 0) maxBm25 = 1f;
            bm25Results = ftsHits.Select(h => (h.chunk, h.bm25 / maxBm25)).ToList();
        }
        catch { /* FTS can fail on special chars */ }

        // ── Semantic search ──
        var semanticResults = new List<(GlobalChunk chunk, float score)>();
        var queryEmbedding = await _embeddingService.GetEmbeddingAsync(question, ct);
        if (queryEmbedding != null)
        {
            var candidates = _globalDb.GetAllChunksWithEmbeddings(sourceTypeFilter, domainPackFilter);
            semanticResults = candidates
                .Select(c => (chunk: c, score: EmbeddingService.CosineSimilarity(queryEmbedding, c.Embedding)))
                .OrderByDescending(x => x.score)
                .Take(topK * 3)
                .ToList();
        }

        // ── RRF merge ──
        var rrfScores = new Dictionary<string, (GlobalChunk chunk, float score)>();

        for (int rank = 0; rank < bm25Results.Count; rank++)
        {
            var (chunk, _) = bm25Results[rank];
            rrfScores[chunk.Id] = (chunk, 1f / (RrfK + rank + 1));
        }

        for (int rank = 0; rank < semanticResults.Count; rank++)
        {
            var (chunk, _) = semanticResults[rank];
            var rrfScore = 1f / (RrfK + rank + 1);
            if (rrfScores.TryGetValue(chunk.Id, out var existing))
                rrfScores[chunk.Id] = (chunk, existing.score + rrfScore);
            else
                rrfScores[chunk.Id] = (chunk, rrfScore);
        }

        var topResults = rrfScores.Values
            .OrderByDescending(x => x.score)
            .Take(topK)
            .ToList();

        if (topResults.Count == 0)
            return "No relevant knowledge found in the Hive Mind. Try promoting some session data first.";

        // ── Also fetch relevant strategies ──
        var strategies = _globalDb.GetStrategies(domainPackFilter);
        var strategyContext = "";
        if (strategies.Count > 0)
        {
            // Find strategies relevant to the question
            var relevantStrategies = strategies;
            if (queryEmbedding != null)
            {
                relevantStrategies = strategies
                    .Where(s => s.Embedding != null)
                    .OrderByDescending(s => EmbeddingService.CosineSimilarity(queryEmbedding, s.Embedding))
                    .Take(3)
                    .ToList();
            }
            else
            {
                relevantStrategies = strategies.Take(3).ToList();
            }

            if (relevantStrategies.Count > 0)
            {
                strategyContext = "\n\n--- RELEVANT STRATEGIES FROM PAST RESEARCH ---\n" +
                    string.Join("\n---\n", relevantStrategies.Select(s => s.Text));
            }
        }

        // ── Build context and generate answer ──
        var contextBlock = string.Join("\n\n---\n\n",
            topResults.Select(r => $"[{r.chunk.SourceType}] (session: {r.chunk.SessionId ?? "unknown"})\n{r.chunk.Text}"));

        var systemPrompt = @"You are a research analyst with access to a knowledge base spanning multiple research sessions.
Answer the user's question using ONLY the provided context chunks and strategies.
If the context is insufficient, say so — do NOT fabricate information.
Reference specific sessions, source types, or strategies when relevant.
Be thorough but concise.";

        var userPrompt = $@"Using this cross-session knowledge base:

{contextBlock}{strategyContext}

Question: {question}

Provide a thorough answer based on the available evidence.";

        return await _llmService.GenerateAsync(userPrompt, systemPrompt, 3000, ct);
    }

    /// <summary>Get Hive Mind statistics.</summary>
    public HiveMindStats GetStats()
    {
        var total = _globalDb.GetChunkCount();
        var strategies = _globalDb.GetStrategies();
        return new HiveMindStats
        {
            TotalChunks = total,
            StrategyCount = strategies.Count,
        };
    }

    // ── Curation API ──

    /// <summary>Browse global chunks with pagination and optional filters.</summary>
    public List<GlobalChunk> BrowseChunks(int offset = 0, int limit = 50,
        string? sourceTypeFilter = null, string? domainPackFilter = null, string? sessionIdFilter = null)
        => _globalDb.GetChunks(offset, limit, sourceTypeFilter, domainPackFilter, sessionIdFilter);

    /// <summary>Delete a single chunk from the global store.</summary>
    public void DeleteChunk(string chunkId) => _globalDb.DeleteChunk(chunkId);

    /// <summary>Delete all chunks promoted from a specific session.</summary>
    public void DeleteSessionChunks(string sessionId) => _globalDb.DeleteBySession(sessionId);

    /// <summary>Get distinct source types for filter dropdowns.</summary>
    public List<string> GetSourceTypes() => _globalDb.GetDistinctSourceTypes();
}

/// <summary>Statistics about the Hive Mind global memory.</summary>
public class HiveMindStats
{
    public int TotalChunks { get; set; }
    public int StrategyCount { get; set; }
}
