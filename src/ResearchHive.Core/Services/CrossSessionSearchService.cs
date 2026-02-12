using ResearchHive.Core.Data;
using ResearchHive.Core.Models;

namespace ResearchHive.Core.Services;

/// <summary>
/// Searches across ALL sessions' evidence databases. Returns results tagged with session info
/// so users can discover findings from past research without remembering which session had them.
/// </summary>
public class CrossSessionSearchService
{
    private readonly SessionManager _sessionManager;

    public CrossSessionSearchService(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// Search all sessions' chunks via FTS for a keyword/phrase query.
    /// Returns results grouped by session, ranked by relevance.
    /// </summary>
    public List<CrossSessionResult> SearchAll(string query, int maxPerSession = 5, int maxSessions = 20)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();

        var allSessions = _sessionManager.GetAllSessions();
        var results = new List<CrossSessionResult>();

        foreach (var session in allSessions.Take(maxSessions))
        {
            try
            {
                var db = _sessionManager.GetSessionDb(session.Id);
                var chunks = db.SearchChunksFts(query, maxPerSession);

                foreach (var chunk in chunks)
                {
                    results.Add(new CrossSessionResult
                    {
                        SessionId = session.Id,
                        SessionTitle = session.Title,
                        DomainPack = session.Pack,
                        Chunk = chunk,
                        SourceUrl = ResolveSourceUrl(db, chunk)
                    });
                }
            }
            catch
            {
                // Session DB may be corrupt or missing — skip silently
            }
        }

        return results;
    }

    /// <summary>
    /// Search all sessions' reports for keywords.
    /// </summary>
    public List<CrossSessionReportResult> SearchReports(string query, int maxPerSession = 3)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();

        var allSessions = _sessionManager.GetAllSessions();
        var results = new List<CrossSessionReportResult>();

        foreach (var session in allSessions)
        {
            try
            {
                var db = _sessionManager.GetSessionDb(session.Id);
                var reports = db.GetReports();

                foreach (var report in reports.Take(maxPerSession))
                {
                    if (report.Content != null &&
                        report.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        // Extract a snippet around the match
                        var idx = report.Content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                        var start = Math.Max(0, idx - 100);
                        var end = Math.Min(report.Content.Length, idx + query.Length + 100);
                        var snippet = report.Content[start..end].Trim();
                        if (start > 0) snippet = "..." + snippet;
                        if (end < report.Content.Length) snippet += "...";

                        results.Add(new CrossSessionReportResult
                        {
                            SessionId = session.Id,
                            SessionTitle = session.Title,
                            ReportTitle = report.Title,
                            ReportType = report.ReportType,
                            Snippet = snippet,
                            CreatedUtc = report.CreatedUtc
                        });
                    }
                }
            }
            catch { }
        }

        return results;
    }

    /// <summary>
    /// Get aggregate stats across all sessions.
    /// </summary>
    public GlobalStats GetGlobalStats()
    {
        var sessions = _sessionManager.GetAllSessions();
        var stats = new GlobalStats { TotalSessions = sessions.Count };

        foreach (var session in sessions)
        {
            try
            {
                var db = _sessionManager.GetSessionDb(session.Id);
                stats.TotalEvidence += db.GetChunkCount();
                stats.TotalReports += db.GetReports().Count;
                stats.TotalSnapshots += db.GetSnapshots().Count;

                var domainKey = session.Pack.ToDisplayName();
                stats.SessionsByDomain.TryGetValue(domainKey, out var count);
                stats.SessionsByDomain[domainKey] = count + 1;
            }
            catch { }
        }

        return stats;
    }

    private static string ResolveSourceUrl(SessionDb db, Chunk chunk)
    {
        if (chunk.SourceType == "snapshot")
        {
            var snapshots = db.GetSnapshots();
            var snap = snapshots.FirstOrDefault(s => s.Id == chunk.SourceId);
            return snap?.Url ?? "";
        }
        return "";
    }
}

/// <summary>An evidence result from a cross-session search.</summary>
public class CrossSessionResult
{
    public string SessionId { get; set; } = "";
    public string SessionTitle { get; set; } = "";
    public DomainPack DomainPack { get; set; }
    public Chunk Chunk { get; set; } = new();
    public string SourceUrl { get; set; } = "";
}

/// <summary>A report match from a cross-session search.</summary>
public class CrossSessionReportResult
{
    public string SessionId { get; set; } = "";
    public string SessionTitle { get; set; } = "";
    public string ReportTitle { get; set; } = "";
    public string ReportType { get; set; } = "";
    public string Snippet { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Aggregate statistics across all sessions.</summary>
public class GlobalStats
{
    public int TotalSessions { get; set; }
    public int TotalEvidence { get; set; }
    public int TotalReports { get; set; }
    public int TotalSnapshots { get; set; }
    public Dictionary<string, int> SessionsByDomain { get; set; } = new();
}
