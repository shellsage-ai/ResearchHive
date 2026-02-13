using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ResearchHive.Core.Services;

/// <summary>
/// Web snapshot capture service. Uses HttpClient for HTML/text capture with
/// polite browsing rules enforced via CourtesyPolicy. Full Playwright integration
/// is optional and will be used if available.
/// </summary>
public partial class SnapshotService
{
    private readonly SessionManager _sessionManager;
    private readonly CourtesyPolicy _courtesy;
    private readonly AppSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly ArtifactStore _artifactStore;
    private readonly IndexService _indexService;
    private readonly List<RequestLog> _requestLogs = new();

    public SnapshotService(SessionManager sessionManager, CourtesyPolicy courtesy, AppSettings settings,
        ArtifactStore artifactStore, IndexService indexService)
    {
        _sessionManager = sessionManager;
        _courtesy = courtesy;
        _settings = settings;
        _artifactStore = artifactStore;
        _indexService = indexService;

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(_settings.UserAgentString);
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    }

    public async Task<Snapshot> CaptureUrlAsync(string sessionId, string url, bool forceRefresh = false, CancellationToken ct = default)
    {
        var session = _sessionManager.GetSession(sessionId)
            ?? throw new InvalidOperationException($"Session {sessionId} not found");
        var db = _sessionManager.GetSessionDb(sessionId);

        var canonical = _courtesy.CanonicalizeUrl(url);
        var domain = _courtesy.GetDomain(url);

        // Check cache
        if (!forceRefresh)
        {
            var existing = db.GetSnapshotByUrl(canonical);
            if (existing != null)
            {
                var log = _courtesy.CreateRequestLog(url, domain);
                log.WasCached = true;
                _requestLogs.Add(log);
                db.Log("INFO", "Snapshot", $"Using cached snapshot for {url}", new() { { "snapshot_id", existing.Id } });
                return existing;
            }
        }

        // Acquire polite slot
        if (!await _courtesy.AcquireSlotAsync(url, ct))
        {
            // Circuit broken
            var blockedSnapshot = CreateBlockedSnapshot(sessionId, url, canonical, session.WorkspacePath, "Circuit breaker tripped for domain");
            db.SaveSnapshot(blockedSnapshot);
            db.Log("WARN", "Snapshot", $"Circuit broken for {domain}, skipping {url}");
            return blockedSnapshot;
        }

        try
        {
            var log = _courtesy.CreateRequestLog(url, domain);
            var startTime = DateTime.UtcNow;

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(url, ct);
                log.HttpStatus = (int)response.StatusCode;
            }
            catch (TaskCanceledException)
            {
                log.Error = "Timeout";
                _requestLogs.Add(log);
                var backoff = _courtesy.RecordFailure(url, 0);
                db.Log("WARN", "Snapshot", $"Timeout fetching {url}, backoff {backoff.TotalSeconds}s");
                return CreateBlockedSnapshot(sessionId, url, canonical, session.WorkspacePath, "Request timeout");
            }
            catch (HttpRequestException ex)
            {
                log.Error = ex.Message;
                _requestLogs.Add(log);
                _courtesy.RecordFailure(url, 0);
                db.Log("WARN", "Snapshot", $"HTTP error fetching {url}: {ex.Message}");
                return CreateBlockedSnapshot(sessionId, url, canonical, session.WorkspacePath, ex.Message);
            }

            log.DelayUsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _requestLogs.Add(log);

            // Handle rate limiting / server errors
            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                response.StatusCode == HttpStatusCode.ServiceUnavailable ||
                (int)response.StatusCode >= 500)
            {
                var backoff = _courtesy.RecordFailure(url, (int)response.StatusCode);
                db.Log("WARN", "Snapshot", $"Rate limit/error {response.StatusCode} from {url}, backoff {backoff.TotalSeconds}s");

                // Retry with backoff
                await Task.Delay(backoff, ct);
                return await RetryCapture(sessionId, url, canonical, session.WorkspacePath, db, 1, ct);
            }

            // Check for blocked/paywall indicators
            if (response.StatusCode == HttpStatusCode.Forbidden ||
                response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _courtesy.RecordFailure(url, (int)response.StatusCode);
                db.Log("WARN", "Snapshot", $"Blocked/paywalled: {url} ({response.StatusCode})");
                var blocked = CreateBlockedSnapshot(sessionId, url, canonical, session.WorkspacePath,
                    $"Access denied ({response.StatusCode}). Not bypassing paywalls/logins per policy.");
                db.SaveSnapshot(blocked);
                return blocked;
            }

            _courtesy.RecordSuccess(url);

            // PDF auto-detection: if the response is a PDF, ingest as artifact instead of HTML snapshot
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ||
                url.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return await IngestPdfResponseAsync(sessionId, url, canonical, session.WorkspacePath, response, db, ct);
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            var contentHash = ArtifactStore.ComputeHash(Encoding.UTF8.GetBytes(html));

            // Content hash dedup: if identical content already captured from a different URL, reuse it
            var existingByHash = db.GetSnapshotByContentHash(contentHash);
            if (existingByHash != null)
            {
                db.Log("INFO", "Snapshot", $"Content dedup: {url} matches existing {existingByHash.Url}", 
                    new() { { "existing_id", existingByHash.Id } });
                _courtesy.CacheUrl(canonical, existingByHash.Id);
                return existingByHash;
            }

            // Create snapshot bundle
            var snapshot = new Snapshot
            {
                SessionId = sessionId,
                Url = url,
                CanonicalUrl = canonical,
                Title = ExtractTitle(html),
                HttpStatus = (int)response.StatusCode,
                ContentHash = contentHash,
                CapturedUtc = DateTime.UtcNow
            };

            var bundlePath = Path.Combine(session.WorkspacePath, "Snapshots", snapshot.Id);
            Directory.CreateDirectory(bundlePath);

            // Save bundle files in parallel (3 independent writes)
            var htmlPath = Path.Combine(bundlePath, "page.html");
            var textPath = Path.Combine(bundlePath, "page.txt");
            var metaPath = Path.Combine(bundlePath, "meta.json");

            var plainText = ExtractReadableText(html);
            var meta = new
            {
                snapshot.Url,
                snapshot.CanonicalUrl,
                snapshot.Title,
                snapshot.CapturedUtc,
                snapshot.HttpStatus,
                snapshot.ContentHash,
                UserAgent = _settings.UserAgentString
            };

            await Task.WhenAll(
                File.WriteAllTextAsync(htmlPath, html, ct),
                File.WriteAllTextAsync(textPath, plainText, ct),
                File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }), ct)
            );

            snapshot.BundlePath = bundlePath;
            snapshot.HtmlPath = htmlPath;
            snapshot.TextPath = textPath;
            snapshot.ScreenshotPath = Path.Combine(bundlePath, "screenshot.png"); // placeholder 

            db.SaveSnapshot(snapshot);
            _courtesy.CacheUrl(canonical, snapshot.Id);
            db.Log("INFO", "Snapshot", $"Captured {url}", new() { { "snapshot_id", snapshot.Id }, { "title", snapshot.Title } });

            return snapshot;
        }
        finally
        {
            _courtesy.ReleaseSlot(url);
        }
    }

    /// <summary>
    /// When CaptureUrlAsync detects a PDF response, download and ingest as an artifact,
    /// then return a snapshot referencing the extracted text.
    /// </summary>
    private async Task<Snapshot> IngestPdfResponseAsync(string sessionId, string url, string canonical,
        string workspacePath, HttpResponseMessage response, Data.SessionDb db, CancellationToken ct)
    {
        var bundlePath = Path.Combine(workspacePath, "Snapshots", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(bundlePath);
        var pdfPath = Path.Combine(bundlePath, "document.pdf");

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        await File.WriteAllBytesAsync(pdfPath, bytes, ct);

        // Ingest as artifact (gets content-addressed storage + indexing)
        try
        {
            var artifact = _artifactStore.IngestFile(sessionId, pdfPath);
            await _indexService.IndexArtifactAsync(sessionId, artifact, ct);
            db.Log("INFO", "Snapshot", $"Auto-ingested PDF from URL: {url}", new() { { "artifact_id", artifact.Id } });
        }
        catch (Exception ex)
        {
            db.Log("WARN", "Snapshot", $"PDF ingest failed for {url}: {ex.Message}");
        }

        // Return a snapshot so the research pipeline can track it
        var snapshot = new Snapshot
        {
            SessionId = sessionId,
            Url = url,
            CanonicalUrl = canonical,
            Title = $"[PDF] {Path.GetFileName(new Uri(url).AbsolutePath)}",
            HttpStatus = (int)response.StatusCode,
            ContentHash = ArtifactStore.ComputeHash(bytes),
            CapturedUtc = DateTime.UtcNow,
            BundlePath = bundlePath,
            TextPath = Path.Combine(bundlePath, "page.txt"),
        };

        // Write extracted text to the snapshot bundle for display
        try
        {
            var pdfService = new PdfIngestionService(new OcrService(_sessionManager));
            var pdfResult = await pdfService.ExtractTextAsync(pdfPath, ct);
            await File.WriteAllTextAsync(snapshot.TextPath, pdfResult.FullText, ct);
        }
        catch
        {
            await File.WriteAllTextAsync(snapshot.TextPath, $"[PDF from {url} — text extraction failed]", ct);
        }

        db.SaveSnapshot(snapshot);
        _courtesy.CacheUrl(canonical, snapshot.Id);
        return snapshot;
    }

    private async Task<Snapshot> RetryCapture(string sessionId, string url, string canonical,
        string workspacePath, Data.SessionDb db, int attempt, CancellationToken ct)
    {
        if (attempt >= _settings.MaxRetries)
        {
            db.Log("WARN", "Snapshot", $"Max retries exceeded for {url}");
            var blocked = CreateBlockedSnapshot(sessionId, url, canonical, workspacePath, "Max retries exceeded");
            db.SaveSnapshot(blocked);
            return blocked;
        }

        if (!await _courtesy.AcquireSlotAsync(url, ct))
        {
            var blocked = CreateBlockedSnapshot(sessionId, url, canonical, workspacePath, "Circuit broken after retry");
            db.SaveSnapshot(blocked);
            return blocked;
        }

        try
        {
            var response = await _httpClient.GetAsync(url, ct);

            if (response.IsSuccessStatusCode)
            {
                _courtesy.RecordSuccess(url);
                var html = await response.Content.ReadAsStringAsync(ct);
                var contentHash = ArtifactStore.ComputeHash(Encoding.UTF8.GetBytes(html));

                var snapshot = new Snapshot
                {
                    SessionId = sessionId, Url = url, CanonicalUrl = canonical,
                    Title = ExtractTitle(html), HttpStatus = (int)response.StatusCode,
                    ContentHash = contentHash, CapturedUtc = DateTime.UtcNow
                };

                var bundlePath = Path.Combine(workspacePath, "Snapshots", snapshot.Id);
                Directory.CreateDirectory(bundlePath);
                var htmlPath = Path.Combine(bundlePath, "page.html");
                var textPath = Path.Combine(bundlePath, "page.txt");

                await File.WriteAllTextAsync(htmlPath, html, ct);
                await File.WriteAllTextAsync(textPath, ExtractReadableText(html), ct);

                snapshot.BundlePath = bundlePath;
                snapshot.HtmlPath = htmlPath;
                snapshot.TextPath = textPath;
                snapshot.ScreenshotPath = Path.Combine(bundlePath, "screenshot.png");

                db.SaveSnapshot(snapshot);
                _courtesy.CacheUrl(canonical, snapshot.Id);
                return snapshot;
            }

            var backoff = _courtesy.RecordFailure(url, (int)response.StatusCode);
            await Task.Delay(backoff, ct);
            return await RetryCapture(sessionId, url, canonical, workspacePath, db, attempt + 1, ct);
        }
        finally
        {
            _courtesy.ReleaseSlot(url);
        }
    }

    private static Snapshot CreateBlockedSnapshot(string sessionId, string url, string canonical, string workspacePath, string reason)
    {
        var snap = new Snapshot
        {
            SessionId = sessionId, Url = url, CanonicalUrl = canonical,
            Title = $"[Blocked] {url}", IsBlocked = true, BlockReason = reason,
            BundlePath = "", HtmlPath = "", TextPath = "", ScreenshotPath = ""
        };
        return snap;
    }

    public static string ExtractTitle(string html)
    {
        var match = TitleRegex().Match(html);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value.Trim()) : "Untitled";
    }

    public static string ExtractReadableText(string html)
    {
        // Remove scripts, styles, and tags using source-generated compiled regex
        var text = ScriptRegex().Replace(html, " ");
        text = StyleRegex().Replace(text, " ");
        text = TagRegex().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text;
    }

    // Source-generated compiled regex (5-10x faster than interpreted, zero allocations for pattern)
    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<script[^>]*>[\s\S]*?</script>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"<style[^>]*>[\s\S]*?</style>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public List<RequestLog> GetRequestLogs() => _requestLogs.ToList();
}
