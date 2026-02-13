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

        // 4. Chunk all files
        var sourceId = $"{profile.Owner}/{profile.Name}";
        var allChunks = new List<Chunk>();
        int filesIndexed = 0;

        foreach (var (absPath, relPath) in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var content = await File.ReadAllTextAsync(absPath, ct);
                if (string.IsNullOrWhiteSpace(content)) continue;

                var chunks = _codeChunker.ChunkFile(relPath, content, sessionId, sourceId);
                allChunks.AddRange(chunks);
                filesIndexed++;
            }
            catch
            {
                // Skip files that can't be read (binary, encoding issues, etc.)
            }
        }

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
