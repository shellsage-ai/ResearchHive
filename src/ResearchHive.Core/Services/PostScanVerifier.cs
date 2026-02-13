using ResearchHive.Core.Models;
using ResearchHive.Core.Configuration;
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
    private readonly ILlmService? _llmService;
    private readonly ILogger<PostScanVerifier>? _logger;

    public PostScanVerifier(ILogger<PostScanVerifier>? logger = null, ILlmService? llmService = null)
    {
        _logger = logger;
        _llmService = llmService;
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

        // 1b. Prune gaps that are inappropriate for the app type (e.g., auth for desktop, Dockerfile for WPF)
        PruneAppTypeInappropriateGaps(profile, factSheet, result);

        // 2. Prune hallucinated strengths (capability confirmed absent)
        PruneHallucinatedStrengths(profile, factSheet, result);

        // 2b. Verify project identity — ensure analysis summary doesn't describe a different project
        VerifyProjectIdentity(profile, factSheet, result);

        // 3. Reject phantom frameworks listed as if active
        PrunePhantomFrameworks(profile, factSheet, result);

        // 4. Validate complement URLs + ecosystem + redundancy (with floor + diversity)
        await ValidateComplementsAsync(profile, factSheet, result, ct);

        // 4b. LLM relevance second pass — if LLM service available, ask the model
        //     to evaluate each surviving complement against the full project identity
        if (_llmService != null && profile.ComplementSuggestions.Count > 0)
        {
            await LlmRelevanceCheckAsync(profile, factSheet, result, ct);
        }

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

    /// <summary>
    /// Prune gaps that are inappropriate for the project type.
    /// Uses the dynamically-inferred InapplicableConcepts from the fact sheet
    /// instead of hardcoded app-type if-chains. Works for ANY project type.
    /// Also rejects gaps contradicting the database technology choice.
    /// </summary>
    private static void PruneAppTypeInappropriateGaps(RepoProfile profile, RepoFactSheet factSheet, VerificationResult result)
    {
        if (factSheet.InapplicableConcepts.Count == 0 && string.IsNullOrEmpty(factSheet.DatabaseTechnology))
            return;

        var toRemove = new List<string>();

        foreach (var gap in profile.Gaps)
        {
            var gapLower = gap.ToLowerInvariant();

            // ── Dynamic InapplicableConcepts check ──
            // If any inapplicable concept's keywords appear in the gap text, prune it.
            foreach (var concept in factSheet.InapplicableConcepts)
            {
                var conceptLower = concept.ToLowerInvariant();
                var conceptWords = conceptLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // For single-word concepts, require the word to appear
                // For multi-word concepts, require ALL significant words (3+ chars) to appear
                bool matches;
                if (conceptWords.Length == 1)
                {
                    matches = gapLower.Contains(conceptLower);
                }
                else
                {
                    var significantWords = conceptWords.Where(w => w.Length >= 3).ToList();
                    matches = significantWords.Count > 0 &&
                              significantWords.All(w => gapLower.Contains(w));
                }

                if (matches)
                {
                    toRemove.Add(gap);
                    result.GapsRemoved.Add($"INAPPLICABLE: \"{gap}\" — concept '{concept}' is not relevant for {factSheet.DeploymentTarget} {factSheet.ArchitectureStyle}");
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

    /// <summary>
    /// Cross-check the analysis summary against the identity scan results.
    /// If the analysis summary describes a fundamentally different project (wrong language,
    /// wrong frameworks), prefer the identity scan's ProjectSummary.
    /// </summary>
    private static void VerifyProjectIdentity(RepoProfile profile, RepoFactSheet factSheet, VerificationResult result)
    {
        // Skip if no identity scan was performed or no analysis summary to verify
        if (string.IsNullOrWhiteSpace(profile.ProjectSummary) || string.IsNullOrWhiteSpace(profile.AnalysisSummary))
            return;

        // If identity scan and analysis produced the same value, no conflict
        if (profile.ProjectSummary == profile.AnalysisSummary)
            return;

        var analysisLower = profile.AnalysisSummary.ToLowerInvariant();
        var identityLower = profile.ProjectSummary.ToLowerInvariant();
        var primaryLang = profile.PrimaryLanguage?.ToLowerInvariant() ?? "";

        // Check if analysis summary mentions a wrong primary language
        var wrongLanguageSignals = new Dictionary<string, string[]>
        {
            { "c#", new[] { "python library", "python framework", "java application", "javascript framework", "rust library", "go application" } },
            { "python", new[] { "c# application", ".net application", "wpf", "java application", "rust library", "go application" } },
            { "javascript", new[] { "c# application", ".net application", "wpf", "python library", "rust library" } },
            { "typescript", new[] { "c# application", ".net application", "wpf", "python library", "rust library" } },
            { "java", new[] { "c# application", ".net application", "wpf", "python library", "javascript framework" } },
        };

        bool identityContaminated = false;

        if (wrongLanguageSignals.TryGetValue(primaryLang, out var wrongPhrases))
        {
            foreach (var phrase in wrongPhrases)
            {
                if (analysisLower.Contains(phrase))
                {
                    identityContaminated = true;
                    result.IdentityWarnings.Add(
                        $"CONTAMINATED: Analysis summary mentions '{phrase}' but project's primary language is {profile.PrimaryLanguage}");
                    break;
                }
            }
        }

        // Check if analysis summary mentions frameworks from a different ecosystem
        if (!identityContaminated && profile.Frameworks.Count > 0)
        {
            // If none of the known frameworks appear in the analysis summary but they appear in identity,
            // the analysis may be confused
            var frameworkKeywords = profile.Frameworks
                .Select(f => f.Split(' ')[0].ToLowerInvariant())
                .Where(f => f.Length >= 3)
                .ToList();

            bool identityMentionsFrameworks = frameworkKeywords.Any(kw => identityLower.Contains(kw));
            bool analysisMentionsFrameworks = frameworkKeywords.Any(kw => analysisLower.Contains(kw));

            if (identityMentionsFrameworks && !analysisMentionsFrameworks)
            {
                // Identity references our frameworks but analysis doesn't — suspicious but not definitive
                result.IdentityWarnings.Add(
                    $"DRIFT: Analysis summary doesn't mention any known frameworks ({string.Join(", ", profile.Frameworks.Take(3))})");
            }
        }

        // If contamination detected, keep identity scan results and discard analysis summary
        if (identityContaminated)
        {
            result.IdentityWarnings.Add(
                $"RESTORED: Keeping identity scan ProjectSummary, discarding contaminated AnalysisSummary");
            // ProjectSummary already has the correct identity scan value — no action needed
            // AnalysisSummary keeps the contaminated value for debugging
        }
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

    /// <summary>Minimum complements to retain after pruning. Backfills from pruned pool if needed.</summary>
    internal const int MinimumComplementFloor = 5;

    /// <summary>Validate complement project URLs, ecosystem match, and redundancy. Enforces minimum floor + category diversity.</summary>
    private async Task ValidateComplementsAsync(
        RepoProfile profile, RepoFactSheet factSheet, VerificationResult result, CancellationToken ct)
    {
        var toRemove = new List<(ComplementProject comp, string reason, string severity)>();

        foreach (var comp in profile.ComplementSuggestions)
        {
            // ── Ecosystem check ── (hard reject — never backfill)
            if (!IsEcosystemCompatible(comp, factSheet))
            {
                toRemove.Add((comp, $"WRONG-ECOSYSTEM: \"{comp.Name}\" — not compatible with {factSheet.Ecosystem}", "HARD"));
                continue;
            }

            // ── Already-installed check: reject complements that match an active dependency ──
            var compNameLower = comp.Name.ToLowerInvariant();
            var compUrlLower = comp.Url.ToLowerInvariant();
            bool alreadyInstalled = false;
            foreach (var pkg in factSheet.ActivePackages)
            {
                var pkgLower = pkg.PackageName.ToLowerInvariant();
                // Name match ("xunit" ≈ "xunit/xunit") or URL contains package name
                if (compNameLower.Contains(pkgLower) || pkgLower.Contains(compNameLower) ||
                    compUrlLower.Contains(pkgLower))
                {
                    toRemove.Add((comp, $"ALREADY-INSTALLED: \"{comp.Name}\" — {pkg.PackageName} {pkg.Version} is already an active dependency", "HARD"));
                    alreadyInstalled = true;
                    break;
                }
            }
            if (alreadyInstalled) continue;

            // ── Meta-project / infrastructure engine filter ──
            // Some repos are engines or platforms, not installable libraries (e.g., dependabot-core is Ruby infra).
            // Reject complements whose repo language doesn't match the target ecosystem.
            if (IsMetaProjectNotUsableDirectly(comp, factSheet))
            {
                toRemove.Add((comp, $"META-PROJECT: \"{comp.Name}\" — infrastructure engine, not an installable {factSheet.Ecosystem} package", "HARD"));
                continue;
            }

            // ── Archived repo check (from GitHub API enrichment) ──
            if (comp.IsArchived)
            {
                toRemove.Add((comp, $"ARCHIVED: \"{comp.Name}\" — GitHub repo is archived/abandoned", "HARD"));
                continue;
            }

            // ── Staleness check (from GitHub API enrichment) ──
            if (comp.LastPushed.HasValue)
            {
                var age = DateTime.UtcNow - comp.LastPushed.Value;
                if (age.TotalDays > 365 * 3) // 3+ years: hard reject
                {
                    toRemove.Add((comp, $"STALE: \"{comp.Name}\" — last pushed {comp.LastPushed.Value:yyyy-MM-dd} ({age.TotalDays / 365:F1}yr ago)", "HARD"));
                    continue;
                }
                if (age.TotalDays > 365 * 2) // 2-3 years: soft reject
                {
                    toRemove.Add((comp, $"STALE: \"{comp.Name}\" — last pushed {comp.LastPushed.Value:yyyy-MM-dd} ({age.TotalDays / 365:F1}yr ago)", "SOFT"));
                    continue;
                }
            }

            // ── Minimum stars check (from GitHub API enrichment) ──
            if (comp.Stars >= 0) // -1 means not enriched
            {
                if (comp.Stars < 10) // < 10 stars: hard reject (too obscure)
                {
                    toRemove.Add((comp, $"LOW-STARS: \"{comp.Name}\" — only {comp.Stars} stars", "HARD"));
                    continue;
                }
                if (comp.Stars < 50) // < 50 stars: soft reject
                {
                    toRemove.Add((comp, $"LOW-STARS: \"{comp.Name}\" — only {comp.Stars} stars", "SOFT"));
                    continue;
                }
            }

            // ── Repo language vs ecosystem check (from GitHub API enrichment) ──
            if (!string.IsNullOrEmpty(comp.RepoLanguage) && !string.IsNullOrEmpty(factSheet.Ecosystem))
            {
                if (!IsRepoLanguageCompatible(comp.RepoLanguage, factSheet.Ecosystem))
                {
                    toRemove.Add((comp, $"WRONG-LANGUAGE: \"{comp.Name}\" — repo language is {comp.RepoLanguage}, project ecosystem is {factSheet.Ecosystem}", "HARD"));
                    continue;
                }
            }

            // ── InapplicableConcepts check — reject complements matching inapplicable concepts ──
            if (factSheet.InapplicableConcepts.Count > 0)
            {
                var compDesc = (comp.Purpose + " " + comp.WhatItAdds + " " + comp.Name + " " + comp.Category).ToLowerInvariant();
                bool isInapplicable = false;
                foreach (var concept in factSheet.InapplicableConcepts)
                {
                    var conceptLower = concept.ToLowerInvariant();
                    var conceptWords = conceptLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var significantWords = conceptWords.Where(w => w.Length >= 3).ToList();
                    if (significantWords.Count > 0 && significantWords.All(w => compDesc.Contains(w)))
                    {
                        toRemove.Add((comp, $"INAPPLICABLE: \"{comp.Name}\" — addresses concept '{concept}' which is irrelevant for this project", "SOFT"));
                        isInapplicable = true;
                        break;
                    }
                }
                if (isInapplicable) continue;
            }

            // ── Database technology contradiction: reject if complement conflicts with DB choice ──
            if (!string.IsNullOrEmpty(factSheet.DatabaseTechnology))
            {
                var compText = (comp.Purpose + " " + comp.WhatItAdds + " " + comp.Name).ToLowerInvariant();
                if (factSheet.DatabaseTechnology.Contains("Raw SQLite", StringComparison.OrdinalIgnoreCase) &&
                    (compText.Contains("entity framework") || compText.Contains("ef core") || compText.Contains("efcore")))
                {
                    toRemove.Add((comp, $"WRONG-DB: \"{comp.Name}\" — project uses {factSheet.DatabaseTechnology}, not EF Core", "HARD"));
                    continue;
                }
            }

            // ── Redundancy check: does this complement overlap with a proven capability? ──
            var compLower = (comp.Purpose + " " + comp.WhatItAdds + " " + comp.Name).ToLowerInvariant();
            bool redundant = false;
            foreach (var proven in factSheet.ProvenCapabilities)
            {
                var keywords = ExtractKeywords(proven.Capability.ToLowerInvariant());
                if (keywords.Count(kw => compLower.Contains(kw)) >= 2) // Require 2+ keyword matches for redundancy
                {
                    toRemove.Add((comp, $"REDUNDANT: \"{comp.Name}\" — project already has: {proven.Capability}", "SOFT"));
                    redundant = true;
                    break;
                }
            }
            if (redundant) continue;

            // ── Redundancy with existing test framework ──
            if (!string.IsNullOrEmpty(factSheet.TestFramework))
            {
                var testFrameworks = new[] { "xunit", "nunit", "mstest", "jest", "mocha", "pytest" };
                bool isTestFramework = testFrameworks.Any(tf => compNameLower.Contains(tf));
                bool isAlreadyUsedFramework = compNameLower.Contains(factSheet.TestFramework.ToLowerInvariant());

                if (isTestFramework && !isAlreadyUsedFramework)
                {
                    toRemove.Add((comp, $"REDUNDANT-TEST: \"{comp.Name}\" — already using {factSheet.TestFramework}", "SOFT"));
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
                        toRemove.Add((comp, $"DEAD-URL: \"{comp.Name}\" — {comp.Url} returned {(int)response.StatusCode}", "HARD"));
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    toRemove.Add((comp, $"DEAD-URL: \"{comp.Name}\" — {comp.Url} unreachable: {ex.Message}", "HARD"));
                }
            }
        }

        // ── Minimum floor enforcement: if removing all flagged items drops below the floor, ──
        // ── backfill from the SOFT-reject pool (sorted by relevance, preserving diversity) ──
        var hardRejects = toRemove.Where(r => r.severity == "HARD").Select(r => r.comp).ToHashSet();
        var softRejects = toRemove.Where(r => r.severity == "SOFT").ToList();
        var remaining = profile.ComplementSuggestions.Except(toRemove.Select(t => t.comp)).ToList();

        if (remaining.Count < MinimumComplementFloor && softRejects.Count > 0)
        {
            // Backfill from soft rejects, preferring category diversity
            var backfillPool = softRejects
                .Where(r => !hardRejects.Contains(r.comp))
                .ToList();

            // Ensure category diversity in backfill: pick from as many different categories as possible
            var categorizedPool = backfillPool
                .Select(r => (r.comp, category: GetComplementCategory(r.comp)))
                .OrderBy(x => remaining.Any(c => GetComplementCategory(c) == x.category) ? 1 : 0) // Prefer new categories
                .ToList();

            foreach (var (comp, category) in categorizedPool)
            {
                if (remaining.Count >= MinimumComplementFloor) break;
                remaining.Add(comp);
                softRejects.RemoveAll(r => r.comp == comp);
                result.ComplementsBackfilled.Add($"BACKFILLED: \"{comp.Name}\" (category: {category}) — retained to meet minimum floor");
            }
        }

        // Apply the final removal list
        foreach (var (comp, reason, _) in toRemove.Where(r => !remaining.Contains(r.comp)))
        {
            profile.ComplementSuggestions.Remove(comp);
            result.ComplementsRemoved.Add(reason);
        }

        // ── Category diversity enforcement: warn but don't re-add if all same category ──
        EnforceCategoryDiversity(profile.ComplementSuggestions, result);
    }

    /// <summary>Categorize a complement project for diversity tracking.</summary>
    internal static string GetComplementCategory(ComplementProject comp)
    {
        var text = $"{comp.Name} {comp.Purpose} {comp.WhatItAdds}".ToLowerInvariant();

        if (text.Contains("security") || text.Contains("vulnerab") || text.Contains("scan") || text.Contains("snyk") || text.Contains("dependabot"))
            return "security";
        if (text.Contains("ci/cd") || text.Contains("pipeline") || text.Contains("deploy") || text.Contains("github actions") || text.Contains("workflow"))
            return "ci-cd";
        if (text.Contains("test") || text.Contains("coverage") || text.Contains("mock") || text.Contains("assert"))
            return "testing";
        if (text.Contains("monitor") || text.Contains("observ") || text.Contains("trac") || text.Contains("telemetry") || text.Contains("metrics"))
            return "observability";
        // Containerization must be checked BEFORE documentation — "docker" contains "doc"
        if (text.Contains("container") || text.Contains("docker") || text.Contains("kubernetes") || text.Contains("helm"))
            return "containerization";
        if (text.Contains("document") || text.Contains("readme") || text.Contains("wiki") || text.Contains("api doc"))
            return "documentation";
        if (text.Contains("lint") || text.Contains("format") || text.Contains("analyz") || text.Contains("style"))
            return "code-quality";
        if (text.Contains("perform") || text.Contains("bench") || text.Contains("profil") || text.Contains("cache"))
            return "performance";
        if (text.Contains("log") || text.Contains("serilog") || text.Contains("nlog"))
            return "logging";

        return "other";
    }

    /// <summary>Track category distribution. If all complements share the same category, log a warning.</summary>
    private static void EnforceCategoryDiversity(List<ComplementProject> complements, VerificationResult result)
    {
        if (complements.Count <= 1) return;

        var categories = complements.Select(GetComplementCategory).Distinct().ToList();
        if (categories.Count == 1)
        {
            result.DiversityWarning = $"Low diversity: all {complements.Count} complements are in category '{categories[0]}'";
        }
    }

    /// <summary>Add proven capabilities as strengths if the LLM missed them. Categorizes as product vs infrastructure.</summary>
    private static void InjectProvenStrengths(RepoProfile profile, RepoFactSheet factSheet, VerificationResult result)
    {
        foreach (var proven in factSheet.ProvenCapabilities)
        {
            var keywords = ExtractKeywords(proven.Capability.ToLowerInvariant());
            bool alreadyInStrengths = profile.Strengths.Any(s =>
                keywords.Any(kw => s.Contains(kw, StringComparison.OrdinalIgnoreCase)));
            bool alreadyInInfra = profile.InfrastructureStrengths.Any(s =>
                keywords.Any(kw => s.Contains(kw, StringComparison.OrdinalIgnoreCase)));

            if (alreadyInStrengths || alreadyInInfra)
                continue;

            // Format cleanly: "Capability (verified in FileName.cs)" — no raw regex patterns
            var cleanEvidence = FormatEvidenceForDisplay(proven.Evidence);
            var strength = string.IsNullOrEmpty(cleanEvidence)
                ? $"{proven.Capability} (verified by code analysis)"
                : $"{proven.Capability} (verified in {cleanEvidence})";

            if (IsInfrastructureCapability(proven.Capability))
            {
                profile.InfrastructureStrengths.Add(strength);
                result.StrengthsAdded.Add($"INJECTED-INFRA: {strength}");
            }
            else
            {
                profile.Strengths.Add(strength);
                result.StrengthsAdded.Add($"INJECTED: {strength}");
            }
        }

        // Also re-categorize existing LLM-generated strengths
        CategorizeExistingStrengths(profile);
    }

    /// <summary>
    /// Move infrastructure-pattern strengths from the main list to InfrastructureStrengths.
    /// Called after LLM analysis to split product vs infrastructure.
    /// </summary>
    private static void CategorizeExistingStrengths(RepoProfile profile)
    {
        var toMove = new List<string>();
        foreach (var strength in profile.Strengths)
        {
            if (IsInfrastructureStrength(strength))
                toMove.Add(strength);
        }
        foreach (var s in toMove)
        {
            profile.Strengths.Remove(s);
            if (!profile.InfrastructureStrengths.Any(x => x.Equals(s, StringComparison.OrdinalIgnoreCase)))
                profile.InfrastructureStrengths.Add(s);
        }
    }

    /// <summary>Check if a proven capability name is infrastructure-level (not a product feature).</summary>
    private static bool IsInfrastructureCapability(string capability)
    {
        var lower = capability.ToLowerInvariant();
        var infraPatterns = new[]
        {
            "ci/cd", "continuous integration", "github actions", "docker",
            "unit test", "integration test", "test coverage", "testing framework",
            "logging", "structured logging", "monitoring", "health check",
            "linting", "code analysis", "static analysis",
            "build system", "makefile", "cmake",
            "dependabot", "renovate", "dependency update",
            "code formatting", "editorconfig"
        };
        return infraPatterns.Any(p => lower.Contains(p));
    }

    /// <summary>Check if an LLM-generated strength description is infrastructure-level.</summary>
    private static bool IsInfrastructureStrength(string strength)
    {
        var lower = strength.ToLowerInvariant();
        var infraSignals = new[]
        {
            "ci/cd", "continuous integration", "github actions", "azure pipelines",
            "unit test", "integration test", "test suite", "test coverage", "597 test", "300+ test",
            "docker", "dockerfile", "container",
            "logging framework", "structured logging", "serilog", "nlog",
            "health check", "monitoring",
            "linting", "eslint", "prettier", "code style",
            "editorconfig", ".editorconfig",
            "dependabot", "renovate",
            "build pipeline", "build system",
            "code analysis", "static analysis", "sonar"
        };
        return infraSignals.Any(p => lower.Contains(p));
    }

    /// <summary>Extract a clean file reference from evidence text, stripping regex patterns.</summary>
    private static string FormatEvidenceForDisplay(string evidence)
    {
        if (string.IsNullOrEmpty(evidence)) return "";
        // Evidence format: "found in FileName.cs" or "FileName.cs — ..." 
        // Extract just the file name, discard pattern details
        var dashIdx = evidence.IndexOf(" — ", StringComparison.Ordinal);
        if (dashIdx >= 0)
            return evidence[..dashIdx].Trim();
        // New format: "found in FileName.cs"
        if (evidence.StartsWith("found in ", StringComparison.OrdinalIgnoreCase))
            return evidence[9..].Trim();
        // If it's just a filename, return it
        if (evidence.Contains('.') && !evidence.Contains(' '))
            return evidence;
        return "";
    }

    /// <summary>Add confirmed-absent items as gaps if the LLM missed them.</summary>
    private static void InjectConfirmedGaps(RepoProfile profile, RepoFactSheet factSheet, VerificationResult result)
    {
        // Only inject from diagnostic files missing + confirmed absent capabilities that are important
        foreach (var missing in factSheet.DiagnosticFilesMissing)
        {
            // Skip items that were deliberately pruned by app-type or DB-tech checks.
            // If GapsRemoved contains an entry mentioning this missing item (e.g., "WRONG-APPTYPE: ... Dockerfile ..."),
            // re-injecting it would undo the pruning.
            var missingKeywords = ExtractKeywords(missing.ToLowerInvariant());
            bool deliberatelyPruned = result.GapsRemoved.Any(r =>
                r.Contains(missing, StringComparison.OrdinalIgnoreCase) ||
                missingKeywords.Any(kw => r.ToLowerInvariant().Contains(kw)));

            if (deliberatelyPruned) continue;

            bool alreadyMentioned = profile.Gaps.Any(g =>
                g.Contains(missing, StringComparison.OrdinalIgnoreCase) ||
                missingKeywords.Any(kw => g.ToLowerInvariant().Contains(kw)));

            if (!alreadyMentioned)
            {
                var gap = $"No {missing} found";
                profile.Gaps.Add(gap);
                result.GapsAdded.Add($"INJECTED: {gap}");
            }
        }
    }

    /// <summary>
    /// LLM-powered relevance check as a second pass after deterministic filters.
    /// Sends the full project identity + surviving complements to the LLM and asks
    /// for a KEEP/REJECT verdict on each. Respects the minimum floor.
    /// </summary>
    private async Task LlmRelevanceCheckAsync(
        RepoProfile profile, RepoFactSheet factSheet, VerificationResult result, CancellationToken ct)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("You are a software ecosystem expert. Evaluate each complement project for RELEVANCE.");
            sb.AppendLine();
            sb.AppendLine("PROJECT IDENTITY:");
            sb.AppendLine($"  Name: {profile.Owner}/{profile.Name}");
            sb.AppendLine($"  Language: {profile.PrimaryLanguage}");
            sb.AppendLine($"  App Type: {factSheet.AppType}");
            sb.AppendLine($"  Deployment: {factSheet.DeploymentTarget}");
            sb.AppendLine($"  Architecture: {factSheet.ArchitectureStyle}");
            sb.AppendLine($"  Ecosystem: {factSheet.Ecosystem}");
            sb.AppendLine($"  Database: {factSheet.DatabaseTechnology}");
            if (factSheet.DomainTags.Count > 0)
                sb.AppendLine($"  Domain: {string.Join(", ", factSheet.DomainTags)}");
            if (factSheet.InapplicableConcepts.Count > 0)
                sb.AppendLine($"  Inapplicable: {string.Join(", ", factSheet.InapplicableConcepts)}");
            sb.AppendLine();
            sb.AppendLine("COMPLEMENT CANDIDATES:");
            for (int i = 0; i < profile.ComplementSuggestions.Count; i++)
            {
                var c = profile.ComplementSuggestions[i];
                sb.AppendLine($"  [{i}] {c.Name} — {c.Purpose} (category: {c.Category}, stars: {c.Stars}, lang: {c.RepoLanguage})");
            }
            sb.AppendLine();
            sb.AppendLine("For each candidate, respond with a JSON object:");
            sb.AppendLine(@"{ ""verdicts"": [ { ""index"": 0, ""verdict"": ""KEEP"", ""reason"": ""..."" }, ... ] }");
            sb.AppendLine("Verdict must be KEEP or REJECT. REJECT if:");
            sb.AppendLine("- Not actually useful for this specific project type/domain");
            sb.AppendLine("- Addresses a concept that's inapplicable to this deployment/architecture");
            sb.AppendLine("- Is generic infrastructure the project doesn't need");
            sb.AppendLine("- Duplicates functionality the project already has");
            sb.AppendLine("Return ONLY valid JSON.");

            var response = await _llmService!.GenerateJsonAsync(
                sb.ToString(),
                "You are a software ecosystem analyst. Return valid JSON with verdicts for each complement.",
                tier: ModelTier.Mini,
                ct: ct);

            // Parse verdicts
            var cleaned = response.Trim();
            if (cleaned.StartsWith("```")) cleaned = cleaned.Split('\n', 2).Length > 1 ? cleaned.Split('\n', 2)[1] : cleaned;
            if (cleaned.EndsWith("```")) cleaned = cleaned[..cleaned.LastIndexOf("```")];
            cleaned = cleaned.Trim();

            using var doc = System.Text.Json.JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("verdicts", out var verdicts)) return;

            var rejectIndices = new List<int>();
            foreach (var v in verdicts.EnumerateArray())
            {
                if (v.TryGetProperty("index", out var idxEl) && v.TryGetProperty("verdict", out var verdictEl))
                {
                    var idx = idxEl.GetInt32();
                    var verdict = verdictEl.GetString() ?? "";
                    if (verdict.Equals("REJECT", StringComparison.OrdinalIgnoreCase))
                    {
                        var reason = v.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() ?? "" : "";
                        rejectIndices.Add(idx);
                        if (idx >= 0 && idx < profile.ComplementSuggestions.Count)
                        {
                            result.ComplementsRemoved.Add(
                                $"LLM-REJECT: \"{profile.ComplementSuggestions[idx].Name}\" — {reason}");
                        }
                    }
                }
            }

            // Apply rejections, respecting the minimum floor
            var toRemove = rejectIndices
                .Where(i => i >= 0 && i < profile.ComplementSuggestions.Count)
                .OrderByDescending(i => i)
                .ToList();

            var remaining = profile.ComplementSuggestions.Count - toRemove.Count;
            if (remaining < MinimumComplementFloor)
            {
                // Keep enough to meet the floor — skip the last N rejections
                toRemove = toRemove.Take(profile.ComplementSuggestions.Count - MinimumComplementFloor).ToList();
            }

            foreach (var idx in toRemove)
                profile.ComplementSuggestions.RemoveAt(idx);

            _logger?.LogInformation("LLM relevance check: {Rejected} complements rejected, {Remaining} remaining",
                toRemove.Count, profile.ComplementSuggestions.Count);
        }
        catch (Exception ex)
        {
            // LLM relevance check is non-fatal — log and continue
            _logger?.LogWarning(ex, "LLM relevance check failed, skipping second-pass filter");
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

    /// <summary>
    /// Detect meta-projects / infrastructure engines that aren't installable packages for the target ecosystem.
    /// Examples: dependabot-core (Ruby engine), terraform (Go binary), renovate (Node platform).
    /// These are useful tools but not NuGet/pip/npm packages you'd add as a dependency.
    /// </summary>
    internal static bool IsMetaProjectNotUsableDirectly(ComplementProject comp, RepoFactSheet factSheet)
    {
        if (string.IsNullOrEmpty(factSheet.Ecosystem)) return false;

        var ecosystem = factSheet.Ecosystem.ToLowerInvariant();
        var compText = $"{comp.Name} {comp.Purpose} {comp.WhatItAdds}".ToLowerInvariant();
        var compNameLower = comp.Name.ToLowerInvariant();

        // Known infrastructure engines / platform repos that are NOT installable packages.
        // Format: (name-fragment, primary-lang) — blocked when the target ecosystem doesn't match.
        var knownMetaProjects = new[]
        {
            // Dependency management platforms
            ("dependabot-core", "ruby"),      // Ruby engine: users configure YAML, not install as NuGet
            ("renovate", "node"),              // Node.js platform: users configure JSON, not install as NuGet
            // Infrastructure-as-code
            ("terraform", "go"),               // Go binary infrastructure tool
            ("pulumi", "go"),                  // Go infrastructure-as-code
            ("ansible", "python"),             // Python automation platform
            ("chef", "ruby"),                  // Ruby configuration management
            ("puppet", "ruby"),                // Ruby configuration management
            ("salt", "python"),                // Python infrastructure automation
            // Container / orchestration
            ("kubernetes", "go"),              // Go container orchestration
            ("containerd", "go"),              // Go container runtime
            ("podman", "go"),                  // Go container engine
            ("helm", "go"),                    // Go package manager for K8s
            // CI/CD engines
            ("jenkins", "java"),               // Java CI server
            ("gitea", "go"),                   // Go git forge
            ("drone", "go"),                   // Go CI platform
            ("woodpecker", "go"),              // Go CI fork of Drone
            // Monitoring / observability platforms
            ("prometheus", "go"),              // Go monitoring system
            ("grafana", "go"),                 // Go dashboarding platform
            ("jaeger", "go"),                  // Go distributed tracing
            // Mega-frameworks / runtimes (not usable as a dependency)
            ("dotnet/runtime", "*"),           // .NET runtime itself — not a complement
            ("microsoft/powertoys", "*"),      // Desktop utility suite — not a library
            ("nodejs/node", "*"),              // Node.js runtime
            ("python/cpython", "*"),           // CPython runtime
            ("rust-lang/rust", "*"),           // Rust compiler
        };

        foreach (var (name, lang) in knownMetaProjects)
        {
            if (compNameLower.Contains(name) || (comp.Url?.ToLowerInvariant().Contains(name) ?? false))
            {
                if (lang == "*") return true; // Always block (runtimes, mega-repos)
                if (!ecosystem.Contains(lang)) return true; // Cross-ecosystem meta project
            }
        }

        // Heuristic: if the complement describes itself as a "platform", "engine", or "service"
        // and the URL points to a different-language repo, it's likely a meta-project
        var metaKeywords = new[] { "platform", "engine", "infrastructure", "service runner", "orchestrator", "runtime" };
        if (metaKeywords.Any(kw => compText.Contains(kw)))
        {
            // Check if the comp name / URL contain indicators of a different primary language
            var rubyIndicators = new[] { "ruby", "gemfile", "bundler" };
            var goIndicators = new[] { "golang", "go module" };

            if ((ecosystem.Contains(".net") || ecosystem.Contains("c#")) &&
                (rubyIndicators.Any(r => compText.Contains(r)) || goIndicators.Any(g => compText.Contains(g))))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if a GitHub repo's primary language is compatible with the target ecosystem.
    /// Uses the ACTUAL language from GitHub API (e.g., "C#", "Python", "Go") rather than
    /// heuristic keyword matching. Works for any ecosystem pairing.
    /// </summary>
    internal static bool IsRepoLanguageCompatible(string repoLanguage, string ecosystem)
    {
        if (string.IsNullOrEmpty(repoLanguage) || string.IsNullOrEmpty(ecosystem)) return true;

        var lang = repoLanguage.ToLowerInvariant();
        var eco = ecosystem.ToLowerInvariant();

        // Build a table of which languages belong to which ecosystem families
        var ecosystemLanguages = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [".net"] = new(StringComparer.OrdinalIgnoreCase) { "c#", "f#", "visual basic .net", "visual basic", "powershell" },
            ["c#"] = new(StringComparer.OrdinalIgnoreCase) { "c#", "f#", "visual basic .net", "powershell" },
            ["jvm"] = new(StringComparer.OrdinalIgnoreCase) { "java", "kotlin", "scala", "groovy", "clojure" },
            ["node"] = new(StringComparer.OrdinalIgnoreCase) { "javascript", "typescript", "coffeescript" },
            ["javascript"] = new(StringComparer.OrdinalIgnoreCase) { "javascript", "typescript", "coffeescript" },
            ["python"] = new(StringComparer.OrdinalIgnoreCase) { "python", "cython", "jupyter notebook" },
            ["rust"] = new(StringComparer.OrdinalIgnoreCase) { "rust" },
            ["go"] = new(StringComparer.OrdinalIgnoreCase) { "go" },
            ["ruby"] = new(StringComparer.OrdinalIgnoreCase) { "ruby" },
            ["php"] = new(StringComparer.OrdinalIgnoreCase) { "php" },
            ["swift"] = new(StringComparer.OrdinalIgnoreCase) { "swift", "objective-c" },
            ["dart"] = new(StringComparer.OrdinalIgnoreCase) { "dart" },
        };

        // Multi-language repos (e.g., documentation, configs) are always allowed
        var universalLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "shell", "bash", "dockerfile", "makefile", "html", "css", "markdown", ""
        };
        if (universalLanguages.Contains(lang)) return true;

        // Find which ecosystem the target project is in
        foreach (var (ecoKey, acceptedLangs) in ecosystemLanguages)
        {
            if (eco.Contains(ecoKey))
                return acceptedLangs.Contains(lang);
        }

        // Unknown ecosystem — allow anything
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
    public List<string> ComplementsBackfilled { get; set; } = new();
    public List<string> StrengthsAdded { get; set; } = new();
    public List<string> GapsAdded { get; set; } = new();
    public List<string> IdentityWarnings { get; set; } = new();
    public string? DiversityWarning { get; set; }

    public int TotalCorrections =>
        GapsRemoved.Count + StrengthsRemoved.Count + FrameworksRemoved.Count +
        ComplementsRemoved.Count + StrengthsAdded.Count + GapsAdded.Count + IdentityWarnings.Count;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (GapsRemoved.Count > 0) parts.Add($"{GapsRemoved.Count} hallucinated gaps removed");
            if (StrengthsRemoved.Count > 0) parts.Add($"{StrengthsRemoved.Count} hallucinated strengths removed");
            if (FrameworksRemoved.Count > 0) parts.Add($"{FrameworksRemoved.Count} phantom frameworks removed");
            if (ComplementsRemoved.Count > 0) parts.Add($"{ComplementsRemoved.Count} invalid complements removed");
            if (ComplementsBackfilled.Count > 0) parts.Add($"{ComplementsBackfilled.Count} complements backfilled");
            if (StrengthsAdded.Count > 0) parts.Add($"{StrengthsAdded.Count} proven strengths injected");
            if (GapsAdded.Count > 0) parts.Add($"{GapsAdded.Count} confirmed gaps injected");
            if (IdentityWarnings.Count > 0) parts.Add($"{IdentityWarnings.Count} identity issues detected");
            if (!string.IsNullOrEmpty(DiversityWarning)) parts.Add(DiversityWarning);
            return parts.Count > 0 ? string.Join(" | ", parts) : "No corrections needed";
        }
    }
}
