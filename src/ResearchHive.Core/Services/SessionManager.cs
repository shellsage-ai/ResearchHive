using ResearchHive.Core.Configuration;
using ResearchHive.Core.Data;
using ResearchHive.Core.Models;
using System.Text.Json;

namespace ResearchHive.Core.Services;

public class SessionManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly RegistryDb _registry;
    private readonly Dictionary<string, SessionDb> _openDbs = new();

    public SessionManager(AppSettings settings)
    {
        _settings = settings;
        Directory.CreateDirectory(_settings.SessionsPath);
        _registry = new RegistryDb(_settings.RegistryDbPath);
    }

    public Session CreateSession(string title, string description, DomainPack pack, List<string>? tags = null)
    {
        var now = DateTime.UtcNow;
        var safeTitle = SanitizeFolderName(title);
        var shortId = Guid.NewGuid().ToString("N")[..8];
        var folderName = $"{now:yyyy-MM-dd}_{safeTitle}_{shortId}";
        var workspacePath = Path.Combine(_settings.SessionsPath, folderName);

        var session = new Session
        {
            Title = title,
            Description = description,
            Pack = pack,
            Tags = tags ?? new(),
            WorkspacePath = workspacePath,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        // Create directory structure
        foreach (var sub in new[] { "Inbox", "Artifacts", "Snapshots", "Captures", "Index", "Notes", "Exports", "Logs" })
        {
            Directory.CreateDirectory(Path.Combine(workspacePath, sub));
        }

        // Create session.json
        File.WriteAllText(
            Path.Combine(workspacePath, "session.json"),
            JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));

        _registry.SaveSession(session);
        return session;
    }

    public List<Session> GetAllSessions() => _registry.GetAllSessions();

    public Session? GetSession(string id) => _registry.GetSession(id);

    public List<Session> SearchSessions(string query) => _registry.SearchSessions(query);

    public void UpdateSession(Session session)
    {
        session.UpdatedUtc = DateTime.UtcNow;
        _registry.SaveSession(session);
        var jsonPath = Path.Combine(session.WorkspacePath, "session.json");
        if (Directory.Exists(session.WorkspacePath))
        {
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public void DeleteSession(string id)
    {
        var session = _registry.GetSession(id);
        if (session != null)
        {
            CloseSessionDb(id);

            if (Directory.Exists(session.WorkspacePath))
            {
                // Retry loop — SQLite may briefly hold the file lock after dispose
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        Directory.Delete(session.WorkspacePath, recursive: true);
                        break;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        Thread.Sleep(200 * (attempt + 1));
                    }
                }
            }

            _registry.DeleteSession(id);
        }
    }

    public SessionDb GetSessionDb(string sessionId)
    {
        if (_openDbs.TryGetValue(sessionId, out var db)) return db;

        var session = _registry.GetSession(sessionId);
        if (session == null) throw new InvalidOperationException($"Session {sessionId} not found");

        var dbPath = Path.Combine(session.WorkspacePath, "session.db");
        db = new SessionDb(dbPath);
        _openDbs[sessionId] = db;
        return db;
    }

    public void CloseSessionDb(string sessionId)
    {
        if (_openDbs.Remove(sessionId, out var db))
            db.Dispose();
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return sanitized.Length > 40 ? sanitized[..40] : sanitized;
    }

    public void Dispose()
    {
        foreach (var db in _openDbs.Values)
            db.Dispose();
        _openDbs.Clear();
        _registry.Dispose();
    }
}
