using ResearchHive.Core.Models;
using ResearchHive.Core.Services;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for the new pipeline helpers added during the Speed + Quality overhaul.
/// Covers: URL relevance scoring, query extraction/cleaning, citation dedup,
/// grounding score, and claims extraction.
/// </summary>
public class ResearchPipelineTests
{
    // ──────────────────────────────────────────────────────────
    // ExtractQueries
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void ExtractQueries_DotFormat()
    {
        var plan = "1. What is quantum computing?\n2. How does it work?\n3. Key applications";
        var result = ResearchJobRunner.ExtractQueries(plan, "quantum");
        Assert.Equal(3, result.Count);
        Assert.Equal("What is quantum computing?", result[0]);
    }

    [Fact]
    public void ExtractQueries_ParenthesisFormat()
    {
        var plan = "1) First query\n2) Second query";
        var result = ResearchJobRunner.ExtractQueries(plan, "fallback");
        Assert.Equal(2, result.Count);
        Assert.Equal("First query", result[0]);
    }

    [Fact]
    public void ExtractQueries_DashFormat()
    {
        var plan = "1- Query one\n2- Query two\n3- Query three";
        var result = ResearchJobRunner.ExtractQueries(plan, "fallback");
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ExtractQueries_ColonFormat()
    {
        // "Alpha" and "Beta" are each 5 chars, but filter requires > 5, so fallback fires
        var plan = "1: Alpha query here\n2: Beta query here";
        var result = ResearchJobRunner.ExtractQueries(plan, "fallback");
        Assert.Equal(2, result.Count);
        Assert.Equal("Alpha query here", result[0]);
    }

    [Fact]
    public void ExtractQueries_FallbackToPrompt_WhenGarbage()
    {
        var plan = "The assistant is thinking about what to search for. Hmm.";
        var result = ResearchJobRunner.ExtractQueries(plan, "local AI inference");
        // Fallback produces 5 generated queries
        Assert.Equal(5, result.Count);
        Assert.All(result, q => Assert.Contains("local AI inference", q));
    }

    [Fact]
    public void ExtractQueries_MixedFormats()
    {
        var plan = "Here are the queries:\n1. First query here\n2) Second query here\n3- Third query here\nSome trailing text.";
        var result = ResearchJobRunner.ExtractQueries(plan, "fallback");
        Assert.Equal(3, result.Count);
    }

    // ──────────────────────────────────────────────────────────
    // CleanSearchQuery
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void CleanSearchQuery_RemovesBooleanOperators()
    {
        var cleaned = ResearchJobRunner.CleanSearchQuery("AI AND machine learning OR deep learning");
        Assert.DoesNotContain("AND", cleaned);
        Assert.DoesNotContain("OR", cleaned);
    }

    [Fact]
    public void CleanSearchQuery_RemovesNOT()
    {
        var cleaned = ResearchJobRunner.CleanSearchQuery("quantum computing NOT classical");
        Assert.DoesNotContain("NOT", cleaned);
    }

    [Fact]
    public void CleanSearchQuery_RemovesParentheses()
    {
        var cleaned = ResearchJobRunner.CleanSearchQuery("(deep learning) (neural networks)");
        Assert.DoesNotContain("(", cleaned);
        Assert.DoesNotContain(")", cleaned);
    }

    [Fact]
    public void CleanSearchQuery_CapsAt120Chars()
    {
        var longQuery = new string('x', 200);
        var cleaned = ResearchJobRunner.CleanSearchQuery(longQuery);
        Assert.True(cleaned.Length <= 120);
    }

    [Fact]
    public void CleanSearchQuery_StripsExcessiveQuotes()
    {
        var cleaned = ResearchJobRunner.CleanSearchQuery("\"\"\"overly quoted\"\"\"");
        // Should reduce excessive quoting
        Assert.DoesNotContain("\"\"\"", cleaned);
    }

    [Fact]
    public void CleanSearchQuery_NormalizesWhitespace()
    {
        var cleaned = ResearchJobRunner.CleanSearchQuery("  too   many    spaces  ");
        Assert.DoesNotContain("  ", cleaned);
        Assert.Equal(cleaned.Trim(), cleaned);
    }

    // ──────────────────────────────────────────────────────────
    // TokenizeForRelevance
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void TokenizeForRelevance_SplitsAndLowercases()
    {
        var tokens = ResearchJobRunner.TokenizeForRelevance("Artificial Machine Learning");
        Assert.Contains("artificial", tokens);
        Assert.Contains("machine", tokens);
        Assert.Contains("learning", tokens);
    }

    [Fact]
    public void TokenizeForRelevance_FiltersShortWords()
    {
        // Words under 3 chars are filtered: "a", "an" excluded. "the" is 3 chars so it passes.
        var tokens = ResearchJobRunner.TokenizeForRelevance("a an the Artificial Intelligence");
        Assert.DoesNotContain("a", tokens);
        Assert.DoesNotContain("an", tokens);
        Assert.Contains("the", tokens); // 3 chars passes >= 3 filter
        Assert.Contains("artificial", tokens);
    }

    [Fact]
    public void TokenizeForRelevance_HandlesSpecialChars()
    {
        var tokens = ResearchJobRunner.TokenizeForRelevance("deep-learning/neural_networks");
        // Should tokenize around separators
        Assert.True(tokens.Length > 0);
    }

    // ──────────────────────────────────────────────────────────
    // ScoreAndFilterUrls
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void ScoreAndFilterUrls_PreservesRelevantUrls()
    {
        var urls = new List<string>
        {
            "https://example.com/ai-machine-learning-guide",
            "https://example.com/cooking-recipes",
            "https://example.com/ai-neural-networks",
        };
        var filtered = ResearchJobRunner.ScoreAndFilterUrls(urls, "artificial intelligence machine learning", new List<string> { "AI machine learning" });
        // The AI-related URLs should score higher and appear first
        Assert.True(filtered.Count >= 2);
        Assert.Contains(filtered, u => u.Contains("ai-machine-learning"));
        Assert.Contains(filtered, u => u.Contains("ai-neural-networks"));
    }

    [Fact]
    public void ScoreAndFilterUrls_KeepsExploratoryUrls()
    {
        // Even URLs with no keyword match should get through as exploratory (up to 5)
        var urls = new List<string>
        {
            "https://unrelated1.com/page",
            "https://unrelated2.com/page",
            "https://unrelated3.com/page",
        };
        var filtered = ResearchJobRunner.ScoreAndFilterUrls(urls, "quantum computing", new List<string> { "qubits" });
        // Should keep some as exploratory
        Assert.True(filtered.Count > 0);
    }

    [Fact]
    public void ScoreAndFilterUrls_EmptyList_ReturnsEmpty()
    {
        var filtered = ResearchJobRunner.ScoreAndFilterUrls(new List<string>(), "query", new List<string>());
        Assert.Empty(filtered);
    }

    // ──────────────────────────────────────────────────────────
    // DeduplicateEvidenceBySource
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void DeduplicateEvidenceBySource_KeepsBestPerDomain()
    {
        var results = new List<RetrievalResult>
        {
            MakeResult("src1", 0.95f, "Best chunk from site-a"),
            MakeResult("src1", 0.80f, "Second chunk from site-a"),
            MakeResult("src2", 0.90f, "Best chunk from site-b"),
            MakeResult("src2", 0.70f, "Second from site-b"),
            MakeResult("src3", 0.85f, "Only chunk from site-c"),
        };
        var urlMap = new Dictionary<string, string>
        {
            ["src1"] = "https://site-a.com/page1",
            ["src2"] = "https://site-b.com/page2",
            ["src3"] = "https://site-c.com/page3",
        };

        var deduped = ResearchJobRunner.DeduplicateEvidenceBySource(results, urlMap);

        // All different domains, each under 3 chunk limit → all accepted as primary
        Assert.Equal(5, deduped.Count);
        Assert.Equal("Best chunk from site-a", deduped[0].Chunk.Text);
        Assert.Equal("Second chunk from site-a", deduped[1].Chunk.Text);
        Assert.Equal("Best chunk from site-b", deduped[2].Chunk.Text);
    }

    [Fact]
    public void DeduplicateEvidenceBySource_SameDomainLimitedToThree()
    {
        // 5 chunks from same domain — only first 3 are "primary", rest overflow
        var results = new List<RetrievalResult>
        {
            MakeResult("s1", 0.9f, "A"),
            MakeResult("s2", 0.8f, "B"),
            MakeResult("s3", 0.7f, "C"),
            MakeResult("s4", 0.6f, "D"),
            MakeResult("s5", 0.5f, "E"),
        };
        var urlMap = new Dictionary<string, string>
        {
            ["s1"] = "https://www.same.com/page1",
            ["s2"] = "https://same.com/page2",
            ["s3"] = "https://same.com/page3",
            ["s4"] = "https://same.com/page4",
            ["s5"] = "https://www.same.com/page5",
        };

        var deduped = ResearchJobRunner.DeduplicateEvidenceBySource(results, urlMap);

        Assert.Equal(5, deduped.Count);
        // First 3 should be primary (under domain limit)
        Assert.Equal("A", deduped[0].Chunk.Text);
        Assert.Equal("B", deduped[1].Chunk.Text);
        Assert.Equal("C", deduped[2].Chunk.Text);
        // Overflow: D and E come after
        Assert.Equal("D", deduped[3].Chunk.Text);
    }

    [Fact]
    public void DeduplicateEvidenceBySource_FallsBackToSourceId()
    {
        // When URL not in map, uses SourceId as key (treated as its own "domain")
        var results = new List<RetrievalResult>
        {
            MakeResult("x", 0.9f, "First"),
            MakeResult("x", 0.8f, "Second"),
            MakeResult("x", 0.7f, "Third"),
            MakeResult("x", 0.6f, "Fourth"),
        };
        var urlMap = new Dictionary<string, string>(); // empty

        var deduped = ResearchJobRunner.DeduplicateEvidenceBySource(results, urlMap);
        Assert.Equal(4, deduped.Count);
        // First 3 primary (domain limit), 4th is overflow
        Assert.Equal("First", deduped[0].Chunk.Text);
        Assert.Equal("Fourth", deduped[3].Chunk.Text);
    }

    // ──────────────────────────────────────────────────────────
    // ExtractClaims
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void ExtractClaims_FilterHeaders()
    {
        var text = "# Header\n## Sub-header\nThis is a claim that is more than twenty characters.";
        var claims = ResearchJobRunner.ExtractClaims(text);
        Assert.Single(claims);
        Assert.DoesNotContain(claims, c => c.StartsWith("#"));
    }

    [Fact]
    public void ExtractClaims_FilterBullets()
    {
        var text = "* Bullet point item that is long enough\nA regular sentence with more than twenty characters here.";
        var claims = ResearchJobRunner.ExtractClaims(text);
        Assert.Single(claims);
        Assert.DoesNotContain(claims, c => c.TrimStart().StartsWith("*"));
    }

    [Fact]
    public void ExtractClaims_FilterShortLines()
    {
        var text = "Short\nAlso short\nThis is a long enough sentence to pass the filter check.";
        var claims = ResearchJobRunner.ExtractClaims(text);
        Assert.Single(claims);
    }

    [Fact]
    public void ExtractClaims_Max20()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"This is claim number {i} which is quite long enough."));
        var claims = ResearchJobRunner.ExtractClaims(lines);
        Assert.Equal(20, claims.Count);
    }

    // ──────────────────────────────────────────────────────────
    // ComputeGroundingScore
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void ComputeGroundingScore_AllCited()
    {
        var claims = new List<string>
        {
            "AI is transforming research [1]",
            "Quantum computing advances [2]",
            "Local inference is feasible [3]"
        };
        Assert.Equal(1.0, ResearchJobRunner.ComputeGroundingScore(claims));
    }

    [Fact]
    public void ComputeGroundingScore_NoneCited()
    {
        var claims = new List<string>
        {
            "AI is transforming research",
            "Quantum computing advances",
        };
        Assert.Equal(0.0, ResearchJobRunner.ComputeGroundingScore(claims));
    }

    [Fact]
    public void ComputeGroundingScore_PartialCited()
    {
        var claims = new List<string>
        {
            "AI is transforming research [1]",
            "Quantum computing advances",
        };
        Assert.Equal(0.5, ResearchJobRunner.ComputeGroundingScore(claims));
    }

    [Fact]
    public void ComputeGroundingScore_EmptyClaims()
    {
        Assert.Equal(0.0, ResearchJobRunner.ComputeGroundingScore(new List<string>()));
    }

    [Fact]
    public void ComputeGroundingScore_RequiresProperBracketFormat()
    {
        // Should NOT count stray brackets like [something] or just [
        var claims = new List<string>
        {
            "This has a bracket [ but no number",
            "This has [text] inside brackets",
            "This has proper cite [5]",
        };
        var score = ResearchJobRunner.ComputeGroundingScore(claims);
        // Only the last one should count
        Assert.Equal(1.0 / 3.0, score, 2);
    }

    [Fact]
    public void ComputeGroundingScore_MultipleRefsInOneClaim()
    {
        var claims = new List<string>
        {
            "Multiple references [1][2][3] in one claim",
        };
        Assert.Equal(1.0, ResearchJobRunner.ComputeGroundingScore(claims));
    }

    // ──────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────

    private static RetrievalResult MakeResult(string sourceId, float score, string text)
    {
        return new RetrievalResult
        {
            SourceId = sourceId,
            Score = score,
            Chunk = new Chunk { SourceId = sourceId, Text = text }
        };
    }
}
