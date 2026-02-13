using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;

namespace ResearchHive.Core.Services;

/// <summary>
/// Indexes a cloned repository's source code and documentation into the session's
/// chunk store for retrieval-based Q&A. Skips re-indexing if TreeSha hasn't changed.
/// </summary>
public class RepoIndexService
{
    private readonly RepoCloneService _cloneService;
    private readonly CodeChunker _codeChunker;
    private readonly EmbeddingService _embeddingService;
    private readonly SessionManager _sessionManager;
    private readonly AppSettings _settings;

    public RepoIndexService(
        RepoCloneService cloneService,
        CodeChunker codeChunker,
        EmbeddingService embeddingService,
        SessionManager sessionManager,
        AppSettings settings)
    {
        _cloneService = cloneService;
        _codeChunker = codeChunker;
        _embeddingService = embeddingService;
        _sessionManager = sessionManager;
        _settings = settings;
    }

    /// <summary>
    /// Clone, chunk, embed, and index a repository's source files into the session DB.
    /// </summary>
    /// <returns>Number of files indexed and total chunks created.</returns>
    public async Task<(int FilesIndexed, int ChunksCreated)> IndexRepoAsync(
        string sessionId, RepoProfile profile, CancellationToken ct = default)
    {
        // 1. Clone or update the repo
        var clonePath = await _cloneService.CloneOrUpdateAsync(profile.RepoUrl, ct);

        // 2. Check if we can skip (tree SHA unchanged)
        var currentSha = await _cloneService.GetTreeShaAsync(clonePath, ct);
        if (!string.IsNullOrEmpty(currentSha) && currentSha == profile.TreeSha)
            return (0, 0); // Already indexed this version

        // 3. Discover files
        var files = _cloneService.DiscoverFiles(clonePath);

        // 4. Chunk all files (parallel I/O for large repos)
        var sourceId = $"{profile.Owner}/{profile.Name}";
        var chunksPerFile = new System.Collections.Concurrent.ConcurrentBag<List<Chunk>>();
        int indexedCount = 0;

        await Parallel.ForEachAsync(files,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (file, token) =>
            {
                try
                {
                    var content = await File.ReadAllTextAsync(file.AbsolutePath, token);
                    if (string.IsNullOrWhiteSpace(content)) return;

                    var chunks = _codeChunker.ChunkFile(file.RelativePath, content, sessionId, sourceId);
                    chunksPerFile.Add(chunks);
                    Interlocked.Increment(ref indexedCount);
                }
                catch
                {
                    // Skip files that can't be read (binary, encoding issues, etc.)
                }
            });

        var allChunks = chunksPerFile.SelectMany(c => c).ToList();
        int filesIndexed = indexedCount;

        if (allChunks.Count == 0) return (0, 0);

        // 5. Batch embed
        var texts = allChunks.Select(c => c.Text).ToList();
        var embeddings = await _embeddingService.GetEmbeddingBatchAsync(
            texts, _settings.EmbeddingConcurrency, ct);

        for (int i = 0; i < allChunks.Count; i++)
            allChunks[i].Embedding = embeddings[i];

        // 6. Save to session DB
        var db = _sessionManager.GetSessionDb(sessionId);
        db.SaveChunksBatch(allChunks);

        // 7. Update profile metadata
        profile.TreeSha = currentSha ?? "";
        profile.IndexedFileCount = filesIndexed;
        profile.IndexedChunkCount = allChunks.Count;

        return (filesIndexed, allChunks.Count);
    }
}
