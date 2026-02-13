using Microsoft.Playwright;
using ResearchHive.Core.Configuration;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace ResearchHive.Core.Services;

/// <summary>
/// Playwright-powered headless browser service for executing search queries.
/// Uses a real Chromium instance that renders JavaScript and handles cookies/sessions,
/// bypassing the CAPTCHAs and JS-only pages that block plain HttpClient requests.
/// Extracts URLs via JavaScript evaluation on the live DOM — far more reliable
/// than regex parsing of serialized HTML.
/// 
/// B1: Browser context pool — pre-warmed IBrowserContext instances are rented/returned
/// instead of created/destroyed per call.
/// </summary>
public sealed class BrowserSearchService : IBrowserSearchService, IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _disposed;

    // Context pool
    private readonly Channel<IBrowserContext> _contextPool = Channel.CreateUnbounded<IBrowserContext>();
    private readonly SemaphoreSlim _poolGate;
    private int _contextCount;
    private readonly int _maxContexts;

    public BrowserSearchService(AppSettings? settings = null)
    {
        _maxContexts = settings?.MaxBrowserContexts ?? 8;
        _poolGate = new SemaphoreSlim(_maxContexts, _maxContexts);
    }

    // Search engines — with Playwright we can use ALL of them because JS is rendered
    // Google is excluded because it aggressively serves CAPTCHAs to headless browsers.
    // Search engines that work reliably with headless Playwright.
    // Google: CAPTCHAs. DuckDuckGo: blocks automation entirely.
    public static readonly (string Name, string UrlTemplate)[] SearchEngines = new[]
    {
        ("bing",           "https://www.bing.com/search?q="),
        ("yahoo",          "https://search.yahoo.com/search?p="),
        ("scholar",        "https://scholar.google.com/scholar?hl=en&q="),
        ("brave",          "https://search.brave.com/search?q="),
    };

    private static readonly string[] BlockedDomains = new[]
    {
        "google.com", "bing.com", "duckduckgo.com", "yahoo.com",
        "brave.com", "facebook.com", "twitter.com", "instagram.com",
        "tiktok.com", "youtube.com/results", "linkedin.com/search",
        "gstatic.com", "googleapis.com", "yimg.com", "yahoo.net",
        "microsoft.com/bing", "r.bing.com", "scholar.google.com",
        "reddit.com/search",
    };

    /// <summary>
    /// Installs Playwright browsers if not already present.
    /// Should be called once at startup or before first search.
    /// </summary>
    public static void EnsureBrowsersInstalled()
    {
        var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"Playwright browser install failed with exit code {exitCode}. " +
                "Run 'powershell playwright.ps1 install chromium' manually.");
    }

    /// <summary>
    /// Ensures the browser is initialized. Thread-safe, lazy initialization.
    /// Pre-warms half the pool on first launch for faster first search.
    /// </summary>
    private async Task EnsureBrowserAsync()
    {
        if (_browser?.IsConnected == true) return;

        await _initLock.WaitAsync();
        try
        {
            if (_browser?.IsConnected == true) return;

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[]
                {
                    "--disable-blink-features=AutomationControlled",
                    "--disable-features=IsolateOrigins,site-per-process",
                    "--no-sandbox",
                }
            });

            // Pre-warm half the pool (fire-and-forget, non-blocking)
            _ = PreWarmPoolAsync(Math.Min(4, _maxContexts / 2));
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Creates N browser contexts upfront and puts them in the pool.
    /// Eliminates cold-start latency on the first batch of searches.
    /// </summary>
    private async Task PreWarmPoolAsync(int count)
    {
        var tasks = Enumerable.Range(0, count).Select(async _ =>
        {
            try
            {
                var ctx = await CreateContextAsync();
                Interlocked.Increment(ref _contextCount);
                await _contextPool.Writer.WriteAsync(ctx);
            }
            catch { /* best-effort pre-warming */ }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Rents a browser context from the pool. Creates a new context if none available and pool isn't full.
    /// Blocks (via semaphore) if pool is at capacity until one is returned.
    /// </summary>
    private async Task<IBrowserContext> RentContextAsync(CancellationToken ct = default)
    {
        await EnsureBrowserAsync();

        // Try to get an existing context from the pool channel
        if (_contextPool.Reader.TryRead(out var existing))
            return existing;

        // Wait for capacity
        await _poolGate.WaitAsync(ct);

        // Double-check pool after acquiring semaphore (another task might have returned one)
        if (_contextPool.Reader.TryRead(out var recycled))
        {
            _poolGate.Release();
            return recycled;
        }

        // Create new context
        Interlocked.Increment(ref _contextCount);
        return await CreateContextAsync();
    }

    /// <summary>
    /// Returns a context to the pool for reuse. Closes pages before returning.
    /// If the context is broken, disposes it and releases the semaphore.
    /// </summary>
    private async Task ReturnContextAsync(IBrowserContext context)
    {
        try
        {
            // Close all pages to reset state for next user
            foreach (var page in context.Pages)
            {
                try { await page.CloseAsync(); } catch { }
            }

            // Return to pool
            await _contextPool.Writer.WriteAsync(context);
        }
        catch
        {
            // Context is broken, dispose and release slot
            try { await context.CloseAsync(); } catch { }
            Interlocked.Decrement(ref _contextCount);
            _poolGate.Release();
        }
    }

    private async Task<IBrowserContext> CreateContextAsync()
    {
        return await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
            Locale = "en-US",
            TimezoneId = "America/New_York",
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
        });
    }

    /// <summary>
    /// Returns the number of browser contexts currently in the pool and total allocated.
    /// </summary>
    public (int Available, int Total, int Max) PoolStats
    {
        get
        {
            int available = _contextPool.Reader.Count;
            return (available, _contextCount, _maxContexts);
        }
    }

    /// <summary>
    /// Performs a search using the specified engine and query, returning extracted result URLs.
    /// Uses pooled browser contexts for efficiency. 
    /// </summary>
    public async Task<List<string>> SearchAsync(string query, string engineName, string urlTemplate, string? timeRange = null, CancellationToken ct = default)
    {
        var context = await RentContextAsync(ct);

        try
        {
            var page = await context.NewPageAsync();

            // Stealth: override navigator.webdriver
            await page.AddInitScriptAsync(@"
                Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            ");

            var searchUrl = urlTemplate + Uri.EscapeDataString(query)
                + GetDateFilter(engineName, timeRange);
            await page.GotoAsync(searchUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30_000,
            });

            // Handle consent dialogs first (Bing, Google, etc.)
            await DismissConsentDialogsAsync(page);

            // Wait for engine-specific result elements to appear
            await WaitForResultsAsync(page, engineName);

            // Extract URLs using engine-specific JavaScript selectors
            var urls = await ExtractUrlsViaJavaScriptAsync(page, engineName);

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
            await ReturnContextAsync(context);
        }
    }

    /// <summary>
    /// Returns engine-specific date filter query parameters for the given time range.
    /// </summary>
    internal static string GetDateFilter(string engineName, string? timeRange)
    {
        if (string.IsNullOrEmpty(timeRange) || timeRange == "any")
            return "";

        return engineName switch
        {
            "bing" => timeRange switch
            {
                "day" => "&freshness=Day",
                "week" => "&freshness=Week",
                "month" => "&freshness=Month",
                "year" => "&freshness=Month", // Bing has no year param; Month is closest
                _ => ""
            },
            "brave" => timeRange switch
            {
                "day" => "&tf=pd",
                "week" => "&tf=pw",
                "month" => "&tf=pm",
                "year" => "&tf=py",
                _ => ""
            },
            "yahoo" => timeRange switch
            {
                "day" => "&age=1d",
                "week" => "&age=1w",
                "month" => "&age=1m",
                "year" => "", // Yahoo has no year filter
                _ => ""
            },
            "scholar" => timeRange switch
            {
                "day" or "week" or "month" => $"&as_ylo={DateTime.UtcNow.Year}",
                "year" => $"&as_ylo={DateTime.UtcNow.Year - 1}",
                _ => ""
            },
            _ => ""
        };
    }
    /// This is critical because many search engines load results dynamically via JS.
    /// </summary>
    private static async Task WaitForResultsAsync(IPage page, string engineName)
    {
        string selector = engineName switch
        {
            "bing" => ".b_algo, #b_results li",
            "yahoo" => ".algo-sr, .compTitle, .dd.algo",
            "scholar" => ".gs_ri, .gs_rt",
            "brave" => ".snippet, .result-header, [data-type='web']",
            _ => "a[href]"
        };

        try
        {
            await page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions
            {
                Timeout = 8000,
                State = WaitForSelectorState.Attached,
            });
            // Brief settle time for late-rendering elements
            await page.WaitForTimeoutAsync(400);
        }
        catch
        {
            // Selector not found within timeout, proceed with whatever we have
            await page.WaitForTimeoutAsync(800);
        }
    }

    /// <summary>
    /// Extracts search result URLs by running JavaScript in the browser page.
    /// Uses engine-specific CSS selectors and JS logic to get actual destination URLs,
    /// handling redirect wrappers, data attributes, and click-through encodings.
    /// </summary>
    private static async Task<List<string>> ExtractUrlsViaJavaScriptAsync(IPage page, string engineName)
    {
        // Universal JS function that runs in the browser context
        string jsScript = engineName switch
        {
            "bing" => @"
                (() => {
                    const results = [];
                    // Strategy 1: Extract actual URLs from Bing ck/a redirect hrefs
                    document.querySelectorAll('li.b_algo h2 a, .b_algo h2 a').forEach(a => {
                        let href = a.href || '';
                        // Bing wraps URLs: /ck/a?...&u=a1<base64url>&ntb=1
                        const uMatch = href.match(/[?&]u=a1([^&]+)/);
                        if (uMatch) {
                            try {
                                let decoded = atob(uMatch[1].replace(/-/g, '+').replace(/_/g, '/'));
                                if (decoded.startsWith('http')) { results.push(decoded); return; }
                            } catch(e) {}
                            try {
                                let decoded = decodeURIComponent(uMatch[1]);
                                if (decoded.startsWith('http')) { results.push(decoded); return; }
                            } catch(e) {}
                        }
                        // Direct non-Bing URL
                        if (href.startsWith('http') && !href.includes('bing.com')) {
                            results.push(href);
                            return;
                        }
                    });
                    // Strategy 2: cite elements show clean URL paths
                    if (results.length < 3) {
                        document.querySelectorAll('.b_algo cite, .b_caption cite, .tptt').forEach(cite => {
                            let text = cite.textContent.trim();
                            // Skip news/time/source annotations
                            if (!text || text.includes('·') || text.includes('...') || text.length < 8) return;
                            // Replace › separators with /
                            text = text.replace(/\s*›\s*/g, '/').replace(/\s*\u203A\s*/g, '/');
                            // Take first word (URL) only
                            text = text.split(/\s+/)[0];
                            if (text && !text.includes('bing.com') && !text.includes('microsoft.com')) {
                                if (!text.startsWith('http')) text = 'https://' + text;
                                if (!results.includes(text)) results.push(text);
                            }
                        });
                    }
                    return results;
                })()",

            "yahoo" => @"
                (() => {
                    const results = [];
                    // Yahoo uses redirect URLs with RU= parameter
                    document.querySelectorAll('.compTitle a, .algo-sr a, h3.title a').forEach(a => {
                        let url = a.href;
                        // Decode Yahoo redirect
                        if (url && url.includes('r.search.yahoo.com')) {
                            const match = url.match(/RU=([^/]+)/);
                            if (match) {
                                try { url = decodeURIComponent(match[1]); } catch(e) {}
                            }
                        }
                        if (url && url.startsWith('http') && !url.includes('yahoo.com') && !url.includes('yimg.com')) {
                            results.push(url);
                        }
                    });
                    return results;
                })()",

            "scholar" => @"
                (() => {
                    const results = [];
                    // Google Scholar: gs_rt (title link), gs_or_ggsm (full text link)
                    document.querySelectorAll('.gs_rt a, .gs_or_ggsm a, .gs_ri a').forEach(a => {
                        let url = a.href;
                        if (url && url.startsWith('http') && !url.includes('google.com') && !url.includes('scholar.google')) {
                            results.push(url);
                        }
                    });
                    return results;
                })()",

            "brave" => @"
                (() => {
                    const results = [];
                    // Brave Search result links
                    document.querySelectorAll('a.result-header, a.heading-serpresult, .snippet a[href]').forEach(a => {
                        let url = a.href;
                        if (url && url.startsWith('http') && !url.includes('brave.com') && !url.includes('search.brave')) {
                            results.push(url);
                        }
                    });
                    // Fallback: any result card links
                    if (results.length === 0) {
                        document.querySelectorAll('.snippet-url, .result-url').forEach(el => {
                            let text = el.textContent.trim();
                            if (text && !text.includes('brave.com')) {
                                if (!text.startsWith('http')) text = 'https://' + text;
                                results.push(text.split(' ')[0]);
                            }
                        });
                    }
                    return results;
                })()",

            _ => @"
                (() => {
                    const results = [];
                    document.querySelectorAll('a[href]').forEach(a => {
                        let url = a.href;
                        if (url && url.startsWith('http') && url.includes('://') && 
                            !url.includes('google.com') && !url.includes('bing.com') && 
                            !url.includes('duckduckgo.com') && !url.includes('yahoo.com')) {
                            results.push(url);
                        }
                    });
                    return results;
                })()"
        };

        var result = await page.EvaluateAsync<string[]>(jsScript);
        return result?.ToList() ?? new List<string>();
    }

    private static async Task DismissConsentDialogsAsync(IPage page)
    {
        try
        {
            // Bing consent
            var bingAccept = page.Locator("#bnp_btn_accept, button:has-text('Accept')");
            if (await bingAccept.CountAsync() > 0)
            {
                await bingAccept.First.ClickAsync(new LocatorClickOptions { Timeout = 2000 });
                await page.WaitForTimeoutAsync(500);
            }
        }
        catch { /* No consent dialog */ }

        try
        {
            // Cookie dialogs
            var cookieAccept = page.Locator("button:has-text('Accept all'), button:has-text('I agree'), button:has-text('Accept cookies')");
            if (await cookieAccept.CountAsync() > 0)
            {
                await cookieAccept.First.ClickAsync(new LocatorClickOptions { Timeout = 2000 });
                await page.WaitForTimeoutAsync(500);
            }
        }
        catch { /* No cookie dialog */ }
    }

    private static bool IsValidResultUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!url.StartsWith("http://") && !url.StartsWith("https://")) return false;
        if (url.Contains("javascript:")) return false;
        if (url.Length > 500) return false;

        foreach (var domain in BlockedDomains)
            if (url.Contains(domain, StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }

    /// <summary>
    /// Gets the fully rendered HTML of a page (used by SnapshotService if needed).
    /// </summary>
    public async Task<string> GetRenderedHtmlAsync(string url, CancellationToken ct = default)
    {
        var context = await RentContextAsync(ct);

        try
        {
            var page = await context.NewPageAsync();
            await page.AddInitScriptAsync(@"
                Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            ");

            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 20_000,
            });

            await page.WaitForTimeoutAsync(1500);
            return await page.ContentAsync();
        }
        finally
        {
            await ReturnContextAsync(context);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Drain context pool
        _contextPool.Writer.TryComplete();
        while (_contextPool.Reader.TryRead(out var ctx))
        {
            try { await ctx.CloseAsync(); } catch { }
        }

        if (_browser != null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;
    }
}
