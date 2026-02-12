using ResearchHive.Core.Data;
using ResearchHive.Core.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace ResearchHive.Core.Services;

/// <summary>
/// Compares two research jobs side-by-side, producing a structured diff of their
/// findings, evidence, and conclusions. Useful for:
/// - Comparing runs with different settings (e.g. 5 vs 15 sources)
/// - Comparing different prompts on the same topic
/// - Assessing how incremental research improved results
/// </summary>
public class ResearchComparisonService
{
    private readonly SessionManager _sessionManager;

    public ResearchComparisonService(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// Compare two research jobs and produce a structured comparison.
    /// Jobs can be from the same or different sessions.
    /// </summary>
    public ResearchComparison Compare(string sessionIdA, string jobIdA, string sessionIdB, string jobIdB)
    {
        var dbA = _sessionManager.GetSessionDb(sessionIdA);
        var dbB = _sessionManager.GetSessionDb(sessionIdB);

        var jobA = dbA.GetJob(jobIdA);
        var jobB = dbB.GetJob(jobIdB);

        if (jobA == null || jobB == null)
            throw new InvalidOperationException("One or both jobs not found");

        var citationsA = dbA.GetCitations(jobIdA);
        var citationsB = dbB.GetCitations(jobIdB);
        var claimsA = dbA.GetClaimLedger(jobIdA);
        var claimsB = dbB.GetClaimLedger(jobIdB);
        var stepsA = dbA.GetJobSteps(jobIdA);
        var stepsB = dbB.GetJobSteps(jobIdB);

        var comparison = new ResearchComparison
        {
            JobA = jobA,
            JobB = jobB,
            SessionIdA = sessionIdA,
            SessionIdB = sessionIdB,

            // Metrics comparison
            SourceCountA = jobA.AcquiredSourceIds.Count,
            SourceCountB = jobB.AcquiredSourceIds.Count,
            CitationCountA = citationsA.Count,
            CitationCountB = citationsB.Count,
            GroundingA = jobA.GroundingScore,
            GroundingB = jobB.GroundingScore,
            ClaimCountA = claimsA.Count,
            ClaimCountB = claimsB.Count,
            CitedClaimsA = claimsA.Count(c => c.Support == "cited"),
            CitedClaimsB = claimsB.Count(c => c.Support == "cited"),
            StepCountA = stepsA.Count,
            StepCountB = stepsB.Count,

            // Duration
            DurationA = ComputeDuration(jobA),
            DurationB = ComputeDuration(jobB),

            // Content analysis
            SharedTopics = FindSharedTopics(jobA.FullReport ?? "", jobB.FullReport ?? ""),
            UniqueToA = FindUniqueContent(jobA.FullReport ?? "", jobB.FullReport ?? ""),
            UniqueToB = FindUniqueContent(jobB.FullReport ?? "", jobA.FullReport ?? ""),

            // Section-level comparison
            SectionDiffs = CompareSections(jobA.FullReport ?? "", jobB.FullReport ?? "")
        };

        // Generate summary markdown
        comparison.SummaryMarkdown = GenerateSummaryMarkdown(comparison);

        return comparison;
    }

    /// <summary>
    /// Compare two jobs within the same session.
    /// </summary>
    public ResearchComparison CompareInSession(string sessionId, string jobIdA, string jobIdB)
    {
        return Compare(sessionId, jobIdA, sessionId, jobIdB);
    }

    private static TimeSpan ComputeDuration(ResearchJob job)
    {
        if (job.CompletedUtc.HasValue && job.CreatedUtc != default)
            return job.CompletedUtc.Value - job.CreatedUtc;
        return TimeSpan.Zero;
    }

    /// <summary>
    /// Find topics/keywords that appear in both reports.
    /// </summary>
    internal static List<string> FindSharedTopics(string reportA, string reportB)
    {
        var wordsA = ExtractKeyPhrases(reportA);
        var wordsB = new HashSet<string>(ExtractKeyPhrases(reportB), StringComparer.OrdinalIgnoreCase);
        return wordsA.Where(w => wordsB.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
    }

    /// <summary>
    /// Find significant content unique to report A (not in B).
    /// </summary>
    internal static List<string> FindUniqueContent(string reportA, string reportB)
    {
        var wordsA = ExtractKeyPhrases(reportA);
        var wordsB = new HashSet<string>(ExtractKeyPhrases(reportB), StringComparer.OrdinalIgnoreCase);
        return wordsA.Where(w => !wordsB.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase).Take(15).ToList();
    }

    /// <summary>
    /// Compare section headings and estimate content overlap per section.
    /// </summary>
    internal static List<SectionDiff> CompareSections(string reportA, string reportB)
    {
        var sectionsA = ExtractSections(reportA);
        var sectionsB = ExtractSections(reportB);
        var diffs = new List<SectionDiff>();

        var allHeadings = sectionsA.Keys.Union(sectionsB.Keys, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var heading in allHeadings)
        {
            var hasA = sectionsA.TryGetValue(heading, out var contentA);
            var hasB = sectionsB.TryGetValue(heading, out var contentB);

            var diff = new SectionDiff
            {
                Heading = heading,
                InA = hasA,
                InB = hasB,
                WordCountA = hasA ? CountWords(contentA!) : 0,
                WordCountB = hasB ? CountWords(contentB!) : 0,
                ContentOverlap = hasA && hasB ? ComputeOverlap(contentA!, contentB!) : 0
            };

            diffs.Add(diff);
        }

        return diffs;
    }

    private static Dictionary<string, string> ExtractSections(string report)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = Regex.Matches(report, @"^##\s+(.+?)$", RegexOptions.Multiline);

        for (int i = 0; i < matches.Count; i++)
        {
            var heading = matches[i].Groups[1].Value.Trim();
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : report.Length;
            sections[heading] = report[start..end].Trim();
        }

        return sections;
    }

    private static List<string> ExtractKeyPhrases(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();

        // Extract 2-3 word phrases that are likely meaningful
        var words = Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(w => w.Length > 3)
            .ToList();

        var stopWords = new HashSet<string> {
            "the", "and", "that", "this", "with", "from", "have", "been",
            "were", "also", "which", "their", "there", "more", "than",
            "other", "some", "very", "into", "over", "such", "about"
        };

        return words.Where(w => !stopWords.Contains(w)).ToList();
    }

    private static double ComputeOverlap(string a, string b)
    {
        var wordsA = new HashSet<string>(ExtractKeyPhrases(a));
        var wordsB = new HashSet<string>(ExtractKeyPhrases(b));
        if (wordsA.Count == 0 || wordsB.Count == 0) return 0;

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();
        return union > 0 ? (double)intersection / union : 0;
    }

    private static int CountWords(string text) =>
        Regex.Split(text, @"\s+").Count(w => w.Length > 0);

    private static string GenerateSummaryMarkdown(ResearchComparison c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Research Comparison\n");

        sb.AppendLine("## Metrics\n");
        sb.AppendLine("| Metric | Run A | Run B | Delta |");
        sb.AppendLine("|--------|-------|-------|-------|");
        sb.AppendLine($"| Sources | {c.SourceCountA} | {c.SourceCountB} | {c.SourceCountB - c.SourceCountA:+#;-#;0} |");
        sb.AppendLine($"| Citations | {c.CitationCountA} | {c.CitationCountB} | {c.CitationCountB - c.CitationCountA:+#;-#;0} |");
        sb.AppendLine($"| Grounding | {c.GroundingA:P0} | {c.GroundingB:P0} | {c.GroundingB - c.GroundingA:+P0} |");
        sb.AppendLine($"| Claims | {c.ClaimCountA} | {c.ClaimCountB} | {c.ClaimCountB - c.ClaimCountA:+#;-#;0} |");
        sb.AppendLine($"| Cited Claims | {c.CitedClaimsA} | {c.CitedClaimsB} | {c.CitedClaimsB - c.CitedClaimsA:+#;-#;0} |");
        sb.AppendLine($"| Duration | {c.DurationA:mm\\:ss} | {c.DurationB:mm\\:ss} | |");
        sb.AppendLine();

        if (c.SharedTopics.Count > 0)
        {
            sb.AppendLine($"## Shared Topics ({c.SharedTopics.Count})\n");
            sb.AppendLine(string.Join(", ", c.SharedTopics.Take(15)));
            sb.AppendLine();
        }

        if (c.UniqueToA.Count > 0)
        {
            sb.AppendLine($"## Unique to Run A ({c.UniqueToA.Count})\n");
            sb.AppendLine(string.Join(", ", c.UniqueToA.Take(10)));
            sb.AppendLine();
        }

        if (c.UniqueToB.Count > 0)
        {
            sb.AppendLine($"## Unique to Run B ({c.UniqueToB.Count})\n");
            sb.AppendLine(string.Join(", ", c.UniqueToB.Take(10)));
            sb.AppendLine();
        }

        if (c.SectionDiffs.Count > 0)
        {
            sb.AppendLine("## Section Comparison\n");
            sb.AppendLine("| Section | Words A | Words B | Overlap |");
            sb.AppendLine("|---------|---------|---------|---------|");
            foreach (var d in c.SectionDiffs)
            {
                var overlapStr = d.InA && d.InB ? $"{d.ContentOverlap:P0}" : (d.InA ? "A only" : "B only");
                sb.AppendLine($"| {d.Heading} | {d.WordCountA} | {d.WordCountB} | {overlapStr} |");
            }
        }

        return sb.ToString();
    }
}

public class ResearchComparison
{
    public ResearchJob JobA { get; set; } = new();
    public ResearchJob JobB { get; set; } = new();
    public string SessionIdA { get; set; } = "";
    public string SessionIdB { get; set; } = "";

    // Metrics
    public int SourceCountA { get; set; }
    public int SourceCountB { get; set; }
    public int CitationCountA { get; set; }
    public int CitationCountB { get; set; }
    public double GroundingA { get; set; }
    public double GroundingB { get; set; }
    public int ClaimCountA { get; set; }
    public int ClaimCountB { get; set; }
    public int CitedClaimsA { get; set; }
    public int CitedClaimsB { get; set; }
    public int StepCountA { get; set; }
    public int StepCountB { get; set; }
    public TimeSpan DurationA { get; set; }
    public TimeSpan DurationB { get; set; }

    // Content analysis
    public List<string> SharedTopics { get; set; } = new();
    public List<string> UniqueToA { get; set; } = new();
    public List<string> UniqueToB { get; set; } = new();
    public List<SectionDiff> SectionDiffs { get; set; } = new();

    public string SummaryMarkdown { get; set; } = "";
}

public class SectionDiff
{
    public string Heading { get; set; } = "";
    public bool InA { get; set; }
    public bool InB { get; set; }
    public int WordCountA { get; set; }
    public int WordCountB { get; set; }
    public double ContentOverlap { get; set; }
}
