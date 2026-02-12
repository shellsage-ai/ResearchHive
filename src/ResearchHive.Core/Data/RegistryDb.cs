using Microsoft.Data.Sqlite;
using ResearchHive.Core.Models;
using System.Text.Json;

namespace ResearchHive.Core.Data;

/// <summary>
/// Manages the global registry database (sessions index)
/// </summary>
public class RegistryDb : IDisposable
{
    private readonly SqliteConnection _conn;

    public RegistryDb(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitSchema();
    }

    private void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                description TEXT,
                pack TEXT NOT NULL,
                status TEXT NOT NULL,
                tags TEXT,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                workspace_path TEXT NOT NULL,
                last_report_summary TEXT,
                last_report_path TEXT
            );
        ";
        cmd.ExecuteNonQuery();
    }

    public void SaveSession(Session s)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO sessions 
            (id, title, description, pack, status, tags, created_utc, updated_utc, workspace_path, last_report_summary, last_report_path)
            VALUES ($id, $title, $desc, $pack, $status, $tags, $created, $updated, $path, $summary, $reportPath)";
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$title", s.Title);
        cmd.Parameters.AddWithValue("$desc", s.Description ?? "");
        cmd.Parameters.AddWithValue("$pack", s.Pack.ToString());
        cmd.Parameters.AddWithValue("$status", s.Status.ToString());
        cmd.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(s.Tags));
        cmd.Parameters.AddWithValue("$created", s.CreatedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", s.UpdatedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$path", s.WorkspacePath);
        cmd.Parameters.AddWithValue("$summary", (object?)s.LastReportSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$reportPath", (object?)s.LastReportPath ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<Session> GetAllSessions()
    {
        var list = new List<Session>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM sessions ORDER BY updated_utc DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(MapSession(reader));
        }
        return list;
    }

    public Session? GetSession(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM sessions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapSession(reader) : null;
    }

    public List<Session> SearchSessions(string query)
    {
        var list = new List<Session>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT * FROM sessions 
            WHERE title LIKE $q OR description LIKE $q OR tags LIKE $q 
            ORDER BY updated_utc DESC";
        cmd.Parameters.AddWithValue("$q", $"%{query}%");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(MapSession(reader));
        }
        return list;
    }

    public void DeleteSession(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static Session MapSession(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Title = r.GetString(1),
        Description = r.IsDBNull(2) ? "" : r.GetString(2),
        Pack = Enum.TryParse<DomainPack>(r.GetString(3), out var p) ? p : DomainPack.GeneralResearch,
        Status = Enum.TryParse<SessionStatus>(r.GetString(4), out var s) ? s : SessionStatus.Active,
        Tags = JsonSerializer.Deserialize<List<string>>(r.IsDBNull(5) ? "[]" : r.GetString(5)) ?? new(),
        CreatedUtc = DateTime.Parse(r.GetString(6)),
        UpdatedUtc = DateTime.Parse(r.GetString(7)),
        WorkspacePath = r.GetString(8),
        LastReportSummary = r.IsDBNull(9) ? null : r.GetString(9),
        LastReportPath = r.IsDBNull(10) ? null : r.GetString(10),
    };

    public void Dispose() => _conn.Dispose();
}
