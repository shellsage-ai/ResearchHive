using Microsoft.Data.Sqlite;
using ResearchHive.Core.Models;
using System.Text.Json;

namespace ResearchHive.Core.Data;

/// <summary>
/// Per-session SQLite database for all session data
/// </summary>
public class SessionDb : IDisposable
{
    private readonly SqliteConnection _conn;

    public SessionDb(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitSchema();
    }

    private void InitSchema()
    {
        // Enable WAL mode for concurrent reads during writes (UI + runner)
        using var walCmd = _conn.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL;";
        walCmd.ExecuteNonQuery();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS artifacts (
                id TEXT PRIMARY KEY, session_id TEXT, original_name TEXT, content_type TEXT,
                size_bytes INTEGER, store_path TEXT, content_hash TEXT, ingested_utc TEXT,
                metadata TEXT
            );
            CREATE TABLE IF NOT EXISTS snapshots (
                id TEXT PRIMARY KEY, session_id TEXT, url TEXT, canonical_url TEXT, title TEXT,
                bundle_path TEXT, html_path TEXT, text_path TEXT, screenshot_path TEXT,
                extraction_path TEXT, captured_utc TEXT, http_status INTEGER,
                content_hash TEXT, is_blocked INTEGER, block_reason TEXT
            );
            CREATE TABLE IF NOT EXISTS captures (
                id TEXT PRIMARY KEY, session_id TEXT, image_path TEXT, ocr_text TEXT,
                boxes TEXT, captured_utc TEXT, source_description TEXT
            );
            CREATE TABLE IF NOT EXISTS chunks (
                id TEXT PRIMARY KEY, session_id TEXT, source_id TEXT, source_type TEXT,
                text TEXT, start_offset INTEGER, end_offset INTEGER, chunk_index INTEGER,
                embedding BLOB
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS fts_chunks USING fts5(
                id, text, source_id, source_type, content=chunks, content_rowid=rowid
            );
            CREATE TABLE IF NOT EXISTS citations (
                id TEXT PRIMARY KEY, session_id TEXT, job_id TEXT, type TEXT,
                source_id TEXT, chunk_id TEXT, start_offset INTEGER, end_offset INTEGER,
                page TEXT, box TEXT, excerpt TEXT, label TEXT
            );
            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY, session_id TEXT, type TEXT, state TEXT,
                prompt TEXT, plan TEXT, search_queries TEXT, search_lanes TEXT,
                acquired_source_ids TEXT, target_source_count INTEGER, max_iterations INTEGER,
                current_iteration INTEGER, created_utc TEXT, updated_utc TEXT, completed_utc TEXT,
                error_message TEXT, checkpoint_data TEXT,
                most_supported_view TEXT, credible_alternatives TEXT,
                executive_summary TEXT, full_report TEXT, activity_report TEXT,
                replay_entries TEXT
            );
            CREATE TABLE IF NOT EXISTS job_steps (
                id TEXT PRIMARY KEY, job_id TEXT, step_number INTEGER, action TEXT,
                detail TEXT, state_after TEXT, timestamp_utc TEXT, success INTEGER, error TEXT
            );
            CREATE TABLE IF NOT EXISTS claim_ledger (
                id TEXT PRIMARY KEY, job_id TEXT, claim TEXT, support TEXT,
                citation_ids TEXT, explanation TEXT
            );
            CREATE TABLE IF NOT EXISTS reports (
                id TEXT PRIMARY KEY, session_id TEXT, job_id TEXT, report_type TEXT,
                title TEXT, content TEXT, format TEXT, created_utc TEXT, file_path TEXT
            );
            CREATE TABLE IF NOT EXISTS safety_assessments (
                id TEXT PRIMARY KEY, session_id TEXT, source_id TEXT, level TEXT,
                recommended_env TEXT, minimum_ppe TEXT, hazards TEXT, disposal_notes TEXT,
                refs TEXT, notes TEXT
            );
            CREATE TABLE IF NOT EXISTS ip_assessments (
                id TEXT PRIMARY KEY, session_id TEXT, source_id TEXT, license_signal TEXT,
                uncertainty_level TEXT, risk_flags TEXT, design_around TEXT, notes TEXT
            );
            CREATE TABLE IF NOT EXISTS idea_cards (
                id TEXT PRIMARY KEY, session_id TEXT, job_id TEXT, title TEXT,
                hypothesis TEXT, mechanism TEXT, minimal_test_plan TEXT, risks TEXT,
                falsification TEXT, novelty_check TEXT, nearest_prior_art TEXT,
                prior_art_citation_ids TEXT, score REAL, score_breakdown TEXT,
                safety TEXT, created_utc TEXT
            );
            CREATE TABLE IF NOT EXISTS material_candidates (
                id TEXT PRIMARY KEY, session_id TEXT, job_id TEXT, name TEXT,
                category TEXT, fit_rationale TEXT, properties TEXT, citation_ids TEXT,
                safety TEXT, diy_feasibility TEXT, test_checklist TEXT,
                fit_score REAL, rank INTEGER
            );
            CREATE TABLE IF NOT EXISTS fusion_results (
                id TEXT PRIMARY KEY, session_id TEXT, job_id TEXT, mode TEXT,
                proposal TEXT, provenance_map TEXT, citation_ids TEXT,
                safety_notes TEXT, ip_notes TEXT, created_utc TEXT
            );
            CREATE TABLE IF NOT EXISTS notebook_entries (
                id TEXT PRIMARY KEY, session_id TEXT, title TEXT, content TEXT,
                created_utc TEXT, updated_utc TEXT, tags TEXT
            );
            CREATE TABLE IF NOT EXISTS qa_messages (
                id TEXT PRIMARY KEY, session_id TEXT, question TEXT, answer TEXT,
                scope TEXT, timestamp_utc TEXT
            );
            CREATE TABLE IF NOT EXISTS pinned_evidence (
                id TEXT PRIMARY KEY, session_id TEXT, chunk_id TEXT, source_id TEXT,
                source_type TEXT, text TEXT, score REAL, source_url TEXT, pinned_utc TEXT
            );
            CREATE TABLE IF NOT EXISTS audit_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp_utc TEXT,
                level TEXT, category TEXT, message TEXT, data TEXT
            );

            -- Indexes for fast lookups on foreign key and query columns
            CREATE INDEX IF NOT EXISTS idx_snapshots_canonical ON snapshots(canonical_url);
            CREATE INDEX IF NOT EXISTS idx_snapshots_content_hash ON snapshots(content_hash);
            CREATE INDEX IF NOT EXISTS idx_chunks_source ON chunks(source_id);
            CREATE INDEX IF NOT EXISTS idx_citations_job ON citations(job_id);
            CREATE INDEX IF NOT EXISTS idx_job_steps_job ON job_steps(job_id);
            CREATE INDEX IF NOT EXISTS idx_reports_job ON reports(job_id);
            CREATE INDEX IF NOT EXISTS idx_claim_ledger_job ON claim_ledger(job_id);
            CREATE INDEX IF NOT EXISTS idx_idea_cards_job ON idea_cards(job_id);
            CREATE INDEX IF NOT EXISTS idx_material_candidates_job ON material_candidates(job_id);
            CREATE INDEX IF NOT EXISTS idx_fusion_results_job ON fusion_results(job_id);
        ";
        cmd.ExecuteNonQuery();
    }

    // ---- Artifact CRUD ----
    public void SaveArtifact(Artifact a)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO artifacts 
            (id, session_id, original_name, content_type, size_bytes, store_path, content_hash, ingested_utc, metadata)
            VALUES ($id,$sid,$name,$ct,$size,$path,$hash,$utc,$meta)";
        cmd.Parameters.AddWithValue("$id", a.Id);
        cmd.Parameters.AddWithValue("$sid", a.SessionId);
        cmd.Parameters.AddWithValue("$name", a.OriginalName);
        cmd.Parameters.AddWithValue("$ct", a.ContentType);
        cmd.Parameters.AddWithValue("$size", a.SizeBytes);
        cmd.Parameters.AddWithValue("$path", a.StorePath);
        cmd.Parameters.AddWithValue("$hash", a.ContentHash);
        cmd.Parameters.AddWithValue("$utc", a.IngestedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$meta", JsonSerializer.Serialize(a.Metadata));
        cmd.ExecuteNonQuery();
    }

    public List<Artifact> GetArtifacts()
    {
        var list = new List<Artifact>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM artifacts ORDER BY ingested_utc DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Artifact
            {
                Id = r.GetString(0), SessionId = r.GetString(1), OriginalName = r.GetString(2),
                ContentType = r.GetString(3), SizeBytes = r.GetInt64(4), StorePath = r.GetString(5),
                ContentHash = r.GetString(6), IngestedUtc = DateTime.Parse(r.GetString(7)),
                Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(8)) ?? new()
            });
        }
        return list;
    }

    // ---- Snapshot CRUD ----
    public void SaveSnapshot(Snapshot s)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO snapshots 
            (id,session_id,url,canonical_url,title,bundle_path,html_path,text_path,screenshot_path,extraction_path,captured_utc,http_status,content_hash,is_blocked,block_reason)
            VALUES ($id,$sid,$url,$curl,$t,$bp,$hp,$tp,$sp,$ep,$utc,$hs,$ch,$ib,$br)";
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$sid", s.SessionId);
        cmd.Parameters.AddWithValue("$url", s.Url);
        cmd.Parameters.AddWithValue("$curl", s.CanonicalUrl);
        cmd.Parameters.AddWithValue("$t", s.Title);
        cmd.Parameters.AddWithValue("$bp", s.BundlePath);
        cmd.Parameters.AddWithValue("$hp", s.HtmlPath);
        cmd.Parameters.AddWithValue("$tp", s.TextPath);
        cmd.Parameters.AddWithValue("$sp", s.ScreenshotPath);
        cmd.Parameters.AddWithValue("$ep", (object?)s.ExtractionPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$utc", s.CapturedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$hs", s.HttpStatus);
        cmd.Parameters.AddWithValue("$ch", s.ContentHash);
        cmd.Parameters.AddWithValue("$ib", s.IsBlocked ? 1 : 0);
        cmd.Parameters.AddWithValue("$br", (object?)s.BlockReason ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<Snapshot> GetSnapshots()
    {
        var list = new List<Snapshot>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM snapshots ORDER BY captured_utc DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapSnapshot(r));
        return list;
    }

    public Snapshot? GetSnapshot(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM snapshots WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapSnapshot(r) : null;
    }

    public Snapshot? GetSnapshotByUrl(string url)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM snapshots WHERE canonical_url = $url AND is_blocked = 0 LIMIT 1";
        cmd.Parameters.AddWithValue("$url", url);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapSnapshot(r) : null;
    }

    /// <summary>
    /// Batch-loads snapshots by IDs in a single query. Eliminates N+1 pattern
    /// when building source URL maps from evidence results.
    /// </summary>
    public Dictionary<string, Snapshot> GetSnapshotsByIds(IEnumerable<string> ids)
    {
        var result = new Dictionary<string, Snapshot>();
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return result;

        using var cmd = _conn.CreateCommand();
        var paramNames = idList.Select((_, i) => $"$s{i}").ToList();
        cmd.CommandText = $"SELECT * FROM snapshots WHERE id IN ({string.Join(",", paramNames)})";
        for (int i = 0; i < idList.Count; i++)
            cmd.Parameters.AddWithValue($"$s{i}", idList[i]);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var snap = MapSnapshot(r);
            result[snap.Id] = snap;
        }
        return result;
    }

    /// <summary>
    /// Check if content hash already exists (non-blocked). Used to deduplicate
    /// identical content captured from different URLs.
    /// </summary>
    public Snapshot? GetSnapshotByContentHash(string contentHash)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM snapshots WHERE content_hash = $hash AND is_blocked = 0 LIMIT 1";
        cmd.Parameters.AddWithValue("$hash", contentHash);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapSnapshot(r) : null;
    }

    private static Snapshot MapSnapshot(SqliteDataReader r) => new()
    {
        Id = r.GetString(0), SessionId = r.GetString(1), Url = r.GetString(2),
        CanonicalUrl = r.GetString(3), Title = r.GetString(4), BundlePath = r.GetString(5),
        HtmlPath = r.GetString(6), TextPath = r.GetString(7), ScreenshotPath = r.GetString(8),
        ExtractionPath = r.IsDBNull(9) ? null : r.GetString(9),
        CapturedUtc = DateTime.Parse(r.GetString(10)), HttpStatus = r.GetInt32(11),
        ContentHash = r.GetString(12), IsBlocked = r.GetInt32(13) != 0,
        BlockReason = r.IsDBNull(14) ? null : r.GetString(14)
    };

    // ---- Capture CRUD ----
    public void SaveCapture(Capture c)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO captures 
            (id,session_id,image_path,ocr_text,boxes,captured_utc,source_description)
            VALUES ($id,$sid,$ip,$ot,$bx,$utc,$sd)";
        cmd.Parameters.AddWithValue("$id", c.Id);
        cmd.Parameters.AddWithValue("$sid", c.SessionId);
        cmd.Parameters.AddWithValue("$ip", c.ImagePath);
        cmd.Parameters.AddWithValue("$ot", (object?)c.OcrText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bx", JsonSerializer.Serialize(c.Boxes));
        cmd.Parameters.AddWithValue("$utc", c.CapturedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$sd", c.SourceDescription);
        cmd.ExecuteNonQuery();
    }

    public List<Capture> GetCaptures()
    {
        var list = new List<Capture>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM captures ORDER BY captured_utc DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Capture
            {
                Id = r.GetString(0), SessionId = r.GetString(1), ImagePath = r.GetString(2),
                OcrText = r.IsDBNull(3) ? null : r.GetString(3),
                Boxes = JsonSerializer.Deserialize<List<OcrBox>>(r.GetString(4)) ?? new(),
                CapturedUtc = DateTime.Parse(r.GetString(5)),
                SourceDescription = r.GetString(6)
            });
        }
        return list;
    }

    // ---- Chunk CRUD ----
    public void SaveChunk(Chunk c)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO chunks 
            (id,session_id,source_id,source_type,text,start_offset,end_offset,chunk_index,embedding)
            VALUES ($id,$sid,$srcid,$st,$txt,$so,$eo,$ci,$emb)";
        cmd.Parameters.AddWithValue("$id", c.Id);
        cmd.Parameters.AddWithValue("$sid", c.SessionId);
        cmd.Parameters.AddWithValue("$srcid", c.SourceId);
        cmd.Parameters.AddWithValue("$st", c.SourceType);
        cmd.Parameters.AddWithValue("$txt", c.Text);
        cmd.Parameters.AddWithValue("$so", c.StartOffset);
        cmd.Parameters.AddWithValue("$eo", c.EndOffset);
        cmd.Parameters.AddWithValue("$ci", c.ChunkIndex);
        if (c.Embedding != null)
        {
            var bytes = new byte[c.Embedding.Length * 4];
            Buffer.BlockCopy(c.Embedding, 0, bytes, 0, bytes.Length);
            cmd.Parameters.AddWithValue("$emb", bytes);
        }
        else
        {
            cmd.Parameters.AddWithValue("$emb", DBNull.Value);
        }
        cmd.ExecuteNonQuery();

        // update FTS
        using var fts = _conn.CreateCommand();
        fts.CommandText = @"INSERT OR REPLACE INTO fts_chunks(id, text, source_id, source_type) 
            VALUES ($id, $txt, $srcid, $st)";
        fts.Parameters.AddWithValue("$id", c.Id);
        fts.Parameters.AddWithValue("$txt", c.Text);
        fts.Parameters.AddWithValue("$srcid", c.SourceId);
        fts.Parameters.AddWithValue("$st", c.SourceType);
        fts.ExecuteNonQuery();
    }

    /// <summary>
    /// Saves multiple chunks in a single transaction. ~10x faster than individual saves
    /// because SQLite auto-commit per statement is extremely slow vs batched transactions.
    /// </summary>
    public void SaveChunksBatch(IReadOnlyList<Chunk> chunks)
    {
        if (chunks.Count == 0) return;

        using var transaction = _conn.BeginTransaction();
        try
        {
            foreach (var c in chunks)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"INSERT OR REPLACE INTO chunks 
                    (id,session_id,source_id,source_type,text,start_offset,end_offset,chunk_index,embedding)
                    VALUES ($id,$sid,$srcid,$st,$txt,$so,$eo,$ci,$emb)";
                cmd.Parameters.AddWithValue("$id", c.Id);
                cmd.Parameters.AddWithValue("$sid", c.SessionId);
                cmd.Parameters.AddWithValue("$srcid", c.SourceId);
                cmd.Parameters.AddWithValue("$st", c.SourceType);
                cmd.Parameters.AddWithValue("$txt", c.Text);
                cmd.Parameters.AddWithValue("$so", c.StartOffset);
                cmd.Parameters.AddWithValue("$eo", c.EndOffset);
                cmd.Parameters.AddWithValue("$ci", c.ChunkIndex);
                if (c.Embedding != null)
                {
                    var bytes = new byte[c.Embedding.Length * 4];
                    Buffer.BlockCopy(c.Embedding, 0, bytes, 0, bytes.Length);
                    cmd.Parameters.AddWithValue("$emb", bytes);
                }
                else
                {
                    cmd.Parameters.AddWithValue("$emb", DBNull.Value);
                }
                cmd.ExecuteNonQuery();

                using var fts = _conn.CreateCommand();
                fts.Transaction = transaction;
                fts.CommandText = @"INSERT OR REPLACE INTO fts_chunks(id, text, source_id, source_type) 
                    VALUES ($id, $txt, $srcid, $st)";
                fts.Parameters.AddWithValue("$id", c.Id);
                fts.Parameters.AddWithValue("$txt", c.Text);
                fts.Parameters.AddWithValue("$srcid", c.SourceId);
                fts.Parameters.AddWithValue("$st", c.SourceType);
                fts.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public List<Chunk> SearchChunksFts(string query, int limit = 20)
    {
        var list = new List<Chunk>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT c.* FROM chunks c 
            JOIN fts_chunks f ON c.id = f.id
            WHERE fts_chunks MATCH $q 
            ORDER BY rank LIMIT $lim";
        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapChunk(r));
        return list;
    }

    /// <summary>
    /// FTS5 BM25-scored search. Returns chunks with their BM25 relevance scores
    /// (FTS5 rank is negative BM25, so we negate it for positive scores).
    /// </summary>
    public List<(Chunk chunk, float bm25Score)> SearchChunksFtsBm25(string query, int limit = 40)
    {
        var list = new List<(Chunk chunk, float bm25Score)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT c.*, bm25(fts_chunks) as bm25_score 
            FROM chunks c 
            JOIN fts_chunks f ON c.id = f.id
            WHERE fts_chunks MATCH $q 
            ORDER BY bm25(fts_chunks) LIMIT $lim";
        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var chunk = MapChunk(r);
            var bm25 = -r.GetFloat(r.GetOrdinal("bm25_score")); // Negate: FTS5 returns negative BM25
            list.Add((chunk, bm25));
        }
        return list;
    }

    /// <summary>
    /// Get chunks by their source IDs. Used for candidate-filtered semantic search
    /// instead of loading ALL chunks.
    /// </summary>
    public List<Chunk> GetChunksBySourceIds(IEnumerable<string> sourceIds)
    {
        var list = new List<Chunk>();
        var ids = sourceIds.Distinct().ToList();
        if (ids.Count == 0) return list;

        using var cmd = _conn.CreateCommand();
        // Build parameterized IN clause
        var paramNames = ids.Select((_, i) => $"$s{i}").ToList();
        cmd.CommandText = $"SELECT * FROM chunks WHERE source_id IN ({string.Join(",", paramNames)})";
        for (int i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"$s{i}", ids[i]);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapChunk(r));
        return list;
    }

    public List<Chunk> GetAllChunks()
    {
        var list = new List<Chunk>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM chunks";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapChunk(r));
        return list;
    }

    private static Chunk MapChunk(SqliteDataReader r)
    {
        var c = new Chunk
        {
            Id = r.GetString(0), SessionId = r.GetString(1), SourceId = r.GetString(2),
            SourceType = r.GetString(3), Text = r.GetString(4),
            StartOffset = r.GetInt32(5), EndOffset = r.GetInt32(6), ChunkIndex = r.GetInt32(7)
        };
        if (!r.IsDBNull(8))
        {
            var bytes = (byte[])r.GetValue(8);
            c.Embedding = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, c.Embedding, 0, bytes.Length);
        }
        return c;
    }

    public int GetChunkCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ---- Citation CRUD ----
    public void SaveCitation(Citation c)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO citations 
            (id,session_id,job_id,type,source_id,chunk_id,start_offset,end_offset,page,box,excerpt,label)
            VALUES ($id,$sid,$jid,$t,$srcid,$cid,$so,$eo,$p,$bx,$ex,$lb)";
        cmd.Parameters.AddWithValue("$id", c.Id);
        cmd.Parameters.AddWithValue("$sid", c.SessionId);
        cmd.Parameters.AddWithValue("$jid", c.JobId);
        cmd.Parameters.AddWithValue("$t", c.Type.ToString());
        cmd.Parameters.AddWithValue("$srcid", c.SourceId);
        cmd.Parameters.AddWithValue("$cid", (object?)c.ChunkId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$so", (object?)c.StartOffset ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$eo", (object?)c.EndOffset ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", (object?)c.Page ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bx", c.Box != null ? JsonSerializer.Serialize(c.Box) : DBNull.Value);
        cmd.Parameters.AddWithValue("$ex", c.Excerpt);
        cmd.Parameters.AddWithValue("$lb", c.Label);
        cmd.ExecuteNonQuery();
    }

    public List<Citation> GetCitations(string jobId)
    {
        var list = new List<Citation>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM citations WHERE job_id = $jid";
        cmd.Parameters.AddWithValue("$jid", jobId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Citation
            {
                Id = r.GetString(0), SessionId = r.GetString(1), JobId = r.GetString(2),
                Type = Enum.TryParse<CitationType>(r.GetString(3), out var t) ? t : CitationType.WebSnapshot,
                SourceId = r.GetString(4), ChunkId = r.IsDBNull(5) ? null : r.GetString(5),
                StartOffset = r.IsDBNull(6) ? null : r.GetInt32(6),
                EndOffset = r.IsDBNull(7) ? null : r.GetInt32(7),
                Page = r.IsDBNull(8) ? null : r.GetString(8),
                Box = r.IsDBNull(9) ? null : JsonSerializer.Deserialize<OcrBox>(r.GetString(9)),
                Excerpt = r.GetString(10), Label = r.GetString(11)
            });
        }
        return list;
    }

    // ---- Job CRUD ----
    /// <summary>
    /// Lightweight state-only update. Avoids full JSON serialization of FullReport,
    /// ReplayEntries, etc. (~15x cheaper than SaveJob for state transitions).
    /// </summary>
    public void SaveJobState(string jobId, JobState state)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET state = $st, updated_utc = $utc WHERE id = $id";
        cmd.Parameters.AddWithValue("$st", state.ToString());
        cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", jobId);
        cmd.ExecuteNonQuery();
    }

    public void SaveJob(ResearchJob j)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO jobs 
            (id,session_id,type,state,prompt,plan,search_queries,search_lanes,acquired_source_ids,
            target_source_count,max_iterations,current_iteration,created_utc,updated_utc,completed_utc,
            error_message,checkpoint_data,most_supported_view,credible_alternatives,
            executive_summary,full_report,activity_report,replay_entries)
            VALUES ($id,$sid,$t,$st,$pr,$pl,$sq,$sl,$asi,$tsc,$mi,$ci,$cu,$uu,$cu2,$em,$cd,$msv,$ca,$es,$fr,$ar,$re)";
        cmd.Parameters.AddWithValue("$id", j.Id);
        cmd.Parameters.AddWithValue("$sid", j.SessionId);
        cmd.Parameters.AddWithValue("$t", j.Type.ToString());
        cmd.Parameters.AddWithValue("$st", j.State.ToString());
        cmd.Parameters.AddWithValue("$pr", j.Prompt);
        cmd.Parameters.AddWithValue("$pl", (object?)j.Plan ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sq", JsonSerializer.Serialize(j.SearchQueries));
        cmd.Parameters.AddWithValue("$sl", JsonSerializer.Serialize(j.SearchLanes));
        cmd.Parameters.AddWithValue("$asi", JsonSerializer.Serialize(j.AcquiredSourceIds));
        cmd.Parameters.AddWithValue("$tsc", j.TargetSourceCount);
        cmd.Parameters.AddWithValue("$mi", j.MaxIterations);
        cmd.Parameters.AddWithValue("$ci", j.CurrentIteration);
        cmd.Parameters.AddWithValue("$cu", j.CreatedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$uu", j.UpdatedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$cu2", j.CompletedUtc?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$em", (object?)j.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cd", (object?)j.CheckpointData ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$msv", (object?)j.MostSupportedView ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ca", (object?)j.CredibleAlternatives ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$es", (object?)j.ExecutiveSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fr", (object?)j.FullReport ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ar", (object?)j.ActivityReport ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$re", JsonSerializer.Serialize(j.ReplayEntries));
        cmd.ExecuteNonQuery();
    }

    public ResearchJob? GetJob(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM jobs WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapJob(r) : null;
    }

    public List<ResearchJob> GetJobs()
    {
        var list = new List<ResearchJob>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM jobs ORDER BY updated_utc DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(MapJob(r));
        return list;
    }

    private static ResearchJob MapJob(SqliteDataReader r) => new()
    {
        Id = r.GetString(0), SessionId = r.GetString(1),
        Type = Enum.TryParse<JobType>(r.GetString(2), out var jt) ? jt : JobType.Research,
        State = Enum.TryParse<JobState>(r.GetString(3), out var js) ? js : JobState.Pending,
        Prompt = r.GetString(4), Plan = r.IsDBNull(5) ? null : r.GetString(5),
        SearchQueries = JsonSerializer.Deserialize<List<string>>(r.GetString(6)) ?? new(),
        SearchLanes = JsonSerializer.Deserialize<List<string>>(r.GetString(7)) ?? new(),
        AcquiredSourceIds = JsonSerializer.Deserialize<List<string>>(r.GetString(8)) ?? new(),
        TargetSourceCount = r.GetInt32(9), MaxIterations = r.GetInt32(10),
        CurrentIteration = r.GetInt32(11), CreatedUtc = DateTime.Parse(r.GetString(12)),
        UpdatedUtc = DateTime.Parse(r.GetString(13)),
        CompletedUtc = r.IsDBNull(14) ? null : DateTime.Parse(r.GetString(14)),
        ErrorMessage = r.IsDBNull(15) ? null : r.GetString(15),
        CheckpointData = r.IsDBNull(16) ? null : r.GetString(16),
        MostSupportedView = r.IsDBNull(17) ? null : r.GetString(17),
        CredibleAlternatives = r.IsDBNull(18) ? null : r.GetString(18),
        ExecutiveSummary = r.IsDBNull(19) ? null : r.GetString(19),
        FullReport = r.IsDBNull(20) ? null : r.GetString(20),
        ActivityReport = r.IsDBNull(21) ? null : r.GetString(21),
        ReplayEntries = JsonSerializer.Deserialize<List<ReplayEntry>>(r.GetString(22)) ?? new()
    };

    // ---- JobStep CRUD ----
    public void SaveJobStep(JobStep s)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO job_steps 
            (id,job_id,step_number,action,detail,state_after,timestamp_utc,success,error)
            VALUES ($id,$jid,$sn,$a,$d,$sa,$utc,$s,$e)";
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$jid", s.JobId);
        cmd.Parameters.AddWithValue("$sn", s.StepNumber);
        cmd.Parameters.AddWithValue("$a", s.Action);
        cmd.Parameters.AddWithValue("$d", s.Detail);
        cmd.Parameters.AddWithValue("$sa", s.StateAfter.ToString());
        cmd.Parameters.AddWithValue("$utc", s.TimestampUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$s", s.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("$e", (object?)s.Error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<JobStep> GetJobSteps(string jobId)
    {
        var list = new List<JobStep>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM job_steps WHERE job_id = $jid ORDER BY step_number";
        cmd.Parameters.AddWithValue("$jid", jobId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new JobStep
            {
                Id = r.GetString(0), JobId = r.GetString(1), StepNumber = r.GetInt32(2),
                Action = r.GetString(3), Detail = r.GetString(4),
                StateAfter = Enum.TryParse<JobState>(r.GetString(5), out var s) ? s : JobState.Pending,
                TimestampUtc = DateTime.Parse(r.GetString(6)),
                Success = r.GetInt32(7) != 0, Error = r.IsDBNull(8) ? null : r.GetString(8)
            });
        }
        return list;
    }

    // ---- Report CRUD ----
    public void SaveReport(Report rep)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO reports 
            (id,session_id,job_id,report_type,title,content,format,created_utc,file_path)
            VALUES ($id,$sid,$jid,$rt,$t,$c,$f,$utc,$fp)";
        cmd.Parameters.AddWithValue("$id", rep.Id);
        cmd.Parameters.AddWithValue("$sid", rep.SessionId);
        cmd.Parameters.AddWithValue("$jid", rep.JobId);
        cmd.Parameters.AddWithValue("$rt", rep.ReportType);
        cmd.Parameters.AddWithValue("$t", rep.Title);
        cmd.Parameters.AddWithValue("$c", rep.Content);
        cmd.Parameters.AddWithValue("$f", rep.Format);
        cmd.Parameters.AddWithValue("$utc", rep.CreatedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$fp", rep.FilePath);
        cmd.ExecuteNonQuery();
    }

    public List<Report> GetReports(string? jobId = null)
    {
        var list = new List<Report>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = jobId != null
            ? "SELECT * FROM reports WHERE job_id = $jid ORDER BY created_utc DESC"
            : "SELECT * FROM reports ORDER BY created_utc DESC";
        if (jobId != null) cmd.Parameters.AddWithValue("$jid", jobId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Report
            {
                Id = r.GetString(0), SessionId = r.GetString(1), JobId = r.GetString(2),
                ReportType = r.GetString(3), Title = r.GetString(4), Content = r.GetString(5),
                Format = r.GetString(6), CreatedUtc = DateTime.Parse(r.GetString(7)),
                FilePath = r.GetString(8)
            });
        }
        return list;
    }

    // ---- Idea Card CRUD ----
    public void SaveIdeaCard(IdeaCard ic)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO idea_cards 
            (id,session_id,job_id,title,hypothesis,mechanism,minimal_test_plan,risks,falsification,
            novelty_check,nearest_prior_art,prior_art_citation_ids,score,score_breakdown,safety,created_utc)
            VALUES ($id,$sid,$jid,$t,$h,$m,$mtp,$r,$f,$nc,$npa,$paci,$s,$sb,$sf,$utc)";
        cmd.Parameters.AddWithValue("$id", ic.Id);
        cmd.Parameters.AddWithValue("$sid", ic.SessionId);
        cmd.Parameters.AddWithValue("$jid", ic.JobId);
        cmd.Parameters.AddWithValue("$t", ic.Title);
        cmd.Parameters.AddWithValue("$h", ic.Hypothesis);
        cmd.Parameters.AddWithValue("$m", ic.Mechanism);
        cmd.Parameters.AddWithValue("$mtp", ic.MinimalTestPlan);
        cmd.Parameters.AddWithValue("$r", JsonSerializer.Serialize(ic.Risks));
        cmd.Parameters.AddWithValue("$f", ic.Falsification);
        cmd.Parameters.AddWithValue("$nc", (object?)ic.NoveltyCheck ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$npa", (object?)ic.NearestPriorArt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$paci", JsonSerializer.Serialize(ic.PriorArtCitationIds));
        cmd.Parameters.AddWithValue("$s", (object?)ic.Score ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sb", JsonSerializer.Serialize(ic.ScoreBreakdown));
        cmd.Parameters.AddWithValue("$sf", ic.Safety != null ? JsonSerializer.Serialize(ic.Safety) : DBNull.Value);
        cmd.Parameters.AddWithValue("$utc", ic.CreatedUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public List<IdeaCard> GetIdeaCards(string? jobId = null)
    {
        var list = new List<IdeaCard>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = jobId != null
            ? "SELECT * FROM idea_cards WHERE job_id = $jid"
            : "SELECT * FROM idea_cards";
        if (jobId != null) cmd.Parameters.AddWithValue("$jid", jobId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new IdeaCard
            {
                Id = r.GetString(0), SessionId = r.GetString(1), JobId = r.GetString(2),
                Title = r.GetString(3), Hypothesis = r.GetString(4), Mechanism = r.GetString(5),
                MinimalTestPlan = r.GetString(6),
                Risks = JsonSerializer.Deserialize<List<string>>(r.GetString(7)) ?? new(),
                Falsification = r.GetString(8),
                NoveltyCheck = r.IsDBNull(9) ? null : r.GetString(9),
                NearestPriorArt = r.IsDBNull(10) ? null : r.GetString(10),
                PriorArtCitationIds = JsonSerializer.Deserialize<List<string>>(r.GetString(11)) ?? new(),
                Score = r.IsDBNull(12) ? null : r.GetDouble(12),
                ScoreBreakdown = JsonSerializer.Deserialize<Dictionary<string, double>>(r.GetString(13)) ?? new(),
                Safety = r.IsDBNull(14) ? null : JsonSerializer.Deserialize<SafetyAssessment>(r.GetString(14)),
                CreatedUtc = DateTime.Parse(r.GetString(15))
            });
        }
        return list;
    }

    // ---- Material Candidate CRUD ----
    public void SaveMaterialCandidate(MaterialCandidate mc)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO material_candidates 
            (id,session_id,job_id,name,category,fit_rationale,properties,citation_ids,safety,diy_feasibility,test_checklist,fit_score,rank)
            VALUES ($id,$sid,$jid,$n,$c,$fr,$pr,$ci,$sf,$df,$tc,$fs,$rk)";
        cmd.Parameters.AddWithValue("$id", mc.Id);
        cmd.Parameters.AddWithValue("$sid", mc.SessionId);
        cmd.Parameters.AddWithValue("$jid", mc.JobId);
        cmd.Parameters.AddWithValue("$n", mc.Name);
        cmd.Parameters.AddWithValue("$c", mc.Category);
        cmd.Parameters.AddWithValue("$fr", mc.FitRationale);
        cmd.Parameters.AddWithValue("$pr", JsonSerializer.Serialize(mc.Properties));
        cmd.Parameters.AddWithValue("$ci", JsonSerializer.Serialize(mc.CitationIds));
        cmd.Parameters.AddWithValue("$sf", mc.Safety != null ? JsonSerializer.Serialize(mc.Safety) : DBNull.Value);
        cmd.Parameters.AddWithValue("$df", mc.DiyFeasibility);
        cmd.Parameters.AddWithValue("$tc", JsonSerializer.Serialize(mc.TestChecklist));
        cmd.Parameters.AddWithValue("$fs", mc.FitScore);
        cmd.Parameters.AddWithValue("$rk", mc.Rank);
        cmd.ExecuteNonQuery();
    }

    public List<MaterialCandidate> GetMaterialCandidates(string? jobId = null)
    {
        var list = new List<MaterialCandidate>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = jobId != null
            ? "SELECT * FROM material_candidates WHERE job_id = $jid ORDER BY rank"
            : "SELECT * FROM material_candidates ORDER BY rank";
        if (jobId != null) cmd.Parameters.AddWithValue("$jid", jobId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new MaterialCandidate
            {
                Id = r.GetString(0), SessionId = r.GetString(1), JobId = r.GetString(2),
                Name = r.GetString(3), Category = r.GetString(4), FitRationale = r.GetString(5),
                Properties = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(6)) ?? new(),
                CitationIds = JsonSerializer.Deserialize<List<string>>(r.GetString(7)) ?? new(),
                Safety = r.IsDBNull(8) ? null : JsonSerializer.Deserialize<SafetyAssessment>(r.GetString(8)),
                DiyFeasibility = r.GetString(9),
                TestChecklist = JsonSerializer.Deserialize<List<string>>(r.GetString(10)) ?? new(),
                FitScore = r.GetDouble(11), Rank = r.GetInt32(12)
            });
        }
        return list;
    }

    // ---- Fusion Result CRUD ----
    public void SaveFusionResult(FusionResult fr)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO fusion_results 
            (id,session_id,job_id,mode,proposal,provenance_map,citation_ids,safety_notes,ip_notes,created_utc)
            VALUES ($id,$sid,$jid,$m,$p,$pm,$ci,$sn,$in,$utc)";
        cmd.Parameters.AddWithValue("$id", fr.Id);
        cmd.Parameters.AddWithValue("$sid", fr.SessionId);
        cmd.Parameters.AddWithValue("$jid", fr.JobId);
        cmd.Parameters.AddWithValue("$m", fr.Mode.ToString());
        cmd.Parameters.AddWithValue("$p", fr.Proposal);
        cmd.Parameters.AddWithValue("$pm", JsonSerializer.Serialize(fr.ProvenanceMap));
        cmd.Parameters.AddWithValue("$ci", JsonSerializer.Serialize(fr.CitationIds));
        cmd.Parameters.AddWithValue("$sn", fr.SafetyNotes != null ? JsonSerializer.Serialize(fr.SafetyNotes) : DBNull.Value);
        cmd.Parameters.AddWithValue("$in", fr.IpNotes != null ? JsonSerializer.Serialize(fr.IpNotes) : DBNull.Value);
        cmd.Parameters.AddWithValue("$utc", fr.CreatedUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public List<FusionResult> GetFusionResults(string? jobId = null)
    {
        var list = new List<FusionResult>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = jobId != null
            ? "SELECT * FROM fusion_results WHERE job_id = $jid ORDER BY created_utc DESC"
            : "SELECT * FROM fusion_results ORDER BY created_utc DESC";
        if (jobId != null) cmd.Parameters.AddWithValue("$jid", jobId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new FusionResult
            {
                Id = r.GetString(0), SessionId = r.GetString(1), JobId = r.GetString(2),
                Mode = Enum.TryParse<FusionMode>(r.GetString(3), out var fm) ? fm : FusionMode.Blend,
                Proposal = r.GetString(4),
                ProvenanceMap = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(5)) ?? new(),
                CitationIds = JsonSerializer.Deserialize<List<string>>(r.GetString(6)) ?? new(),
                SafetyNotes = r.IsDBNull(7) ? null : JsonSerializer.Deserialize<SafetyAssessment>(r.GetString(7)),
                IpNotes = r.IsDBNull(8) ? null : JsonSerializer.Deserialize<IpAssessment>(r.GetString(8)),
                CreatedUtc = DateTime.Parse(r.GetString(9))
            });
        }
        return list;
    }

    // ---- Notebook CRUD ----
    public void SaveNotebookEntry(NotebookEntry ne)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO notebook_entries 
            (id,session_id,title,content,created_utc,updated_utc,tags)
            VALUES ($id,$sid,$t,$c,$cu,$uu,$tg)";
        cmd.Parameters.AddWithValue("$id", ne.Id);
        cmd.Parameters.AddWithValue("$sid", ne.SessionId);
        cmd.Parameters.AddWithValue("$t", ne.Title);
        cmd.Parameters.AddWithValue("$c", ne.Content);
        cmd.Parameters.AddWithValue("$cu", ne.CreatedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$uu", ne.UpdatedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$tg", JsonSerializer.Serialize(ne.Tags));
        cmd.ExecuteNonQuery();
    }

    public List<NotebookEntry> GetNotebookEntries()
    {
        var list = new List<NotebookEntry>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM notebook_entries ORDER BY updated_utc DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new NotebookEntry
            {
                Id = r.GetString(0), SessionId = r.GetString(1), Title = r.GetString(2),
                Content = r.GetString(3), CreatedUtc = DateTime.Parse(r.GetString(4)),
                UpdatedUtc = DateTime.Parse(r.GetString(5)),
                Tags = JsonSerializer.Deserialize<List<string>>(r.GetString(6)) ?? new()
            });
        }
        return list;
    }

    // ---- Q&A Message CRUD ----
    public void SaveQaMessage(QaMessage msg)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO qa_messages 
            (id,session_id,question,answer,scope,timestamp_utc)
            VALUES ($id,$sid,$q,$a,$sc,$utc)";
        cmd.Parameters.AddWithValue("$id", msg.Id);
        cmd.Parameters.AddWithValue("$sid", msg.SessionId);
        cmd.Parameters.AddWithValue("$q", msg.Question);
        cmd.Parameters.AddWithValue("$a", msg.Answer);
        cmd.Parameters.AddWithValue("$sc", msg.Scope);
        cmd.Parameters.AddWithValue("$utc", msg.TimestampUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public List<QaMessage> GetQaMessages()
    {
        var list = new List<QaMessage>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM qa_messages ORDER BY timestamp_utc ASC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new QaMessage
            {
                Id = r.GetString(0), SessionId = r.GetString(1),
                Question = r.GetString(2), Answer = r.GetString(3),
                Scope = r.GetString(4), TimestampUtc = DateTime.Parse(r.GetString(5))
            });
        }
        return list;
    }

    public void DeleteQaMessage(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM qa_messages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---- Pinned Evidence CRUD ----
    public void SavePinnedEvidence(PinnedEvidence pe)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO pinned_evidence 
            (id,session_id,chunk_id,source_id,source_type,text,score,source_url,pinned_utc)
            VALUES ($id,$sid,$cid,$srcid,$st,$txt,$sc,$url,$utc)";
        cmd.Parameters.AddWithValue("$id", pe.Id);
        cmd.Parameters.AddWithValue("$sid", pe.SessionId);
        cmd.Parameters.AddWithValue("$cid", pe.ChunkId);
        cmd.Parameters.AddWithValue("$srcid", pe.SourceId);
        cmd.Parameters.AddWithValue("$st", pe.SourceType);
        cmd.Parameters.AddWithValue("$txt", pe.Text);
        cmd.Parameters.AddWithValue("$sc", pe.Score);
        cmd.Parameters.AddWithValue("$url", pe.SourceUrl);
        cmd.Parameters.AddWithValue("$utc", pe.PinnedUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public List<PinnedEvidence> GetPinnedEvidence()
    {
        var list = new List<PinnedEvidence>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM pinned_evidence ORDER BY pinned_utc DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PinnedEvidence
            {
                Id = r.GetString(0), SessionId = r.GetString(1),
                ChunkId = r.GetString(2), SourceId = r.GetString(3),
                SourceType = r.GetString(4), Text = r.GetString(5),
                Score = r.GetFloat(6), SourceUrl = r.GetString(7),
                PinnedUtc = DateTime.Parse(r.GetString(8))
            });
        }
        return list;
    }

    public void DeletePinnedEvidence(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM pinned_evidence WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---- Safety Assessment CRUD ----
    public void SaveSafetyAssessment(SafetyAssessment sa)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO safety_assessments 
            (id,session_id,source_id,level,recommended_env,minimum_ppe,hazards,disposal_notes,refs,notes)
            VALUES ($id,$sid,$srcid,$l,$re,$mp,$h,$dn,$rf,$n)";
        cmd.Parameters.AddWithValue("$id", sa.Id);
        cmd.Parameters.AddWithValue("$sid", sa.SessionId);
        cmd.Parameters.AddWithValue("$srcid", sa.SourceId);
        cmd.Parameters.AddWithValue("$l", sa.Level.ToString());
        cmd.Parameters.AddWithValue("$re", sa.RecommendedEnvironment);
        cmd.Parameters.AddWithValue("$mp", JsonSerializer.Serialize(sa.MinimumPPE));
        cmd.Parameters.AddWithValue("$h", JsonSerializer.Serialize(sa.Hazards));
        cmd.Parameters.AddWithValue("$dn", JsonSerializer.Serialize(sa.DisposalNotes));
        cmd.Parameters.AddWithValue("$rf", JsonSerializer.Serialize(sa.References));
        cmd.Parameters.AddWithValue("$n", sa.Notes);
        cmd.ExecuteNonQuery();
    }

    // ---- IP Assessment CRUD ----
    public void SaveIpAssessment(IpAssessment ip)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO ip_assessments 
            (id,session_id,source_id,license_signal,uncertainty_level,risk_flags,design_around,notes)
            VALUES ($id,$sid,$srcid,$ls,$ul,$rf,$da,$n)";
        cmd.Parameters.AddWithValue("$id", ip.Id);
        cmd.Parameters.AddWithValue("$sid", ip.SessionId);
        cmd.Parameters.AddWithValue("$srcid", ip.SourceId);
        cmd.Parameters.AddWithValue("$ls", ip.LicenseSignal);
        cmd.Parameters.AddWithValue("$ul", ip.UncertaintyLevel);
        cmd.Parameters.AddWithValue("$rf", JsonSerializer.Serialize(ip.RiskFlags));
        cmd.Parameters.AddWithValue("$da", JsonSerializer.Serialize(ip.DesignAroundOptions));
        cmd.Parameters.AddWithValue("$n", ip.Notes);
        cmd.ExecuteNonQuery();
    }

    // ---- Audit Log ----
    public void Log(string level, string category, string message, Dictionary<string, string>? data = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO audit_log (timestamp_utc, level, category, message, data) 
            VALUES ($utc, $l, $c, $m, $d)";
        cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$l", level);
        cmd.Parameters.AddWithValue("$c", category);
        cmd.Parameters.AddWithValue("$m", message);
        cmd.Parameters.AddWithValue("$d", data != null ? JsonSerializer.Serialize(data) : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ---- Claim Ledger CRUD ----
    public void SaveClaim(ClaimLedger cl)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO claim_ledger 
            (id,job_id,claim,support,citation_ids,explanation)
            VALUES ($id,$jid,$c,$s,$ci,$e)";
        cmd.Parameters.AddWithValue("$id", cl.Id);
        cmd.Parameters.AddWithValue("$jid", cl.JobId);
        cmd.Parameters.AddWithValue("$c", cl.Claim);
        cmd.Parameters.AddWithValue("$s", cl.Support);
        cmd.Parameters.AddWithValue("$ci", JsonSerializer.Serialize(cl.CitationIds));
        cmd.Parameters.AddWithValue("$e", (object?)cl.Explanation ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<ClaimLedger> GetClaimLedger(string jobId)
    {
        var list = new List<ClaimLedger>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM claim_ledger WHERE job_id = $jid";
        cmd.Parameters.AddWithValue("$jid", jobId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ClaimLedger
            {
                Id = r["id"]?.ToString() ?? "",
                JobId = r["job_id"]?.ToString() ?? "",
                Claim = r["claim"]?.ToString() ?? "",
                Support = r["support"]?.ToString() ?? "",
                CitationIds = JsonSerializer.Deserialize<List<string>>(r["citation_ids"]?.ToString() ?? "[]") ?? new(),
                Explanation = r["explanation"]?.ToString()
            });
        }
        return list;
    }

    // =========================================================================
    //  DELETE OPERATIONS — per-entity removal to keep sessions tidy
    // =========================================================================

    /// <summary>
    /// Delete a job and all related data (steps, reports, citations, claims, idea cards, 
    /// material candidates, fusion results). Does NOT delete snapshots since they 
    /// may be shared across jobs.
    /// </summary>
    public void DeleteJob(string jobId)
    {
        var tables = new[]
        {
            ("job_steps", "job_id"),
            ("reports", "job_id"),
            ("citations", "job_id"),
            ("claim_ledger", "job_id"),
            ("idea_cards", "job_id"),
            ("material_candidates", "job_id"),
            ("fusion_results", "job_id"),
        };
        foreach (var (table, col) in tables)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {table} WHERE {col} = $id";
            cmd.Parameters.AddWithValue("$id", jobId);
            cmd.ExecuteNonQuery();
        }
        using var del = _conn.CreateCommand();
        del.CommandText = "DELETE FROM jobs WHERE id = $id";
        del.Parameters.AddWithValue("$id", jobId);
        del.ExecuteNonQuery();
    }

    /// <summary>Delete a single report by ID.</summary>
    public void DeleteReport(string reportId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM reports WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", reportId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Delete a snapshot and its associated chunks/FTS.</summary>
    public void DeleteSnapshot(string snapshotId)
    {
        // Delete chunks indexed from this snapshot
        DeleteChunksBySource(snapshotId);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM snapshots WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", snapshotId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Delete a notebook entry.</summary>
    public void DeleteNotebookEntry(string entryId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM notebook_entries WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Delete an idea card.</summary>
    public void DeleteIdeaCard(string cardId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM idea_cards WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Delete a material candidate.</summary>
    public void DeleteMaterialCandidate(string candidateId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM material_candidates WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", candidateId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Delete a fusion result.</summary>
    public void DeleteFusionResult(string resultId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM fusion_results WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", resultId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Delete an artifact and its associated chunks/FTS.</summary>
    public void DeleteArtifact(string artifactId)
    {
        DeleteChunksBySource(artifactId);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM artifacts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", artifactId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Delete a capture and its associated chunks/FTS.</summary>
    public void DeleteCapture(string captureId)
    {
        DeleteChunksBySource(captureId);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM captures WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", captureId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Remove all chunks (and their FTS entries) belonging to a given source.</summary>
    private void DeleteChunksBySource(string sourceId)
    {
        // Remove FTS entries first
        using var fts = _conn.CreateCommand();
        fts.CommandText = "DELETE FROM fts_chunks WHERE source_id = $sid";
        fts.Parameters.AddWithValue("$sid", sourceId);
        fts.ExecuteNonQuery();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM chunks WHERE source_id = $sid";
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        // Checkpoint WAL to flush journal files before closing
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch { /* best effort */ }

        var connString = _conn.ConnectionString;
        _conn.Close();
        _conn.Dispose();

        // Clear the SQLite connection pool so the file is fully released
        SqliteConnection.ClearPool(new SqliteConnection(connString));
    }
}
