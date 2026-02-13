using Microsoft.Data.Sqlite;
using ResearchHive.Core.Models;
using System.Text.Json;

namespace ResearchHive.Core.Data;

/// <summary>
/// Global memory database — stores promoted chunks from all sessions.
/// Used by the Hive Mind for cross-session retrieval and strategy extraction.
/// Single-file SQLite at DataRootPath/global.db.
/// </summary>
public class GlobalDb : IDisposable
{
    private readonly SqliteConnection _conn;

    public GlobalDb(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();

        using var wal = _conn.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL;";
        wal.ExecuteNonQuery();

        InitSchema();
    }

    private void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS global_chunks (
                id TEXT PRIMARY KEY,
                session_id TEXT,
                job_id TEXT,
                source_type TEXT NOT NULL,
                repo_url TEXT,
                domain_pack TEXT,
                text TEXT NOT NULL,
                embedding BLOB,
                tags TEXT DEFAULT '[]',
                promoted_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_gc_source_type ON global_chunks(source_type);
            CREATE INDEX IF NOT EXISTS idx_gc_domain_pack ON global_chunks(domain_pack);
            CREATE INDEX IF NOT EXISTS idx_gc_session ON global_chunks(session_id);

            CREATE VIRTUAL TABLE IF NOT EXISTS fts_global
                USING fts5(id, text, source_type, domain_pack, tokenize='porter unicode61');
        ";
        cmd.ExecuteNonQuery();
    }

    // ── Save / Query ──

    public void SaveChunk(GlobalChunk gc)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO global_chunks 
            (id, session_id, job_id, source_type, repo_url, domain_pack, text, embedding, tags, promoted_utc)
            VALUES ($id,$sid,$jid,$st,$ru,$dp,$txt,$emb,$tags,$utc)";
        AddChunkParams(cmd, gc);
        cmd.ExecuteNonQuery();

        UpsertFts(gc);
    }

    public void SaveChunksBatch(IReadOnlyList<GlobalChunk> chunks)
    {
        if (chunks.Count == 0) return;

        using var tx = _conn.BeginTransaction();
        try
        {
            foreach (var gc in chunks)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT OR REPLACE INTO global_chunks 
                    (id, session_id, job_id, source_type, repo_url, domain_pack, text, embedding, tags, promoted_utc)
                    VALUES ($id,$sid,$jid,$st,$ru,$dp,$txt,$emb,$tags,$utc)";
                AddChunkParams(cmd, gc);
                cmd.ExecuteNonQuery();

                using var fts = _conn.CreateCommand();
                fts.Transaction = tx;
                fts.CommandText = @"INSERT OR REPLACE INTO fts_global(id, text, source_type, domain_pack)
                    VALUES ($id,$txt,$st,$dp)";
                fts.Parameters.AddWithValue("$id", gc.Id);
                fts.Parameters.AddWithValue("$txt", gc.Text);
                fts.Parameters.AddWithValue("$st", gc.SourceType);
                fts.Parameters.AddWithValue("$dp", gc.DomainPack ?? "");
                fts.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// BM25 full-text search with optional source_type and domain_pack filters.
    /// </summary>
    public List<(GlobalChunk chunk, float bm25)> SearchFtsBm25(string query, string? sourceTypeFilter = null, string? domainPackFilter = null, int limit = 40)
    {
        var results = new List<(GlobalChunk, float)>();
        using var cmd = _conn.CreateCommand();

        // FTS5 query via subquery, then join back to global_chunks for full data + filters
        var extraWhere = "";
        if (sourceTypeFilter != null)
            extraWhere += " AND g.source_type = $stf";
        if (domainPackFilter != null)
            extraWhere += " AND g.domain_pack = $dpf";

        cmd.CommandText = $@"SELECT g.*, f.rank as bm25_score
            FROM fts_global f
            JOIN global_chunks g ON g.id = f.id
            WHERE fts_global MATCH $q{extraWhere}
            ORDER BY f.rank LIMIT $lim";

        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$lim", limit);
        if (sourceTypeFilter != null) cmd.Parameters.AddWithValue("$stf", sourceTypeFilter);
        if (domainPackFilter != null) cmd.Parameters.AddWithValue("$dpf", domainPackFilter);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var gc = MapChunk(r);
            var bm25 = -r.GetFloat(r.GetOrdinal("bm25_score"));
            results.Add((gc, bm25));
        }
        return results;
    }

    /// <summary>Get all chunks with embeddings for semantic search.</summary>
    public List<GlobalChunk> GetAllChunksWithEmbeddings(string? sourceTypeFilter = null, string? domainPackFilter = null)
    {
        var list = new List<GlobalChunk>();
        using var cmd = _conn.CreateCommand();

        var conditions = new List<string>();
        if (sourceTypeFilter != null) conditions.Add("source_type = $stf");
        if (domainPackFilter != null) conditions.Add("domain_pack = $dpf");

        var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : "";
        cmd.CommandText = $"SELECT * FROM global_chunks {where}";
        if (sourceTypeFilter != null) cmd.Parameters.AddWithValue("$stf", sourceTypeFilter);
        if (domainPackFilter != null) cmd.Parameters.AddWithValue("$dpf", domainPackFilter);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var gc = MapChunk(r);
            if (gc.Embedding != null)
                list.Add(gc);
        }
        return list;
    }

    /// <summary>Get all strategy chunks.</summary>
    public List<GlobalChunk> GetStrategies(string? domainPack = null)
    {
        var list = new List<GlobalChunk>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = domainPack != null
            ? "SELECT * FROM global_chunks WHERE source_type = 'strategy' AND domain_pack = $dp ORDER BY promoted_utc DESC"
            : "SELECT * FROM global_chunks WHERE source_type = 'strategy' ORDER BY promoted_utc DESC";
        if (domainPack != null) cmd.Parameters.AddWithValue("$dp", domainPack);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapChunk(r));
        return list;
    }

    public int GetChunkCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM global_chunks";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Paginated listing of global chunks with optional filters.
    /// </summary>
    public List<GlobalChunk> GetChunks(int offset = 0, int limit = 50,
        string? sourceTypeFilter = null, string? domainPackFilter = null, string? sessionIdFilter = null)
    {
        using var cmd = _conn.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrEmpty(sourceTypeFilter))
        {
            where.Add("source_type = $st");
            cmd.Parameters.AddWithValue("$st", sourceTypeFilter);
        }
        if (!string.IsNullOrEmpty(domainPackFilter))
        {
            where.Add("domain_pack = $dp");
            cmd.Parameters.AddWithValue("$dp", domainPackFilter);
        }
        if (!string.IsNullOrEmpty(sessionIdFilter))
        {
            where.Add("session_id = $sid");
            cmd.Parameters.AddWithValue("$sid", sessionIdFilter);
        }

        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $@"SELECT id, session_id, job_id, source_type, repo_url, domain_pack,
            text, embedding, tags, promoted_utc
            FROM global_chunks {whereClause}
            ORDER BY promoted_utc DESC
            LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var list = new List<GlobalChunk>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapChunk(r));
        return list;
    }

    /// <summary>Get distinct source types present in the global store.</summary>
    public List<string> GetDistinctSourceTypes()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT source_type FROM global_chunks ORDER BY source_type";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void DeleteChunk(string id)
    {
        using var fts = _conn.CreateCommand();
        fts.CommandText = "DELETE FROM fts_global WHERE id = $id";
        fts.Parameters.AddWithValue("$id", id);
        fts.ExecuteNonQuery();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM global_chunks WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteBySession(string sessionId)
    {
        // Get chunk IDs first for FTS cleanup
        var ids = new List<string>();
        using (var sel = _conn.CreateCommand())
        {
            sel.CommandText = "SELECT id FROM global_chunks WHERE session_id = $sid";
            sel.Parameters.AddWithValue("$sid", sessionId);
            using var r = sel.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
        }

        if (ids.Count == 0) return;

        using var tx = _conn.BeginTransaction();
        try
        {
            foreach (var id in ids)
            {
                using var fts = _conn.CreateCommand();
                fts.Transaction = tx;
                fts.CommandText = "DELETE FROM fts_global WHERE id = $id";
                fts.Parameters.AddWithValue("$id", id);
                fts.ExecuteNonQuery();
            }

            using var del = _conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM global_chunks WHERE session_id = $sid";
            del.Parameters.AddWithValue("$sid", sessionId);
            del.ExecuteNonQuery();

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ── Helpers ──

    private static void AddChunkParams(SqliteCommand cmd, GlobalChunk gc)
    {
        cmd.Parameters.AddWithValue("$id", gc.Id);
        cmd.Parameters.AddWithValue("$sid", (object?)gc.SessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$jid", (object?)gc.JobId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$st", gc.SourceType);
        cmd.Parameters.AddWithValue("$ru", (object?)gc.RepoUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dp", (object?)gc.DomainPack ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$txt", gc.Text);
        cmd.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(gc.Tags));
        cmd.Parameters.AddWithValue("$utc", gc.PromotedUtc.ToString("O"));

        if (gc.Embedding != null)
        {
            var bytes = new byte[gc.Embedding.Length * 4];
            Buffer.BlockCopy(gc.Embedding, 0, bytes, 0, bytes.Length);
            cmd.Parameters.AddWithValue("$emb", bytes);
        }
        else
        {
            cmd.Parameters.AddWithValue("$emb", DBNull.Value);
        }
    }

    private void UpsertFts(GlobalChunk gc)
    {
        using var fts = _conn.CreateCommand();
        fts.CommandText = @"INSERT OR REPLACE INTO fts_global(id, text, source_type, domain_pack)
            VALUES ($id,$txt,$st,$dp)";
        fts.Parameters.AddWithValue("$id", gc.Id);
        fts.Parameters.AddWithValue("$txt", gc.Text);
        fts.Parameters.AddWithValue("$st", gc.SourceType);
        fts.Parameters.AddWithValue("$dp", gc.DomainPack ?? "");
        fts.ExecuteNonQuery();
    }

    private static GlobalChunk MapChunk(SqliteDataReader r)
    {
        var gc = new GlobalChunk
        {
            Id = r.GetString(0),
            SessionId = r.IsDBNull(1) ? null : r.GetString(1),
            JobId = r.IsDBNull(2) ? null : r.GetString(2),
            SourceType = r.GetString(3),
            RepoUrl = r.IsDBNull(4) ? null : r.GetString(4),
            DomainPack = r.IsDBNull(5) ? null : r.GetString(5),
            Text = r.GetString(6)
        };

        // Embedding (column 7)
        if (!r.IsDBNull(7))
        {
            var bytes = (byte[])r.GetValue(7);
            gc.Embedding = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, gc.Embedding, 0, bytes.Length);
        }

        // Tags (column 8)
        if (!r.IsDBNull(8))
            gc.Tags = JsonSerializer.Deserialize<List<string>>(r.GetString(8)) ?? new();

        // PromotedUtc (column 9)
        if (!r.IsDBNull(9))
            gc.PromotedUtc = DateTime.Parse(r.GetString(9));

        return gc;
    }

    public void Dispose()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch { }

        var connString = _conn.ConnectionString;
        _conn.Close();
        _conn.Dispose();
        SqliteConnection.ClearPool(new SqliteConnection(connString));
    }
}
