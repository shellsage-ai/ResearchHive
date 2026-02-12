using SeleniumUndetectedChromeDriver;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace ResearchHive.Core.Services;

/// <summary>
/// Google search using Selenium.UndetectedChromeDriver (fysh711426 v1.1.3)
/// to bypass bot detection. Uses a patched ChromeDriver that avoids CAPTCHAs
/// by emulating genuine browser fingerprints.
///
/// Rate-limited by design — Google searches are serialized with generous
/// courtesy delays and human-like jitter. This is a personal research tool,
/// not a scraper.
/// </summary>
public sealed class GoogleSearchService : IDisposable
{
    private UndetectedChromeDriver? _driver;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _searchLock = new(1, 1); // Serialize Google searches
    private bool _disposed;
    private DateTime _lastSearchTime = DateTime.MinValue;
    private int _searchCount;
    private string? _tempProfileDir;

    /// <summary>Minimum delay between Google searches — be kind.</summary>
    private static readonly TimeSpan MinSearchInterval = TimeSpan.FromSeconds(3);

    /// <summary>Random jitter to look more human (thread-safe).</summary>
    private static Random Jitter => Random.Shared;

    /// <summary>
    /// Maximum Google queries per multi-lane search session.
    /// Google is a supplementary engine — don't hammer it.
    /// </summary>
    public const int MaxQueriesPerSession = 3;

    /// <summary>
    /// Lazily initializes the UndetectedChromeDriver.
    /// Uses non-headless mode (a real Chrome window, pushed offscreen) because
    /// Google detects headless browsers even with undetected patches.
    /// For a personal desktop research assistant, a briefly-visible Chrome window
    /// is acceptable — it ensures genuine Google results without CAPTCHAs.
    /// </summary>
    private async Task EnsureDriverAsync()
    {
        if (_driver != null) return;

        await _initLock.WaitAsync();
        try
        {
            if (_driver != null) return;

            // Auto-download chromedriver matching the locally installed Chrome version
            var driverPath = await new ChromeDriverInstaller().Auto();

            // Non-headless to avoid detection, but completely invisible to the user:
            // - Isolated profile directory so it NEVER touches the user's Chrome sessions
            // - Window pushed far offscreen and minimized
            // - All unnecessary features disabled for speed
            _tempProfileDir = Path.Combine(Path.GetTempPath(), "ResearchHive_Chrome_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_tempProfileDir);

            var options = new ChromeOptions();
            options.AddArgument($"--user-data-dir={_tempProfileDir}");
            options.AddArgument("--window-position=-32000,-32000");
            options.AddArgument("--window-size=1,1");
            options.AddArgument("--disable-popup-blocking");
            options.AddArgument("--no-first-run");
            options.AddArgument("--no-default-browser-check");
            options.AddArgument("--disable-extensions");
            options.AddArgument("--disable-gpu");
            options.AddArgument("--mute-audio");
            options.AddArgument("--disable-infobars");
            options.AddArgument("--disable-notifications");
            options.AddArgument("--disable-background-networking");
            options.AddArgument("--disable-default-apps");
            options.AddArgument("--disable-sync");
            options.AddArgument("--disable-translate");
            options.AddArgument("--metrics-recording-only");
            options.AddArgument("--no-default-browser-check");

            _driver = UndetectedChromeDriver.Create(
                driverExecutablePath: driverPath,
                headless: false,
                options: options,
                hideCommandPromptWindow: true,
                suppressWelcome: true);

            // Minimize the window immediately so it's completely invisible
            try { _driver.Manage().Window.Minimize(); } catch { /* best effort */ }
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Indicates whether this service has been initialized and the driver
    /// is available. Used for status reporting.
    /// </summary>
    public bool IsReady => _driver != null && !_disposed;

    /// <summary>
    /// Searches Google for the given query and returns extracted result URLs.
    /// Serialized — only one Google search runs at a time, with courtesy delays.
    /// </summary>
    public async Task<List<string>> SearchAsync(string query, string? timeRange = null, CancellationToken ct = default)
    {
        if (_disposed) return new List<string>();

        // Serialize all Google searches — one at a time, with courtesy delays
        await _searchLock.WaitAsync(ct);
        try
        {
            await EnsureDriverAsync();

            // Courtesy delay: wait between searches
            var elapsed = DateTime.UtcNow - _lastSearchTime;
            if (elapsed < MinSearchInterval)
            {
                var delay = MinSearchInterval - elapsed
                    + TimeSpan.FromMilliseconds(Jitter.Next(500, 2000));
                await Task.Delay(delay, ct);
            }

            var dateFilter = timeRange switch
            {
                "day" => "&tbs=qdr:d",
                "week" => "&tbs=qdr:w",
                "month" => "&tbs=qdr:m",
                "year" => "&tbs=qdr:y",
                _ => ""
            };

            var searchUrl = "https://www.google.com/search?q="
                + Uri.EscapeDataString(query)
                + "&num=10&hl=en"
                + dateFilter;

            _driver!.GoToUrl(searchUrl);

            // Wait for results to render — generous wait, don't rush
            await Task.Delay(2000 + Jitter.Next(500, 1500), ct);

            // Try to dismiss any consent dialogs
            DismissConsentDialogs();

            // Google may serve an abuse challenge page that auto-resolves.
            // Wait for actual search results to appear (yuRUbf or #rso).
            await WaitForSearchResultsAsync(ct);

            // Extract URLs via JavaScript on the live DOM
            var urls = ExtractUrls();

            _lastSearchTime = DateTime.UtcNow;
            _searchCount++;

            return urls
                .Where(IsValidResultUrl)
                .Distinct()
                .Take(10)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
        finally
        {
            _searchLock.Release();
        }
    }

    /// <summary>
    /// Resets the per-session query count. Call at the start of each research session.
    /// </summary>
    public void ResetSessionCount() => _searchCount = 0;

    /// <summary>
    /// Gets the number of Google searches performed in this session.
    /// </summary>
    public int SessionSearchCount => _searchCount;

    /// <summary>
    /// Handles Google's cookie consent dialog if it appears.
    /// </summary>
    private void DismissConsentDialogs()
    {
        try
        {
            // Google consent dialog: "Accept all" / "I agree" / "Reject all"
            var buttons = _driver!.FindElements(By.CssSelector(
                "button[id='L2AGLb'], " +           // Accept all (EEA)
                "button[id='W0wltc'], " +            // Reject all (EEA)
                "button:has(div.QS5gu)"));           // Consent form buttons

            foreach (var btn in buttons)
            {
                var text = btn.Text.ToLowerInvariant();
                if (text.Contains("accept") || text.Contains("agree") || text.Contains("consent"))
                {
                    btn.Click();
                    break;
                }
            }
        }
        catch { /* No consent dialog or already dismissed */ }
    }

    /// <summary>
    /// Waits for Google's actual search results to appear in the DOM.
    /// Google may serve an abuse challenge page (/sorry/) that auto-resolves
    /// after a few seconds. This method polls until result containers appear.
    /// </summary>
    private async Task WaitForSearchResultsAsync(CancellationToken ct)
    {
        // Poll for up to 15 seconds for result containers to appear
        for (int attempt = 0; attempt < 15; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var hasResults = _driver!.ExecuteScript(
                    "return document.querySelectorAll('div.yuRUbf, #rso, #search').length > 0"
                );

                if (hasResults is true)
                {
                    // Results found — wait a bit more for all to render
                    await Task.Delay(500 + Jitter.Next(200, 800), ct);
                    return;
                }
            }
            catch { /* Page still transitioning */ }

            await Task.Delay(1000, ct);
        }

        // Timed out waiting for results — will extract whatever is available
    }

    /// <summary>
    /// Extracts search result URLs by executing JavaScript on the live Google DOM.
    /// Uses multiple strategies to handle Google's frequently-changing layouts.
    /// </summary>
    private List<string> ExtractUrls()
    {
        // Google's DOM structure changes often. Current approach (2024-2025):
        // - Results live inside #rso (result set organic)
        // - Individual results use div.yuRUbf for the title/link container
        // - Fallback: any a[href] inside #search that points externally
        // NOTE: Selenium's ExecuteScript wraps the body in a function,
        // so we must use 'return' at the top level (unlike Playwright's EvaluateAsync).
        const string jsScript = @"
            return (() => {
                const results = [];
                const seen = new Set();
                function isGoogle(url) {
                    return url.includes('google.com') || url.includes('google.co.')
                        || url.includes('gstatic.com') || url.includes('googleapis.com')
                        || url.includes('webcache.googleusercontent')
                        || url.includes('/search?') || url.includes('accounts.google')
                        || url.includes('maps.google') || url.includes('translate.google');
                }
                function add(url) {
                    // Handle Google redirect wrappers: /url?q=actual_url&sa=...
                    if (url.includes('/url?')) {
                        try {
                            const u = new URL(url);
                            const q = u.searchParams.get('q') || u.searchParams.get('url');
                            if (q && q.startsWith('http')) url = q;
                        } catch(e) {}
                    }
                    if (url && url.startsWith('http') && !isGoogle(url) && !seen.has(url)) {
                        seen.add(url);
                        results.push(url);
                    }
                }

                // Strategy 1: yuRUbf containers (Google's current result link wrapper)
                document.querySelectorAll('div.yuRUbf a[href]').forEach(a => add(a.href));

                // Strategy 2: #rso (result set organic) direct links
                if (results.length < 5) {
                    document.querySelectorAll('#rso a[href]').forEach(a => add(a.href));
                }

                // Strategy 3: jsname-attributed links (Google's JS framework)
                if (results.length < 5) {
                    document.querySelectorAll('#search a[jsname][href]').forEach(a => add(a.href));
                }

                // Strategy 4: cite elements (clean URLs displayed under results)
                if (results.length < 5) {
                    document.querySelectorAll('cite').forEach(cite => {
                        let text = cite.textContent.trim().split(' ')[0];
                        text = text.replace(/\s*[›\u203A]\s*/g, '/');
                        if (text && !isGoogle(text) && text.includes('.')) {
                            if (!text.startsWith('http')) text = 'https://' + text;
                            add(text);
                        }
                    });
                }

                return results;
            })()";

        try
        {
            var result = _driver!.ExecuteScript(jsScript);

            if (result is System.Collections.IEnumerable enumerable and not string)
            {
                return enumerable.Cast<object>()
                    .Select(o => o?.ToString() ?? "")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
            }

            return new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Filters out search engine domains, social media, and tracker URLs.
    /// </summary>
    private static bool IsValidResultUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!url.StartsWith("http://") && !url.StartsWith("https://")) return false;
        if (url.Contains("javascript:")) return false;
        if (url.Length > 500) return false;

        string[] blocked =
        {
            "google.com", "google.co.", "gstatic.com", "googleapis.com",
            "webcache.googleusercontent.com", "translate.google",
            "accounts.google", "support.google", "maps.google",
            "facebook.com", "twitter.com", "instagram.com", "tiktok.com",
            "youtube.com/results", "linkedin.com/search", "reddit.com/search",
        };

        foreach (var domain in blocked)
            if (url.Contains(domain, StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }

    /// <summary>
    /// Releases the Chrome driver instance and cleans up the temp profile.
    /// Called after each research session so the Chrome window goes away
    /// and doesn't block the user's normal Chrome usage.
    /// The driver will be re-created lazily if needed for the next session.
    /// </summary>
    public void ReleaseDriver()
    {
        try { _driver?.Dispose(); }
        catch { /* Chrome process cleanup may throw */ }
        _driver = null;
        _searchCount = 0;
        CleanupTempProfile();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _driver?.Dispose(); }
        catch { /* Chrome process cleanup may throw */ }

        _driver = null;
        _initLock.Dispose();
        _searchLock.Dispose();
        CleanupTempProfile();
    }

    private void CleanupTempProfile()
    {
        if (_tempProfileDir != null)
        {
            try { Directory.Delete(_tempProfileDir, true); } catch { }
            _tempProfileDir = null;
        }
    }
}
