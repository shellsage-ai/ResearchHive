using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace ResearchHive.Core.Services;

/// <summary>
/// Post-generation verifier for fusion output. Validates that the 10 independently-generated
/// fusion sections don't hallucinate technologies, misattribute capabilities, or fabricate
/// gap resolutions. Runs both deterministic checks and one LLM validation pass.
/// </summary>
public class FusionPostVerifier
{
    private readonly ILlmService? _llmService;
    private readonly ILogger<FusionPostVerifier>? _logger;

    public FusionPostVerifier(ILogger<FusionPostVerifier>? logger = null, ILlmService? llmService = null)
    {
        _logger = logger;
        _llmService = llmService;
    }

    /// <summary>
    /// Verify and correct fusion section outputs against the original input profiles.
    /// Returns corrected sections + a verification result summary.
    /// </summary>
    public async Task<FusionVerificationResult> VerifyAsync(
        Dictionary<string, string> sectionResults,
        IReadOnlyList<RepoProfile> inputProfiles,
        CancellationToken ct = default)
    {
        var result = new FusionVerificationResult();

        // Build a unified vocabulary of valid technologies, features, and capabilities per project
        var projectVocabs = inputProfiles.Select(BuildProjectVocabulary).ToList();

        // 1. Validate TECH_STACK table — remove rows citing technologies not in any input
        if (sectionResults.TryGetValue("TECH_STACK", out var techStack))
        {
            var corrected = ValidateTechStackTable(techStack, projectVocabs, result);
            sectionResults["TECH_STACK"] = corrected;
        }

        // 2. Validate FEATURE_MATRIX — check attributions
        if (sectionResults.TryGetValue("FEATURE_MATRIX", out var featureMatrix))
        {
            var corrected = ValidateFeatureMatrix(featureMatrix, projectVocabs, result);
            sectionResults["FEATURE_MATRIX"] = corrected;
        }

        // 3. Validate GAPS_CLOSED — verify gap exists in claimed project, fix exists in other
        if (sectionResults.TryGetValue("GAPS_CLOSED", out var gapsClosed))
        {
            var corrected = ValidateGapsClosed(gapsClosed, inputProfiles, result);
            sectionResults["GAPS_CLOSED"] = corrected;
        }

        // 4. Validate PROVENANCE — verify decisions map to real features
        if (sectionResults.TryGetValue("PROVENANCE", out var provenance))
        {
            var corrected = ValidateProvenance(provenance, projectVocabs, result);
            sectionResults["PROVENANCE"] = corrected;
        }

        // 5. LLM-based validation of free-form prose sections (UNIFIED_VISION + ARCHITECTURE)
        if (_llmService != null)
        {
            await ValidateProseAsync(sectionResults, inputProfiles, result, ct);
        }

        _logger?.LogInformation(
            "FusionPostVerifier: {TechRemoved} tech rows removed, {FeaturesFixed} features corrected, " +
            "{GapsFixed} gap claims corrected, {ProvenanceFixed} provenance fixed, {ProseCorrections} prose corrections",
            result.TechStackRowsRemoved.Count, result.FeaturesCorrected.Count,
            result.GapsClosedCorrected.Count, result.ProvenanceCorrected.Count, result.ProseCorrections.Count);

        return result;
    }

    // ─────────────── Vocabulary Builder ───────────────

    /// <summary>
    /// Build a vocabulary of all valid terms for a project — technologies, features, capabilities.
    /// Used for deterministic validation against LLM-generated content.
    /// </summary>
    internal static ProjectVocabulary BuildProjectVocabulary(RepoProfile profile)
    {
        var vocab = new ProjectVocabulary
        {
            ProjectName = $"{profile.Owner}/{profile.Name}",
            ProjectNameShort = profile.Name,
        };

        // Technologies = dependencies + frameworks + languages
        foreach (var dep in profile.Dependencies)
            vocab.Technologies.Add(dep.Name.ToLowerInvariant());
        foreach (var fw in profile.Frameworks)
            vocab.Technologies.Add(fw.ToLowerInvariant());
        foreach (var lang in profile.Languages)
            vocab.Technologies.Add(lang.ToLowerInvariant());

        // Features = strengths + core capabilities + infrastructure strengths
        foreach (var s in profile.Strengths)
            vocab.Features.Add(s.ToLowerInvariant());
        foreach (var s in profile.InfrastructureStrengths)
            vocab.Features.Add(s.ToLowerInvariant());
        foreach (var c in profile.CoreCapabilities)
            vocab.Features.Add(c.ToLowerInvariant());

        // Gaps
        foreach (var g in profile.Gaps)
            vocab.Gaps.Add(g.ToLowerInvariant());

        return vocab;
    }

    // ─────────────── TECH_STACK Validation ───────────────

    /// <summary>
    /// Parse the TECH_STACK markdown table and remove rows that cite technologies
    /// not present in any input profile's dependencies, frameworks, or languages.
    /// </summary>
    internal static string ValidateTechStackTable(string techStackSection, IReadOnlyList<ProjectVocabulary> vocabs, FusionVerificationResult result)
    {
        var lines = techStackSection.Split('\n');
        var validLines = new List<string>();

        foreach (var line in lines)
        {
            // Keep non-table-row lines (headers, separators, prose)
            if (!line.TrimStart().StartsWith('|') || line.Contains("---") || IsTableHeader(line))
            {
                validLines.Add(line);
                continue;
            }

            // Parse table row: | Technology | Purpose | Source |
            var cells = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (cells.Length < 2)
            {
                validLines.Add(line);
                continue;
            }

            var techName = cells[0].Trim().ToLowerInvariant()
                .Replace("**", "").Replace("*", "").Trim();

            // Check if this technology appears in ANY input profile's vocabulary
            bool found = vocabs.Any(v => v.Technologies.Any(t =>
                t.Contains(techName) || techName.Contains(t) ||
                FuzzyTechMatch(techName, t)));

            if (found)
            {
                validLines.Add(line);
            }
            else
            {
                result.TechStackRowsRemoved.Add($"FABRICATED: '{cells[0].Trim()}' — not found in any input profile's dependencies/frameworks/languages");
            }
        }

        return string.Join('\n', validLines);
    }

    /// <summary>Fuzzy match for technology names (e.g., "community toolkit mvvm" matches "CommunityToolkit.Mvvm").</summary>
    private static bool FuzzyTechMatch(string a, string b)
    {
        // Normalize: remove dots, dashes, underscores, lowercase
        var normA = Regex.Replace(a, @"[\.\-_\s]+", "");
        var normB = Regex.Replace(b, @"[\.\-_\s]+", "");
        return normA.Contains(normB) || normB.Contains(normA);
    }

    private static bool IsTableHeader(string line)
    {
        var lower = line.ToLowerInvariant();
        return lower.Contains("technology") || lower.Contains("purpose") || lower.Contains("source") ||
               lower.Contains("feature") || lower.Contains("decision");
    }

    // ─────────────── FEATURE_MATRIX Validation ───────────────

    /// <summary>
    /// Validate feature attributions. For each "FEATURE: X | SOURCE: Y", verify that
    /// feature X relates to a capability of project Y.
    /// </summary>
    internal static string ValidateFeatureMatrix(string featureSection, IReadOnlyList<ProjectVocabulary> vocabs, FusionVerificationResult result)
    {
        var lines = featureSection.Split('\n');
        var validLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // 1. Handle markdown table format FIRST: | Feature | Source |
            //    (Must come before the regex check, because table rows also contain '|')
            if (trimmed.StartsWith('|'))
            {
                var cells = trimmed.Split('|', StringSplitOptions.RemoveEmptyEntries);
                if (cells.Length >= 2 && !IsTableHeader(trimmed) && !trimmed.Contains("---"))
                {
                    var feature = cells[0].Trim().Replace("**", "").ToLowerInvariant();
                    var source = cells[cells.Length - 1].Trim().ToLowerInvariant();

                    // Verify: does this source project actually have this feature?
                    var sourceVocab = vocabs.FirstOrDefault(v =>
                        source.Contains(v.ProjectName.ToLowerInvariant()) ||
                        source.Contains(v.ProjectNameShort.ToLowerInvariant()));

                    if (sourceVocab != null)
                    {
                        bool hasFeature = sourceVocab.Features.Any(f =>
                            FeatureOverlap(feature, f));

                        if (!hasFeature)
                        {
                            // Check if ANOTHER project has this feature
                            var correctSource = vocabs.FirstOrDefault(v =>
                                v.Features.Any(f => FeatureOverlap(feature, f)));

                            if (correctSource != null && correctSource != sourceVocab)
                            {
                                // Re-attribute to correct source
                                var correctedLine = line.Replace(cells[cells.Length - 1].Trim(), correctSource.ProjectName);
                                validLines.Add(correctedLine);
                                result.FeaturesCorrected.Add(
                                    $"RE-ATTRIBUTED: '{cells[0].Trim()}' from {sourceVocab.ProjectName} → {correctSource.ProjectName}");
                                continue;
                            }
                        }
                    }
                }
                validLines.Add(line);
                continue;
            }

            // 2. Parse non-table "FEATURE: X | SOURCE: Y" format
            var featureMatch = Regex.Match(trimmed, @"(?:FEATURE:\s*)?(.+?)\s*\|\s*(?:SOURCE:\s*)?(.+)", RegexOptions.IgnoreCase);
            if (!featureMatch.Success)
            {
                validLines.Add(line);
                continue;
            }

            // Non-table FEATURE: X | SOURCE: Y format
            validLines.Add(line);
        }

        return string.Join('\n', validLines);
    }

    /// <summary>Check if a feature description overlaps with a known capability.</summary>
    private static bool FeatureOverlap(string feature, string capability)
    {
        // Extract significant words (3+ chars) from both
        var featureWords = Regex.Split(feature, @"[\s/\(\)\-,.:]+")
            .Where(w => w.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var capWords = Regex.Split(capability, @"[\s/\(\)\-,.:]+")
            .Where(w => w.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Require at least 2 shared significant words (or 1 if the feature is short)
        var shared = featureWords.Intersect(capWords, StringComparer.OrdinalIgnoreCase).Count();
        return featureWords.Count <= 2 ? shared >= 1 : shared >= 2;
    }

    // ─────────────── GAPS_CLOSED Validation ───────────────

    /// <summary>
    /// Validate gap closure claims. For each claim "Project A's gap resolved by Project B",
    /// verify (a) gap exists in A's gaps, (b) resolving capability exists in B's features.
    /// Remove fabricated entries.
    /// </summary>
    internal static string ValidateGapsClosed(string gapsClosedSection, IReadOnlyList<RepoProfile> profiles, FusionVerificationResult result)
    {
        var lines = gapsClosedSection.Split('\n');
        var validLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Skip non-bullet lines
            if (!trimmed.StartsWith('-') && !trimmed.StartsWith('✅') && !trimmed.StartsWith('*'))
            {
                // Keep table rows, headers, prose
                if (trimmed.StartsWith('|') && !trimmed.Contains("---") && !IsTableHeader(trimmed))
                {
                    // Table format: | Gap | Project with gap | Resolved by |
                    var cells = trimmed.Split('|', StringSplitOptions.RemoveEmptyEntries);
                    if (cells.Length >= 3)
                    {
                        var gapText = cells[0].Trim().ToLowerInvariant();
                        var gapProject = cells[1].Trim().ToLowerInvariant();
                        var resolvedBy = cells[2].Trim().ToLowerInvariant();

                        // Verify the resolving project has the claimed capability
                        var resolver = profiles.FirstOrDefault(p =>
                            resolvedBy.Contains(p.Name.ToLowerInvariant()) ||
                            resolvedBy.Contains($"{p.Owner}/{p.Name}".ToLowerInvariant()));

                        if (resolver != null)
                        {
                            bool hasCapability = resolver.Strengths.Concat(resolver.InfrastructureStrengths)
                                .Concat(resolver.CoreCapabilities)
                                .Any(s => FeatureOverlapSimple(gapText, s.ToLowerInvariant()));

                            if (!hasCapability)
                            {
                                result.GapsClosedCorrected.Add(
                                    $"FABRICATED: Claimed '{resolver.Owner}/{resolver.Name}' resolves '{cells[0].Trim()}' but no matching capability found");
                                continue; // Remove this row
                            }
                        }
                    }
                }
                validLines.Add(line);
                continue;
            }

            // For bullet-point items, do a lightweight check: if the line mentions
            // a resolving project/capability, verify it exists in that project's vocabulary
            var bulletContent = trimmed.TrimStart('-', '*', '•', ' ');

            // Try to extract "resolved by <project>/<capability>" or "→ resolved by" patterns
            var arrowMatch = Regex.Match(bulletContent, @"→\s*(?:resolved\s+by\s+)?(.+)", RegexOptions.IgnoreCase);
            if (arrowMatch.Success)
            {
                var resolutionText = arrowMatch.Groups[1].Value.Trim().ToLowerInvariant();
                // Try to find which profile this resolution references
                bool anyProfileMatches = false;
                foreach (var p in profiles)
                {
                    var nameLower = p.Name.ToLowerInvariant();
                    var fullName = $"{p.Owner}/{p.Name}".ToLowerInvariant();
                    if (resolutionText.Contains(nameLower) || resolutionText.Contains(fullName))
                    {
                        // Verify the claimed capability exists
                        bool hasCapability = p.Strengths
                            .Concat(p.InfrastructureStrengths)
                            .Concat(p.CoreCapabilities)
                            .Any(s => FeatureOverlapSimple(resolutionText, s.ToLowerInvariant()));
                        if (!hasCapability)
                        {
                            result.GapsClosedCorrected.Add(
                                $"FABRICATED: Bullet claims resolution via '{p.Owner}/{p.Name}' but no matching capability: {bulletContent}");
                            goto nextLine; // Skip this line
                        }
                        anyProfileMatches = true;
                        break;
                    }
                }
                // If no profile matched at all, it might be hallucinated
                if (!anyProfileMatches && profiles.Count > 0)
                {
                    result.GapsClosedCorrected.Add(
                        $"UNVERIFIABLE: Bullet references unknown project: {bulletContent}");
                    goto nextLine;
                }
            }

            validLines.Add(line);
            nextLine:;
        }

        return string.Join('\n', validLines);
    }

    /// <summary>Simple overlap: check if any 3+ char word from the gap appears in the capability.</summary>
    private static bool FeatureOverlapSimple(string gapText, string capability)
    {
        var gapWords = Regex.Split(gapText, @"[\s/\(\)\-,.:]+")
            .Where(w => w.Length >= 4)
            .ToList();
        return gapWords.Count > 0 && gapWords.Any(w => capability.Contains(w));
    }

    // ─────────────── PROVENANCE Validation ───────────────

    /// <summary>
    /// Validate provenance entries. Each "DECISION: X | FROM: Y" should reference
    /// a real project from the inputs.
    /// </summary>
    internal static string ValidateProvenance(string provenanceSection, IReadOnlyList<ProjectVocabulary> vocabs, FusionVerificationResult result)
    {
        var lines = provenanceSection.Split('\n');
        var validLines = new List<string>();
        var projectNames = vocabs.SelectMany(v => new[] { v.ProjectName.ToLowerInvariant(), v.ProjectNameShort.ToLowerInvariant() }).ToHashSet();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Check DECISION: X | FROM: Y format
            var match = Regex.Match(trimmed, @"(?:DECISION:\s*)?(.+?)\s*\|\s*(?:FROM:\s*)?(.*)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var fromText = match.Groups[2].Value.Trim().ToLowerInvariant();
                // Verify at least one project name is referenced
                bool referencesProject = projectNames.Any(p => fromText.Contains(p));
                if (!referencesProject && fromText.Length > 2)
                {
                    result.ProvenanceCorrected.Add($"ORPHANED: Decision '{match.Groups[1].Value.Trim()}' references unknown source '{match.Groups[2].Value.Trim()}'");
                    continue; // Remove orphaned provenance
                }
            }

            validLines.Add(line);
        }

        return string.Join('\n', validLines);
    }

    // ─────────────── Prose Validation (LLM-based) ───────────────

    /// <summary>
    /// Use an LLM to fact-check the UNIFIED_VISION and ARCHITECTURE sections
    /// against the input project identities. Catches misattributed capabilities.
    /// </summary>
    private async Task ValidateProseAsync(
        Dictionary<string, string> sectionResults,
        IReadOnlyList<RepoProfile> inputProfiles,
        FusionVerificationResult result,
        CancellationToken ct)
    {
        // Only validate if both sections have content
        var proseToCheck = new StringBuilder();
        if (sectionResults.TryGetValue("UNIFIED_VISION", out var vision) && vision.Length > 50)
            proseToCheck.AppendLine($"## UNIFIED_VISION:\n{vision}");
        if (sectionResults.TryGetValue("ARCHITECTURE", out var arch) && arch.Length > 50)
            proseToCheck.AppendLine($"\n## ARCHITECTURE:\n{arch}");

        if (proseToCheck.Length < 100) return;

        // Build compact identity cards
        var identityCards = new StringBuilder();
        foreach (var p in inputProfiles)
        {
            identityCards.AppendLine($"### {p.Owner}/{p.Name}");
            identityCards.AppendLine($"Description: {p.Description}");
            identityCards.AppendLine($"Summary: {p.ProjectSummary}");
            identityCards.AppendLine($"Language: {p.PrimaryLanguage}");
            identityCards.AppendLine($"Strengths: {string.Join(", ", p.Strengths.Take(8))}");
            identityCards.AppendLine($"Frameworks: {string.Join(", ", p.Frameworks)}");
            identityCards.AppendLine();
        }

        var prompt = $@"You are a fact-checker reviewing a fusion analysis for accuracy.

## Project Identity Cards (ground truth):
{identityCards}

## Fusion Sections to Review:
{proseToCheck}

Find ANY statement that:
1. Attributes Project A's capability/class/service to Project B
2. Invents a capability that NEITHER project has
3. Claims a project uses a technology not in its dependencies/frameworks

For each error, output EXACTLY:
ERROR: ""<quoted erroneous text>"" → CORRECTION: <what it should say>

If no errors found, respond with: NO_ERRORS";

        try
        {
            var response = await _llmService!.GenerateAsync(prompt,
                "You are a strict fact-checker. Flag ONLY genuinely wrong attributions or fabricated capabilities. Ignore style issues.",
                maxTokens: 1000, tier: ModelTier.Mini, ct: ct);

            if (string.IsNullOrWhiteSpace(response) || response.Contains("NO_ERRORS"))
                return;

            // Parse errors and apply corrections
            var errorLines = response.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var errorLine in errorLines)
            {
                var errorMatch = Regex.Match(errorLine, @"ERROR:\s*""?(.+?)""?\s*→\s*CORRECTION:\s*(.+)");
                if (!errorMatch.Success) continue;

                var errorText = errorMatch.Groups[1].Value.Trim();
                var correction = errorMatch.Groups[2].Value.Trim();

                result.ProseCorrections.Add($"PROSE: \"{errorText}\" → \"{correction}\"");

                // Apply correction to the relevant sections
                foreach (var sectionName in new[] { "UNIFIED_VISION", "ARCHITECTURE" })
                {
                    if (sectionResults.TryGetValue(sectionName, out var sectionText) &&
                        sectionText.Contains(errorText, StringComparison.OrdinalIgnoreCase))
                    {
                        sectionResults[sectionName] = sectionText.Replace(errorText, correction);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Fusion prose validation failed — prose unverified");
        }
    }
}

// ─────────────── Supporting types ───────────────

/// <summary>Vocabulary of valid terms for a single project — used for deterministic validation.</summary>
public class ProjectVocabulary
{
    public string ProjectName { get; set; } = "";
    public string ProjectNameShort { get; set; } = "";
    public HashSet<string> Technologies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Features { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Gaps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Summary of all corrections made by the fusion post-verifier.</summary>
public class FusionVerificationResult
{
    public List<string> TechStackRowsRemoved { get; set; } = new();
    public List<string> FeaturesCorrected { get; set; } = new();
    public List<string> GapsClosedCorrected { get; set; } = new();
    public List<string> ProvenanceCorrected { get; set; } = new();
    public List<string> ProseCorrections { get; set; } = new();

    public int TotalCorrections =>
        TechStackRowsRemoved.Count + FeaturesCorrected.Count +
        GapsClosedCorrected.Count + ProvenanceCorrected.Count + ProseCorrections.Count;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (TechStackRowsRemoved.Count > 0) parts.Add($"{TechStackRowsRemoved.Count} fabricated tech entries removed");
            if (FeaturesCorrected.Count > 0) parts.Add($"{FeaturesCorrected.Count} features re-attributed");
            if (GapsClosedCorrected.Count > 0) parts.Add($"{GapsClosedCorrected.Count} gap claims corrected");
            if (ProvenanceCorrected.Count > 0) parts.Add($"{ProvenanceCorrected.Count} provenance entries fixed");
            if (ProseCorrections.Count > 0) parts.Add($"{ProseCorrections.Count} prose errors corrected");
            return parts.Count > 0 ? string.Join(" | ", parts) : "No corrections needed";
        }
    }
}
