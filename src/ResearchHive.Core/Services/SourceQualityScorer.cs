namespace ResearchHive.Core.Services;

/// <summary>
/// Scores URLs by domain authority. Higher scores = more authoritative sources.
/// Used when AppSettings.SourceQualityRanking is enabled.
/// </summary>
public static class SourceQualityScorer
{
    // ── Tier 1 (1.0): Government, education, major journals ──
    private static readonly HashSet<string> Tier1Domains = new(StringComparer.OrdinalIgnoreCase)
    {
        "pubmed.ncbi.nlm.nih.gov", "ncbi.nlm.nih.gov", "nih.gov",
        "nature.com", "sciencedirect.com", "springer.com", "wiley.com",
        "ieee.org", "arxiv.org", "jstor.org", "pnas.org",
        "science.org", "cell.com", "thelancet.com", "bmj.com",
        "nejm.org", "acs.org", "rsc.org", "mdpi.com",
        "scholar.google.com", "clinicaltrials.gov",
    };

    // ── Tier 2 (0.7): Known authoritative non-academic ──
    private static readonly HashSet<string> Tier2Domains = new(StringComparer.OrdinalIgnoreCase)
    {
        "wikipedia.org", "stackoverflow.com", "stackexchange.com",
        "cdc.gov", "who.int", "epa.gov", "fda.gov", "osha.gov",
        "europa.eu", "un.org", "worldbank.org",
        "github.com", "docs.microsoft.com", "learn.microsoft.com",
        "developer.mozilla.org", "cppreference.com",
        "mayoclinic.org", "webmd.com", "nist.gov",
    };

    // ── Tier 3 (0.5): Major news, reputable tech/science outlets ──
    private static readonly HashSet<string> Tier3Domains = new(StringComparer.OrdinalIgnoreCase)
    {
        "nytimes.com", "reuters.com", "bbc.com", "bbc.co.uk",
        "theguardian.com", "washingtonpost.com", "apnews.com",
        "arstechnica.com", "wired.com", "scienceamerican.com",
        "scientificamerican.com", "newscientist.com",
        "techcrunch.com", "theregister.com",
        "nationalgeographic.com", "smithsonianmag.com",
    };

    /// <summary>
    /// Score a URL's domain authority. Returns 0.0–1.0.
    /// </summary>
    public static double ScoreUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return 0.2;

        return ScoreUri(uri);
    }

    /// <summary>
    /// Score a parsed URI's domain authority. Returns 0.0–1.0.
    /// </summary>
    public static double ScoreUri(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        // Strip leading "www."
        if (host.StartsWith("www."))
            host = host[4..];

        // Check exact domain match first
        if (Tier1Domains.Contains(host)) return 1.0;
        if (Tier2Domains.Contains(host)) return 0.7;
        if (Tier3Domains.Contains(host)) return 0.5;

        // Check parent domain (e.g., "pubs.acs.org" → "acs.org")
        var parts = host.Split('.');
        if (parts.Length > 2)
        {
            var parentDomain = string.Join('.', parts[^2..]);
            if (Tier1Domains.Contains(parentDomain)) return 1.0;
            if (Tier2Domains.Contains(parentDomain)) return 0.7;
            if (Tier3Domains.Contains(parentDomain)) return 0.5;
        }

        // TLD-based scoring
        if (host.EndsWith(".gov") || host.EndsWith(".gov.uk") || host.EndsWith(".mil"))
            return 0.9;
        if (host.EndsWith(".edu") || host.EndsWith(".ac.uk") || host.EndsWith(".ac.jp"))
            return 0.85;
        if (host.EndsWith(".org"))
            return 0.5;

        // Path-based boost: research/paper/study indicators
        var path = uri.AbsolutePath.ToLowerInvariant();
        double pathBoost = 0;
        if (path.Contains("research") || path.Contains("paper") || path.Contains("study")
            || path.Contains("journal") || path.Contains("article") || path.Contains("publication"))
            pathBoost = 0.1;

        // Default tier: everything else (blogs, forums, commercial)
        return 0.2 + pathBoost;
    }

    /// <summary>
    /// Returns a human-readable quality tier label for display.
    /// </summary>
    public static string GetTierLabel(double score) => score switch
    {
        >= 0.9 => "Academic",
        >= 0.7 => "Authoritative",
        >= 0.5 => "News/Reputable",
        >= 0.3 => "General",
        _ => "Unranked"
    };
}
