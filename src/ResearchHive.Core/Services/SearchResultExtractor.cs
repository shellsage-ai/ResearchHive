using System.Text.RegularExpressions;

namespace ResearchHive.Core.Services;

/// <summary>
/// Engine-specific HTML parsers for extracting search result URLs.
/// Each method uses multiple class-attribute patterns for robustness against
/// layout changes, with a generic href fallback for unknown layouts.
/// </summary>
public static class SearchResultExtractor
{
    private static readonly string[] BlockedDomains = new[]
    {
        "duckduckgo.com", "brave.com", "bing.com", "google.com",
        "microsoft.com/bing", "yahoo.com", "facebook.com",
        "twitter.com", "instagram.com", "tiktok.com", "youtube.com/results",
        "reddit.com/search", "linkedin.com/search",
        "yimg.com", "yahoo.net", "gstatic.com", "googleapis.com",
        "yahoo.uservoice.com", "scout.yahoo.com",
        "scholar.google.com/scholar_url",
    };

    public static List<string> Extract(string html, string engineUrl)
    {
        List<string> results;
        if (engineUrl.Contains("google") && !engineUrl.Contains("scholar"))
            results = ExtractGoogle(html);
        else if (engineUrl.Contains("yahoo"))
            results = ExtractYahoo(html);
        else if (engineUrl.Contains("scholar"))
            results = ExtractGoogleScholar(html);
        else if (engineUrl.Contains("duckduckgo"))
            results = ExtractDuckDuckGo(html);
        else if (engineUrl.Contains("brave"))
            results = ExtractBrave(html);
        else if (engineUrl.Contains("bing"))
            results = ExtractBing(html);
        else
            results = ExtractGenericHrefs(html);

        // Always fall back to generic if engine-specific found nothing
        if (results.Count == 0)
            results = ExtractGenericHrefs(html);

        return results;
    }

    /// <summary>
    /// Google Search: with Playwright rendering, we get the full DOM.
    /// Results are in div.g containers with anchor tags to external sites.
    /// </summary>
    public static List<string> ExtractGoogle(string html)
    {
        var results = new List<string>();

        // Pattern 1: div class="g" ... <a href="https://..." (organic results)
        AddMatches(results, html,
            @"<div\s+class=""[^""]*\bg\b[^""]*""[^>]*>.*?<a[^>]*href=""(https?://[^""]+)""",
            RegexOptions.Singleline);

        // Pattern 2: yuRUbf container (Google's result anchor wrapper)
        AddMatches(results, html,
            @"class=""[^""]*yuRUbf[^""]*""[^>]*>.*?<a[^>]*href=""(https?://[^""]+)""",
            RegexOptions.Singleline);

        // Pattern 3: data-href attributes used in some Google layouts
        AddMatches(results, html,
            @"data-href=""(https?://[^""]+)""");

        // Pattern 4: cite tags often contain the URL text (for verification)
        // but primarily use the a href above

        return results.Distinct().Take(10).ToList();
    }

    /// <summary>
    /// Yahoo Search: result URLs are wrapped in r.search.yahoo.com redirect links
    /// with the actual destination embedded as RU=encoded_url.
    /// </summary>
    public static List<string> ExtractYahoo(string html)
    {
        var results = new List<string>();

        // Primary pattern: RU= parameter in Yahoo redirect URLs
        var ruMatches = Regex.Matches(html,
            @"RU=(https?%3[Aa]%2[Ff]%2[Ff][^/""&\s]+)",
            RegexOptions.IgnoreCase);
        foreach (Match m in ruMatches)
        {
            var decoded = Uri.UnescapeDataString(m.Groups[1].Value);
            if (IsValidResultUrl(decoded))
                results.Add(decoded);
        }

        // Fallback: compTitle links (Yahoo result titles)
        if (results.Count == 0)
        {
            AddMatches(results, html,
                @"class=""[^""]*compTitle[^""]*""[^>]*>.*?<a[^>]*href=""(https?://[^""]+)""",
                RegexOptions.Singleline);
        }

        return results.Distinct().Take(10).ToList();
    }

    /// <summary>
    /// Google Scholar: result links are in h3.gs_rt > a elements, or direct PDF links.
    /// </summary>
    public static List<string> ExtractGoogleScholar(string html)
    {
        var results = new List<string>();

        // Pattern 1: gs_rt (Google Scholar result title) > a href
        AddMatches(results, html,
            @"class=""gs_rt""[^>]*>.*?<a[^>]*href=""(https?://[^""]+)""",
            RegexOptions.Singleline);

        // Pattern 2: gs_or_ggsm (direct PDF/fulltext links)
        AddMatches(results, html,
            @"class=""gs_or_ggsm""[^>]*>.*?<a[^>]*href=""(https?://[^""]+)""",
            RegexOptions.Singleline);

        // Pattern 3: generic Scholar result links from the data-clk elements 
        AddMatches(results, html,
            @"data-clk[^>]*href=""(https?://[^""]+)""");

        // Pattern 4: direct links inside div.gs_ri
        AddMatches(results, html,
            @"class=""gs_ri""[^>]*>.*?<a[^>]*href=""(https?://[^""]+)""",
            RegexOptions.Singleline);

        return results.Distinct().Take(10).ToList();
    }

    /// <summary>
    /// DuckDuckGo HTML-only search: multiple patterns covering layout changes.
    /// </summary>
    public static List<string> ExtractDuckDuckGo(string html)
    {
        var results = new List<string>();

        // Pattern 1: class="result__a" with href after
        AddMatches(results, html,
            @"class=""result__a""[^>]*href=""(https?://[^""]+)""");

        // Pattern 2: href before class="result__a"
        AddMatches(results, html,
            @"href=""(https?://[^""]+)""[^>]*class=""result__a""");

        // Pattern 3: result__url class (alternative layout)
        AddMatches(results, html,
            @"class=""result__url""[^>]*href=""(https?://[^""]+)""");

        // Pattern 4: data-result href (newer DuckDuckGo)
        AddMatches(results, html,
            @"data-result[^>]*href=""(https?://[^""]+)""");

        // Pattern 5: result-link class
        AddMatches(results, html,
            @"class=""[^""]*result[-_]?link[^""]*""[^>]*href=""(https?://[^""]+)""");

        return results.Distinct().Take(10).ToList();
    }

    /// <summary>
    /// Brave Search: multiple patterns for result headers.
    /// </summary>
    public static List<string> ExtractBrave(string html)
    {
        var results = new List<string>();

        // Pattern 1: class containing "result-header" with href
        AddMatches(results, html,
            @"class=""[^""]*result-header[^""]*""[^>]*href=""(https?://[^""]+)""");

        // Pattern 2: href before class
        AddMatches(results, html,
            @"href=""(https?://[^""]+)""[^>]*class=""[^""]*result-header[^""]*""");

        // Pattern 3: snippet-title (alternative Brave layout)
        AddMatches(results, html,
            @"class=""[^""]*snippet[^""]*title[^""]*""[^>]*href=""(https?://[^""]+)""");

        // Pattern 4: heading link inside article/card sections
        AddMatches(results, html,
            @"<article[^>]*>.*?<a[^>]*href=""(https?://[^""]+)""",
            RegexOptions.Singleline);

        return results.Distinct().Take(10).ToList();
    }

    /// <summary>
    /// Bing: result links inside li.b_algo and various heading patterns.
    /// </summary>
    public static List<string> ExtractBing(string html)
    {
        var results = new List<string>();

        // Pattern 1: li.b_algo > h2 > a hierarchical 
        AddMatches(results, html,
            @"class=""b_algo""[^>]*>.*?<h2[^>]*>.*?<a[^>]*href=""(https?://[^""]+)""",
            RegexOptions.Singleline);

        // Pattern 2: simpler h2 > a href
        AddMatches(results, html,
            @"<h2><a\s+href=""(https?://[^""]+)""");

        // Pattern 3: b_title class
        AddMatches(results, html,
            @"class=""[^""]*b_title[^""]*""[^>]*>.*?href=""(https?://[^""]+)""",
            RegexOptions.Singleline);

        // Pattern 4: tilk class (Bing tile links)
        AddMatches(results, html,
            @"class=""[^""]*tilk[^""]*""[^>]*href=""(https?://[^""]+)""");

        return results.Distinct().Take(10).ToList();
    }

    /// <summary>
    /// Generic fallback: extract all https URLs from href attributes, filter out engine internals.
    /// </summary>
    public static List<string> ExtractGenericHrefs(string html)
    {
        var results = new List<string>();
        var matches = Regex.Matches(html,
            @"href=""(https?://[^""]+)""", RegexOptions.IgnoreCase);

        foreach (Match m in matches)
        {
            var url = CleanUrl(m.Groups[1].Value);
            if (IsValidResultUrl(url))
                results.Add(url);
        }

        return results.Distinct().Take(8).ToList();
    }

    private static void AddMatches(List<string> results, string html, string pattern,
        RegexOptions extraOptions = RegexOptions.None)
    {
        var matches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase | extraOptions);
        foreach (Match m in matches)
        {
            var url = CleanUrl(m.Groups[1].Value);
            if (IsValidResultUrl(url)) results.Add(url);
        }
    }

    private static string CleanUrl(string url)
    {
        // Handle DuckDuckGo redirect URLs
        if (url.Contains("duckduckgo.com/l/?uddg="))
        {
            var match = Regex.Match(url, @"uddg=(https?%3A[^&]+)");
            if (match.Success)
                return Uri.UnescapeDataString(match.Groups[1].Value);
        }
        // Handle Bing redirect URLs
        if (url.Contains("bing.com/ck/a?") || url.Contains("r.bing.com"))
        {
            var match = Regex.Match(url, @"[?&]u=a1(https?%3[Aa][^&]+)");
            if (match.Success)
                return Uri.UnescapeDataString(match.Groups[1].Value);
        }
        // Handle Yahoo redirect URLs
        if (url.Contains("r.search.yahoo.com"))
        {
            var match = Regex.Match(url, @"RU=(https?%3[Aa][^/""&\s]+)");
            if (match.Success)
                return Uri.UnescapeDataString(match.Groups[1].Value);
        }
        return url.Trim();
    }

    private static bool IsValidResultUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!url.StartsWith("http://") && !url.StartsWith("https://")) return false;
        if (url.Contains("javascript:")) return false;

        foreach (var blocked in BlockedDomains)
        {
            if (url.Contains(blocked, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
