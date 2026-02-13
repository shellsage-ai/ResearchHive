using ResearchHive.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace ResearchHive.Core.Services;

/// <summary>
/// Post-generation verifier: validates LLM-generated strengths, gaps, and complements
/// against the deterministic fact sheet. Removes hallucinations and fixes inconsistencies.
///
/// Checks:
///   1. Reject gap claims for capabilities listed as PROVEN in the fact sheet
///   2. Reject strength claims for capabilities listed as ABSENT
///   3. Flag frameworks not found in active packages or fact sheet
///   4. Validate complement URLs (HTTP HEAD) — kill hallucinated repos
///   5. Ecosystem match — reject cross-ecosystem suggestions (e.g., Java lib for .NET)
///   6. Redundancy — reject complements that duplicate existing capabilities
/// </summary>
public class PostScanVerifier
{
    private readonly HttpClient _http;
    private readonly ILogger<PostScanVerifier>? _logger;

    public PostScanVerifier(ILogger<PostScanVerifier>? logger = null)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new System.Net.Http.Headers.ProductInfoHeaderValue("ResearchHive", "1.0"));
    }

    /// <summary>
    /// Verify and clean up LLM-generated profile data against the fact sheet.
    /// Returns a summary of corrections made.
    /// </summary>
    public async Task<VerificationResult> VerifyAsync(
        RepoProfile profile, RepoFactSheet factSheet, CancellationToken ct = default)
    {
        var result = new VerificationResult();

        // 1. Prune hallucinated gaps (capability actually exists)
        PruneHallucinatedGaps(profile, factSheet, result);

        // 2. Prune hallucinated strengths (capability confirmed absent)
        PruneHallucinatedStrengths(profile, factSheet, result);

        // 3. Reject phantom frameworks listed as if active
        PrunePhantomFrameworks(profile, factSheet, result);

        // 4. Validate complement URLs + ecosystem + redundancy
        await ValidateComplementsAsync(profile, factSheet, result, ct);

        // 5. Inject fact-sheet-derived strengths the LLM may have missed
        InjectProvenStrengths(profile, factSheet, result);

        // 6. Inject fact-sheet-derived gaps the LLM may have missed
        InjectConfirmedGaps(profile, factSheet, result);

        _logger?.LogInformation(
            "PostScanVerifier: {GapsRemoved} gaps removed, {StrengthsRemoved} strengths removed, " +
            "{ComplementsRemoved} complements removed, {StrengthsAdded} strengths added, {GapsAdded} gaps added",
            result.GapsRemoved.Count, result.StrengthsRemoved.Count,
            result.ComplementsRemoved.Count, result.StrengthsAdded.Count, result.GapsAdded.Count);

        return result;
    }

    /// <summary>Remove gaps that contradict proven capabilities.</summary>
    private static void PruneHallucinatedGaps(RepoProfile profile, RepoFactSheet factSheet, VerificationResult result)
    {
        var toRemove = new List<string>();

        foreach (var gap in profile.Gaps)
        {
            var gapLower = gap.ToLowerInvariant();

            foreach (var proven in factSheet.ProvenCapabilities)
            {
                var capLower = proven.Capability.ToLowerInvariant();
                // Check if the gap is about a capability we've proven exists
                var keywords = ExtractKeywords(capLower);
                if (keywords.Any(kw => gapLower.Contains(kw)))
                {
                    toRemove.Add(gap);
                    result.GapsRemoved.Add($"HALLUCINATED: \"{gap}\" — contradicts proven capability: {proven.Capability} ({proven.Evidence})");
                    break;
                }
            }
        }

        foreach (var gap in toRemove)
            profile.Gaps.Remove(gap);
    }

    /// <summary>Remove strengths that contradict confirmed-absent capabilities.</summary>
    private static void PruneHallucinatedStrengths(RepoProfile profile, RepoFactSheet factSheet, VerificationResult result)
    {
        var toRemove = new List<string>();

        foreach (var strength in profile.Strengths)
        {
            var strLower = strength.ToLowerInvariant();

            // Check against confirmed-absent capabilities
            foreach (var absent in factSheet.ConfirmedAbsent)
            {
                var keywords = ExtractKeywords(absent.Capability.ToLowerInvariant());
                if (keywords.Any(kw => strLower.Contains(kw)))
                {
                    toRemove.Add(strength);
                    result.StrengthsRemoved.Add($"HALLUCINATED: \"{strength}\" — contradicts absence: {absent.Capability}");
                    break;
                }
            }

            // Check for phantom packages claimed as strengths
            foreach (var phantom in factSheet.PhantomPackages)
            {
                if (strLower.Contains(phantom.PackageName.ToLowerInvariant()))
                {
                    toRemove.Add(strength);
                    result.StrengthsRemoved.Add($"PHANTOM-DEP: \"{strength}\" — {phantom.PackageName} is installed but unused");
                    break;
                }
            }
        }

        foreach (var strength in toRemove)
            profile.Strengths.Remove(strength);
    }

    /// <summary>Remove framework entries based on phantom packages or wrong technologies.</summary>
    private static void PrunePhantomFrameworks(RepoProfile profile, RepoFactSheet factSheet, VerificationResult result)
    {
        var toRemove = new List<string>();

        foreach (var fw in profile.Frameworks)
        {
            var fwLower = fw.ToLowerInvariant();

            // If this framework references a phantom package, remove it
            foreach (var phantom in factSheet.PhantomPackages)
            {
                // Only remove if it's specifically about that package's functionality
                // (not just a coincidental name match)
                if (fwLower.Contains(phantom.PackageName.ToLowerInvariant()) &&
                    // Ensure it's not a build tool (always considered active)
                    !phantom.PackageName.StartsWith("coverlet", StringComparison.OrdinalIgnoreCase))
                {
                    toRemove.Add(fw);
                    result.FrameworksRemoved.Add($"PHANTOM: \"{fw}\" — {phantom.PackageName} installed but unused");
                    break;
                }
            }

            // Check for wrong app type claims
            if (fwLower.Contains("asp.net core") && factSheet.AppType.Contains("WPF", StringComparison.OrdinalIgnoreCase))
            {
                toRemove.Add(fw);
                result.FrameworksRemoved.Add($"WRONG-TYPE: \"{fw}\" — app is {factSheet.AppType}, not ASP.NET");
            }
            if (fwLower.Contains("entity framework") && factSheet.DatabaseTechnology.Contains("Raw SQLite", StringComparison.OrdinalIgnoreCase))
            {
                toRemove.Add(fw);
                result.FrameworksRemoved.Add($"WRONG-DB: \"{fw}\" — database is {factSheet.DatabaseTechnology}");
            }
        }

        foreach (var fw in toRemove)
            profile.Frameworks.Remove(fw);
    }

    /// <summary>Validate complement project URLs, ecosystem match, and redundancy.</summary>
    private async Task ValidateComplementsAsync(
        RepoProfile profile, RepoFactSheet factSheet, VerificationResult result, CancellationToken ct)
    {
        var toRemove = new List<ComplementProject>();

        foreach (var comp in profile.ComplementSuggestions)
        {
            // ── Ecosystem check ──
            if (!IsEcosystemCompatible(comp, factSheet))
            {
                toRemove.Add(comp);
                result.ComplementsRemoved.Add($"WRONG-ECOSYSTEM: \"{comp.Name}\" — not compatible with {factSheet.Ecosystem}");
                continue;
            }

            // ── Redundancy check: does this complement overlap with a proven capability? ──
            var compLower = (comp.Purpose + " " + comp.WhatItAdds + " " + comp.Name).ToLowerInvariant();
            bool redundant = false;
            foreach (var proven in factSheet.ProvenCapabilities)
            {
                var keywords = ExtractKeywords(proven.Capability.ToLowerInvariant());
                if (keywords.Count(kw => compLower.Contains(kw)) >= 2) // Require 2+ keyword matches for redundancy
                {
                    toRemove.Add(comp);
                    result.ComplementsRemoved.Add($"REDUNDANT: \"{comp.Name}\" — project already has: {proven.Capability}");
                    redundant = true;
                    break;
                }
            }
            if (redundant) continue;

            // ── Redundancy with existing test framework ──
            if (!string.IsNullOrEmpty(factSheet.TestFramework))
            {
                var testFrameworks = new[] { "xunit", "nunit", "mstest", "jest", "mocha", "pytest" };
                var compNameLower = comp.Name.ToLowerInvariant();
                bool isTestFramework = testFrameworks.Any(tf => compNameLower.Contains(tf));
                bool isAlreadyUsedFramework = compNameLower.Contains(factSheet.TestFramework.ToLowerInvariant());

                if (isTestFramework && !isAlreadyUsedFramework)
                {
                    // Suggesting a DIFFERENT test framework when one already exists = redundant
                    toRemove.Add(comp);
                    result.ComplementsRemoved.Add($"REDUNDANT-TEST: \"{comp.Name}\" — already using {factSheet.TestFramework}");
                    continue;
                }
            }

            // ── URL validation (HTTP HEAD) ── only for GitHub URLs
            if (comp.Url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Head, comp.Url);
                    using var response = await _http.SendAsync(request, ct);
                    if (!response.IsSuccessStatusCode && (int)response.StatusCode != 301 && (int)response.StatusCode != 302)
                    {
                        toRemove.Add(comp);
                        result.ComplementsRemoved.Add($"DEAD-URL: \"{comp.Name}\" — {comp.Url} returned {(int)response.StatusCode}");
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    toRemove.Add(comp);
                    result.ComplementsRemoved.Add($"DEAD-URL: \"{comp.Name}\" — {comp.Url} unreachable: {ex.Message}");
                }
            }
        }

        foreach (var comp in toRemove)
            profile.ComplementSuggestions.Remove(comp);
    }

    /// <summary>Add proven capabilities as strengths if the LLM missed them.</summary>
    private static void InjectProvenStrengths(RepoProfile profile, RepoFactSheet factSheet, VerificationResult result)
    {
        foreach (var proven in factSheet.ProvenCapabilities)
        {
            var keywords = ExtractKeywords(proven.Capability.ToLowerInvariant());
            bool alreadyMentioned = profile.Strengths.Any(s =>
                keywords.Any(kw => s.Contains(kw, StringComparison.OrdinalIgnoreCase)));

            if (!alreadyMentioned)
            {
                var strength = $"{proven.Capability} ({proven.Evidence})";
                profile.Strengths.Add(strength);
                result.StrengthsAdded.Add($"INJECTED: {strength}");
            }
        }
    }

    /// <summary>Add confirmed-absent items as gaps if the LLM missed them.</summary>
    private static void InjectConfirmedGaps(RepoProfile profile, RepoFactSheet factSheet, VerificationResult result)
    {
        // Only inject from diagnostic files missing + confirmed absent capabilities that are important
        foreach (var missing in factSheet.DiagnosticFilesMissing)
        {
            bool alreadyMentioned = profile.Gaps.Any(g =>
                g.Contains(missing, StringComparison.OrdinalIgnoreCase) ||
                ExtractKeywords(missing.ToLowerInvariant()).Any(kw => g.ToLowerInvariant().Contains(kw)));

            if (!alreadyMentioned)
            {
                var gap = $"No {missing} found";
                profile.Gaps.Add(gap);
                result.GapsAdded.Add($"INJECTED: {gap}");
            }
        }
    }

    /// <summary>Check if a complement project is in the same language ecosystem.</summary>
    private static bool IsEcosystemCompatible(ComplementProject comp, RepoFactSheet factSheet)
    {
        if (string.IsNullOrEmpty(factSheet.Ecosystem)) return true;

        var ecosystem = factSheet.Ecosystem.ToLowerInvariant();
        var compText = $"{comp.Name} {comp.Purpose} {comp.WhatItAdds}".ToLowerInvariant();

        // Known cross-ecosystem indicators
        var javaIndicators = new[] { "java", "kotlin", "maven", "gradle", "spring", "jvm" };
        var dotnetIndicators = new[] { ".net", "c#", "nuget", "csharp", "dotnet", "asp.net" };
        var pythonIndicators = new[] { "python", "pip", "django", "flask", "pytorch" };
        var jsIndicators = new[] { "javascript", "node.js", "npm", "yarn", "react", "angular", "vue" };
        var rustIndicators = new[] { "rust", "cargo", "crate" };

        // If the project ecosystem is .NET and the complement smells like Java/Python/etc., reject
        if (ecosystem.Contains(".net") || ecosystem.Contains("c#"))
        {
            if (javaIndicators.Any(i => compText.Contains(i)) && !dotnetIndicators.Any(i => compText.Contains(i)))
                return false;
            // Python-only tools for a .NET project
            if (pythonIndicators.Count(i => compText.Contains(i)) >= 2 && !dotnetIndicators.Any(i => compText.Contains(i)))
                return false;
        }
        else if (ecosystem.Contains("node") || ecosystem.Contains("javascript"))
        {
            if (dotnetIndicators.Any(i => compText.Contains(i)) && !jsIndicators.Any(i => compText.Contains(i)))
                return false;
        }
        else if (ecosystem.Contains("python"))
        {
            if (dotnetIndicators.Any(i => compText.Contains(i)) && !pythonIndicators.Any(i => compText.Contains(i)))
                return false;
        }

        return true;
    }

    /// <summary>Extract significant keywords from a capability label for fuzzy matching.</summary>
    private static List<string> ExtractKeywords(string text)
    {
        // Split on spaces/punctuation, take words 3+ chars, skip common stop words
        var stopWords = new HashSet<string> { "the", "and", "for", "with", "via", "from", "not", "found", "has", "are", "all" };
        return Regex.Split(text, @"[\s/\(\)\-,]+")
            .Where(w => w.Length >= 3 && !stopWords.Contains(w))
            .Distinct()
            .ToList();
    }
}

/// <summary>Summary of all corrections made by the post-scan verifier.</summary>
public class VerificationResult
{
    public List<string> GapsRemoved { get; set; } = new();
    public List<string> StrengthsRemoved { get; set; } = new();
    public List<string> FrameworksRemoved { get; set; } = new();
    public List<string> ComplementsRemoved { get; set; } = new();
    public List<string> StrengthsAdded { get; set; } = new();
    public List<string> GapsAdded { get; set; } = new();

    public int TotalCorrections =>
        GapsRemoved.Count + StrengthsRemoved.Count + FrameworksRemoved.Count +
        ComplementsRemoved.Count + StrengthsAdded.Count + GapsAdded.Count;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (GapsRemoved.Count > 0) parts.Add($"{GapsRemoved.Count} hallucinated gaps removed");
            if (StrengthsRemoved.Count > 0) parts.Add($"{StrengthsRemoved.Count} hallucinated strengths removed");
            if (FrameworksRemoved.Count > 0) parts.Add($"{FrameworksRemoved.Count} phantom frameworks removed");
            if (ComplementsRemoved.Count > 0) parts.Add($"{ComplementsRemoved.Count} invalid complements removed");
            if (StrengthsAdded.Count > 0) parts.Add($"{StrengthsAdded.Count} proven strengths injected");
            if (GapsAdded.Count > 0) parts.Add($"{GapsAdded.Count} confirmed gaps injected");
            return parts.Count > 0 ? string.Join(" | ", parts) : "No corrections needed";
        }
    }
}
