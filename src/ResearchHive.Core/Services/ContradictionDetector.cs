using ResearchHive.Core.Data;
using ResearchHive.Core.Models;
using System.Text.RegularExpressions;

namespace ResearchHive.Core.Services;

/// <summary>
/// Detects contradictions and disagreements between evidence chunks in a session.
/// Uses both heuristic keyword detection (fast) and optional LLM verification.
/// Unlike ChatGPT which just mentions disagreements in prose, we programmatically
/// identify and flag specific conflicting evidence pairs.
/// </summary>
public class ContradictionDetector
{
    private readonly SessionManager _sessionManager;
    private readonly LlmService _llmService;
    private readonly EmbeddingService _embeddingService;

    // Contradiction signal words/patterns
    private static readonly string[] ContradictionSignals = {
        "however", "contrary", "contradicts", "disagrees", "inconsistent",
        "in contrast", "on the other hand", "disputes", "challenges",
        "opposes", "refutes", "conflicts with", "whereas", "although",
        "despite", "nevertheless", "conversely", "unlike"
    };

    // Negation patterns that can flip a claim's meaning
    private static readonly Regex NegationPattern = new(
        @"\b(not|no|never|neither|nor|none|cannot|can't|won't|doesn't|didn't|hasn't|haven't|isn't|aren't|wasn't|weren't)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ContradictionDetector(SessionManager sessionManager, LlmService llmService, EmbeddingService embeddingService)
    {
        _sessionManager = sessionManager;
        _llmService = llmService;
        _embeddingService = embeddingService;
    }

    /// <summary>
    /// Detect contradictions among evidence chunks in a session.
    /// Phase 1: Find topically similar chunk pairs (embedding similarity).
    /// Phase 2: Check each pair for contradiction signals.
    /// Returns pairs of evidence that appear to disagree.
    /// </summary>
    public async Task<List<Contradiction>> DetectAsync(
        string sessionId, int maxPairs = 50, CancellationToken ct = default)
    {
        var db = _sessionManager.GetSessionDb(sessionId);
        var chunks = db.GetAllChunks();

        if (chunks.Count < 2) return new();

        // Phase 1: Find topically similar pairs using embedding cosine similarity
        var candidates = await FindSimilarPairsAsync(chunks, maxPairs, ct);

        // Phase 2: Check each candidate pair for contradiction signals
        var contradictions = new List<Contradiction>();
        foreach (var (chunkA, chunkB, similarity) in candidates)
        {
            if (ct.IsCancellationRequested) break;

            var score = ScoreContradiction(chunkA.Text, chunkB.Text);
            if (score > 0.3)
            {
                contradictions.Add(new Contradiction
                {
                    ChunkA = chunkA,
                    ChunkB = chunkB,
                    TopicSimilarity = similarity,
                    ContradictionScore = score,
                    Type = ClassifyContradiction(chunkA.Text, chunkB.Text),
                    Summary = GenerateContradictionSummary(chunkA.Text, chunkB.Text)
                });
            }
        }

        return contradictions.OrderByDescending(c => c.ContradictionScore).ToList();
    }

    /// <summary>
    /// Quick heuristic-only detection — no embeddings needed. Checks all chunk pairs
    /// from different sources for contradiction signals. Good for small evidence sets.
    /// </summary>
    public List<Contradiction> DetectQuick(string sessionId, int maxResults = 20)
    {
        var db = _sessionManager.GetSessionDb(sessionId);
        var chunks = db.GetAllChunks();

        if (chunks.Count < 2) return new();

        var contradictions = new List<Contradiction>();

        // Group chunks by source to compare across sources
        var bySource = chunks.GroupBy(c => c.SourceId).ToList();

        for (int i = 0; i < bySource.Count && contradictions.Count < maxResults; i++)
        {
            for (int j = i + 1; j < bySource.Count && contradictions.Count < maxResults; j++)
            {
                foreach (var chunkA in bySource[i].Take(5))
                {
                    foreach (var chunkB in bySource[j].Take(5))
                    {
                        // Check topic overlap first
                        var topicOverlap = ComputeTopicOverlap(chunkA.Text, chunkB.Text);
                        if (topicOverlap < 0.15) continue; // Not about the same topic

                        var score = ScoreContradiction(chunkA.Text, chunkB.Text);
                        if (score > 0.3)
                        {
                            contradictions.Add(new Contradiction
                            {
                                ChunkA = chunkA,
                                ChunkB = chunkB,
                                TopicSimilarity = topicOverlap,
                                ContradictionScore = score,
                                Type = ClassifyContradiction(chunkA.Text, chunkB.Text),
                                Summary = GenerateContradictionSummary(chunkA.Text, chunkB.Text)
                            });
                        }
                    }
                }
            }
        }

        return contradictions.OrderByDescending(c => c.ContradictionScore).Take(maxResults).ToList();
    }

    /// <summary>
    /// Use LLM to verify and explain detected contradictions.
    /// Takes raw candidates and asks the LLM to confirm/reject and explain.
    /// </summary>
    public async Task<List<Contradiction>> VerifyWithLlmAsync(
        string sessionId, List<Contradiction> candidates, CancellationToken ct = default)
    {
        var verified = new List<Contradiction>();

        foreach (var c in candidates.Take(10))
        {
            if (ct.IsCancellationRequested) break;

            var prompt = $@"Do these two evidence excerpts contradict each other? Answer YES or NO, then explain in 1-2 sentences.

EXCERPT A:
{c.ChunkA.Text[..Math.Min(500, c.ChunkA.Text.Length)]}

EXCERPT B:
{c.ChunkB.Text[..Math.Min(500, c.ChunkB.Text.Length)]}

Format: YES/NO | Explanation";

            try
            {
                var response = await _llmService.GenerateAsync(prompt, maxTokens: 200, ct: ct);
                var isContradiction = response.TrimStart().StartsWith("YES", StringComparison.OrdinalIgnoreCase);

                if (isContradiction)
                {
                    var explanation = response.Contains('|') ? response[(response.IndexOf('|') + 1)..].Trim() : response;
                    c.LlmVerified = true;
                    c.LlmExplanation = explanation;
                    verified.Add(c);
                }
            }
            catch { /* Skip on LLM failure */ }
        }

        return verified;
    }

    private async Task<List<(Chunk, Chunk, double)>> FindSimilarPairsAsync(
        List<Chunk> chunks, int maxPairs, CancellationToken ct)
    {
        var pairs = new List<(Chunk, Chunk, double)>();

        // Use embeddings if available
        var withEmbeddings = chunks.Where(c => c.Embedding != null && c.Embedding.Length > 0).ToList();

        if (withEmbeddings.Count >= 2)
        {
            for (int i = 0; i < withEmbeddings.Count && pairs.Count < maxPairs * 3; i++)
            {
                for (int j = i + 1; j < withEmbeddings.Count && pairs.Count < maxPairs * 3; j++)
                {
                    // Skip same-source pairs (we want cross-source contradictions)
                    if (withEmbeddings[i].SourceId == withEmbeddings[j].SourceId) continue;

                    var sim = CosineSimilarity(withEmbeddings[i].Embedding!, withEmbeddings[j].Embedding!);
                    if (sim > 0.5) // Topically similar but from different sources
                    {
                        pairs.Add((withEmbeddings[i], withEmbeddings[j], sim));
                    }
                }
            }

            return pairs.OrderByDescending(p => p.Item3).Take(maxPairs).ToList();
        }

        // Fallback: use keyword overlap for topic similarity
        var bySource = chunks.GroupBy(c => c.SourceId).ToList();
        for (int i = 0; i < bySource.Count && pairs.Count < maxPairs; i++)
        {
            for (int j = i + 1; j < bySource.Count && pairs.Count < maxPairs; j++)
            {
                foreach (var a in bySource[i].Take(3))
                {
                    foreach (var b in bySource[j].Take(3))
                    {
                        var overlap = ComputeTopicOverlap(a.Text, b.Text);
                        if (overlap > 0.2)
                            pairs.Add((a, b, overlap));
                    }
                }
            }
        }

        return pairs.OrderByDescending(p => p.Item3).Take(maxPairs).ToList();
    }

    /// <summary>
    /// Score how likely two texts contradict each other (0-1).
    /// Combines: negation asymmetry, contradiction signal words, numeric disagreement.
    /// </summary>
    internal static double ScoreContradiction(string textA, string textB)
    {
        var score = 0.0;
        var lowerA = textA.ToLowerInvariant();
        var lowerB = textB.ToLowerInvariant();

        // Check for contradiction signal words (partial score)
        var signalCount = ContradictionSignals.Count(signal =>
            lowerA.Contains(signal) || lowerB.Contains(signal));
        score += Math.Min(0.3, signalCount * 0.1);

        // Check for negation asymmetry: one text negates, the other doesn't (on same topic)
        var negA = NegationPattern.Matches(lowerA).Count;
        var negB = NegationPattern.Matches(lowerB).Count;
        var negDiff = Math.Abs(negA - negB);
        if (negDiff >= 2) score += 0.2;
        else if (negDiff >= 1) score += 0.1;

        // Check for numeric disagreement (different numbers for same context)
        var numsA = ExtractNumbers(textA);
        var numsB = ExtractNumbers(textB);
        if (numsA.Count > 0 && numsB.Count > 0)
        {
            // If they share topic words but have different numbers, that's suspicious
            var topicOverlap = ComputeTopicOverlap(textA, textB);
            if (topicOverlap > 0.2)
            {
                var sharedNums = numsA.Intersect(numsB).Count();
                var totalNums = numsA.Union(numsB).Count();
                if (totalNums > 0 && sharedNums < totalNums * 0.5)
                    score += 0.2;
            }
        }

        return Math.Min(1.0, score);
    }

    private static ContradictionType ClassifyContradiction(string textA, string textB)
    {
        var numsA = ExtractNumbers(textA);
        var numsB = ExtractNumbers(textB);

        if (numsA.Count > 0 && numsB.Count > 0 && !numsA.SetEquals(numsB))
            return ContradictionType.NumericDisagreement;

        var negA = NegationPattern.IsMatch(textA);
        var negB = NegationPattern.IsMatch(textB);
        if (negA != negB)
            return ContradictionType.DirectContradiction;

        return ContradictionType.InterpretationDifference;
    }

    private static string GenerateContradictionSummary(string textA, string textB)
    {
        var snippetA = textA.Length > 80 ? textA[..80].Trim() + "..." : textA;
        var snippetB = textB.Length > 80 ? textB[..80].Trim() + "..." : textB;
        return $"Source A: \"{snippetA}\" vs Source B: \"{snippetB}\"";
    }

    internal static double ComputeTopicOverlap(string textA, string textB)
    {
        var wordsA = TokenizeContent(textA);
        var wordsB = new HashSet<string>(TokenizeContent(textB), StringComparer.OrdinalIgnoreCase);
        if (wordsA.Count == 0 || wordsB.Count == 0) return 0;

        var overlap = wordsA.Count(w => wordsB.Contains(w));
        return (double)overlap / Math.Max(wordsA.Count, wordsB.Count);
    }

    private static List<string> TokenizeContent(string text)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "have", "has",
            "had", "do", "does", "did", "will", "would", "could", "should", "may",
            "might", "can", "to", "of", "in", "for", "on", "with", "at", "by", "from",
            "as", "and", "but", "or", "not", "if", "than", "that", "this", "it", "its",
            "they", "their", "them", "we", "our", "he", "she", "his", "her", "you", "your"
        };

        return Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .ToList();
    }

    private static HashSet<double> ExtractNumbers(string text)
    {
        var nums = new HashSet<double>();
        foreach (Match m in Regex.Matches(text, @"\b\d+\.?\d*\b"))
        {
            if (double.TryParse(m.Value, out var num) && num > 0)
                nums.Add(num);
        }
        return nums;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;

        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var denom = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denom > 0 ? dot / denom : 0;
    }
}

public enum ContradictionType
{
    DirectContradiction,      // One source says X, another says not-X
    NumericDisagreement,      // Same metric, different numbers
    InterpretationDifference  // Same data, different conclusions
}

public class Contradiction
{
    public Chunk ChunkA { get; set; } = new();
    public Chunk ChunkB { get; set; } = new();
    public double TopicSimilarity { get; set; }
    public double ContradictionScore { get; set; }
    public ContradictionType Type { get; set; }
    public string Summary { get; set; } = "";
    public bool LlmVerified { get; set; }
    public string LlmExplanation { get; set; } = "";
}
