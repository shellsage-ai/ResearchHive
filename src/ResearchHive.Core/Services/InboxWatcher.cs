using ResearchHive.Core.Models;

namespace ResearchHive.Core.Services;

/// <summary>
/// Watches the Inbox folder of active sessions and auto-ingests dropped files.
/// </summary>
public class InboxWatcher : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly ArtifactStore _artifactStore;
    private readonly IndexService _indexService;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();

    public event EventHandler<(string SessionId, Artifact Artifact)>? FileIngested;

    public InboxWatcher(SessionManager sessionManager, ArtifactStore artifactStore, IndexService indexService)
    {
        _sessionManager = sessionManager;
        _artifactStore = artifactStore;
        _indexService = indexService;
    }

    public void WatchSession(string sessionId)
    {
        if (_watchers.ContainsKey(sessionId)) return;

        var session = _sessionManager.GetSession(sessionId);
        if (session == null) return;

        var inboxPath = Path.Combine(session.WorkspacePath, "Inbox");
        Directory.CreateDirectory(inboxPath);

        var watcher = new FileSystemWatcher(inboxPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        watcher.Created += (_, e) => OnFileCreated(sessionId, e.FullPath);
        watcher.Renamed += (_, e) => OnFileCreated(sessionId, e.FullPath);

        _watchers[sessionId] = watcher;
    }

    public void StopWatching(string sessionId)
    {
        if (_watchers.TryGetValue(sessionId, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            _watchers.Remove(sessionId);
        }
    }

    private async void OnFileCreated(string sessionId, string filePath)
    {
        // Wait a bit for any copy operation to finish
        await Task.Delay(500);

        if (!File.Exists(filePath)) return;

        try
        {
            // Wait for file to be unlocked
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(300);
                }
            }

            var artifact = _artifactStore.IngestFile(sessionId, filePath);
            await _indexService.IndexArtifactAsync(sessionId, artifact);
            FileIngested?.Invoke(this, (sessionId, artifact));

            // Log the ingestion
            var db = _sessionManager.GetSessionDb(sessionId);
                db.Log("info", "inbox_ingest", $"Auto-ingested file: {artifact.OriginalName} ({artifact.SizeBytes} bytes)");
        }
        catch (Exception ex)
        {
            try
            {
                var db = _sessionManager.GetSessionDb(sessionId);
                db.Log("error", "inbox_error", $"Failed to ingest {Path.GetFileName(filePath)}: {ex.Message}");
            }
            catch { }
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}
