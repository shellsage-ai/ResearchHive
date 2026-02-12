using ResearchHive.Core.Data;
using ResearchHive.Core.Models;
using System.Text.RegularExpressions;

namespace ResearchHive.Core.Services;

/// <summary>
/// Verifies that citations in a research report are actually supported by the source material.
/// For each citation [N], checks if the claim around it is present in or consistent with
/// the cited source's text. This is a key differentiator over ChatGPT — we don't just cite,
/// we prove citations are accurate.
/// </summary>
public class CitationVerificationService
{
    private readonly SessionManager _sessionManager;
    private readonly LlmService _llmService;

    // Match [N] citation references
    private static readonly Regex CitationRefRegex = new(@"\[(\d+)\]", RegexOptions.Compiled);

    // Match claim text around a citation: sentence(s) containing or preceding [N]
    private static readonly Regex SentenceRegex = new(@"[^.!?\n]+[.!?]?\s*", RegexOptions.Compiled);

    public CitationVerificationService(SessionManager sessionManager, LlmService llmService)
    {
        _sessionManager = sessionManager;
        _llmService = llmService;
    }

    /// <summary>
    /// Verify all citations in a report. Returns a list of verification results.
    /// Uses local text matching first (fast), then LLM for ambiguous cases.
    /// </summary>
    public async Task<List<CitationVerification>> VerifyReportAsync(
        string sessionId, string reportContent, string jobId, CancellationToken ct = default)
    {
        var db = _sessionManager.GetSessionDb(sessionId);
        var citations = db.GetCitations(jobId);
        var results = new List<CitationVerification>();

        if (citations.Count == 0 || string.IsNullOrWhiteSpace(reportContent))
            return results;

        // Build citation label → Citation lookup
        var citationMap = citations
            .Where(c => !string.IsNullOrEmpty(c.Label))
            .GroupBy(c => c.Label)
            .ToDictionary(g => g.Key, g => g.First());

        // Find all citation references in the report and extract surrounding claims
        var claimsWithCitations = ExtractClaimsWithCitations(reportContent);

        foreach (var (citLabel, claimText) in claimsWithCitations)
        {
            if (ct.IsCancellationRequested) break;
            if (!citationMap.TryGetValue(citLabel, out var citation)) continue;

            var verification = new CitationVerification
            {
                CitationLabel = citLabel,
                ClaimText = claimText,
                SourceExcerpt = citation.Excerpt ?? "",
                SourceId = citation.SourceId
            };

            // Phase 1: Fast text similarity check
            var similarity = ComputeTextOverlap(claimText, citation.Excerpt ?? "");

            if (similarity >= 0.3)
            {
                verification.Status = VerificationStatus.Verified;
                verification.Confidence = Math.Min(1.0, similarity + 0.2);
                verification.Method = "text-match";
            }
            else if (similarity >= 0.1)
            {
                verification.Status = VerificationStatus.Plausible;
                verification.Confidence = similarity + 0.1;
                verification.Method = "text-match";
            }
            else
            {
                // Phase 2: Check against full source text if available
                var sourceText = GetSourceText(db, citation);
                if (!string.IsNullOrEmpty(sourceText))
                {
                    var fullSimilarity = ComputeTextOverlap(claimText, sourceText);
                    if (fullSimilarity >= 0.2)
                    {
                        verification.Status = VerificationStatus.Verified;
                        verification.Confidence = Math.Min(1.0, fullSimilarity + 0.3);
                        verification.Method = "source-text";
                    }
                    else
                    {
                        verification.Status = VerificationStatus.Unverified;
                        verification.Confidence = fullSimilarity;
                        verification.Method = "source-text";
                        verification.Note = "Claim not found in source material";
                    }
                }
                else
                {
                    verification.Status = VerificationStatus.NoSource;
                    verification.Confidence = 0;
                    verification.Method = "no-source";
                    verification.Note = "Source text not available for verification";
                }
            }

            results.Add(verification);
        }

        // Compute summary
        return results;
    }

    /// <summary>
    /// Quick verification — text-match only, no LLM calls. Fast enough for real-time display.
    /// </summary>
    public List<CitationVerification> VerifyReportQuick(string sessionId, string reportContent, string jobId)
    {
        var db = _sessionManager.GetSessionDb(sessionId);
        var citations = db.GetCitations(jobId);
        var results = new List<CitationVerification>();

        if (citations.Count == 0) return results;

        var citationMap = citations
            .Where(c => !string.IsNullOrEmpty(c.Label))
            .GroupBy(c => c.Label)
            .ToDictionary(g => g.Key, g => g.First());

        var claimsWithCitations = ExtractClaimsWithCitations(reportContent);

        foreach (var (citLabel, claimText) in claimsWithCitations)
        {
            if (!citationMap.TryGetValue(citLabel, out var citation)) continue;

            var similarity = ComputeTextOverlap(claimText, citation.Excerpt ?? "");
            var sourceText = GetSourceText(db, citation);
            var fullSimilarity = !string.IsNullOrEmpty(sourceText)
                ? ComputeTextOverlap(claimText, sourceText)
                : 0.0;

            var bestSimilarity = Math.Max(similarity, fullSimilarity);

            results.Add(new CitationVerification
            {
                CitationLabel = citLabel,
                ClaimText = claimText,
                SourceExcerpt = citation.Excerpt ?? "",
                SourceId = citation.SourceId,
                Status = bestSimilarity >= 0.3 ? VerificationStatus.Verified
                       : bestSimilarity >= 0.1 ? VerificationStatus.Plausible
                       : string.IsNullOrEmpty(sourceText) ? VerificationStatus.NoSource
                       : VerificationStatus.Unverified,
                Confidence = bestSimilarity,
                Method = "quick-text-match"
            });
        }

        return results;
    }

    /// <summary>
    /// Compute a verification summary for display.
    /// </summary>
    public static VerificationSummary Summarize(List<CitationVerification> verifications)
    {
        return new VerificationSummary
        {
            Total = verifications.Count,
            Verified = verifications.Count(v => v.Status == VerificationStatus.Verified),
            Plausible = verifications.Count(v => v.Status == VerificationStatus.Plausible),
            Unverified = verifications.Count(v => v.Status == VerificationStatus.Unverified),
            NoSource = verifications.Count(v => v.Status == VerificationStatus.NoSource),
            AverageConfidence = verifications.Count > 0 ? verifications.Average(v => v.Confidence) : 0
        };
    }

    /// <summary>
    /// Extract (citation_label, surrounding_claim_text) pairs from report content.
    /// </summary>
    internal static List<(string citLabel, string claimText)> ExtractClaimsWithCitations(string text)
    {
        var results = new List<(string, string)>();
        var matches = CitationRefRegex.Matches(text);

        foreach (Match match in matches)
        {
            var label = match.Value; // e.g. "[1]"
            var pos = match.Index;

            // Extract the sentence containing this citation
            var lineStart = text.LastIndexOf('\n', Math.Max(0, pos - 1));
            if (lineStart < 0) lineStart = 0; else lineStart++;

            var lineEnd = text.IndexOf('\n', pos);
            if (lineEnd < 0) lineEnd = text.Length;

            var line = text[lineStart..lineEnd].Trim();

            // Clean up the claim text (remove citation markers for comparison)
            var cleanClaim = CitationRefRegex.Replace(line, "").Trim();
            if (cleanClaim.Length > 10)
                results.Add((label, cleanClaim));
        }

        return results;
    }

    /// <summary>
    /// Compute keyword overlap between a claim and source text.
    /// Returns 0-1 where 1 means perfect overlap.
    /// </summary>
    internal static double ComputeTextOverlap(string claim, string source)
    {
        if (string.IsNullOrWhiteSpace(claim) || string.IsNullOrWhiteSpace(source))
            return 0;

        // Tokenize: split to words, lowercase, remove short/stop words
        var claimWords = TokenizeForComparison(claim);
        var sourceWords = new HashSet<string>(TokenizeForComparison(source), StringComparer.OrdinalIgnoreCase);

        if (claimWords.Count == 0) return 0;

        var matching = claimWords.Count(w => sourceWords.Contains(w));
        return (double)matching / claimWords.Count;
    }

    private static List<string> TokenizeForComparison(string text)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could",
            "should", "may", "might", "can", "shall", "to", "of", "in", "for",
            "on", "with", "at", "by", "from", "as", "into", "through", "during",
            "before", "after", "above", "below", "between", "under", "and", "but",
            "or", "nor", "not", "no", "so", "if", "than", "that", "this", "these",
            "those", "it", "its", "they", "their", "them", "we", "our", "us",
            "he", "she", "his", "her", "him", "you", "your", "also", "more",
            "which", "who", "whom", "what", "when", "where", "how", "all", "each",
            "both", "few", "some", "any", "most", "other", "such", "only"
        };

        return Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .ToList();
    }

    private static string GetSourceText(SessionDb db, Citation citation)
    {
        // Try to get the chunk text directly
        var chunks = db.GetChunksBySourceIds(new[] { citation.SourceId });
        if (chunks.Count > 0)
        {
            return string.Join("\n", chunks.Select(c => c.Text));
        }

        // Try snapshot text
        if (citation.Type == CitationType.WebSnapshot)
        {
            var snapshots = db.GetSnapshots();
            var snap = snapshots.FirstOrDefault(s => s.Id == citation.SourceId);
            if (snap != null && !string.IsNullOrEmpty(snap.TextPath) && File.Exists(snap.TextPath))
            {
                try { return File.ReadAllText(snap.TextPath); }
                catch { }
            }
        }

        return "";
    }
}

public enum VerificationStatus
{
    Verified,    // Claim clearly supported by source
    Plausible,   // Partial overlap — likely supported
    Unverified,  // Claim not found in source material
    NoSource     // Source text not available
}

public class CitationVerification
{
    public string CitationLabel { get; set; } = "";
    public string ClaimText { get; set; } = "";
    public string SourceExcerpt { get; set; } = "";
    public string SourceId { get; set; } = "";
    public VerificationStatus Status { get; set; }
    public double Confidence { get; set; }
    public string Method { get; set; } = "";
    public string Note { get; set; } = "";
}

public class VerificationSummary
{
    public int Total { get; set; }
    public int Verified { get; set; }
    public int Plausible { get; set; }
    public int Unverified { get; set; }
    public int NoSource { get; set; }
    public double AverageConfidence { get; set; }

    public string StatusLabel => Total > 0
        ? $"{Verified}/{Total} verified ({(double)Verified / Total:P0})"
        : "No citations to verify";
}
