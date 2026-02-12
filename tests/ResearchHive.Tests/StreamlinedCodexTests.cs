using FluentAssertions;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for the Streamlined Codex Mode feature.
/// Verifies: setting defaults, query extraction logic, routing decisions.
/// </summary>
public class StreamlinedCodexTests
{
    // ── AppSettings defaults ──

    [Fact]
    public void StreamlinedCodexMode_DefaultsToTrue()
    {
        var settings = new AppSettings();
        settings.StreamlinedCodexMode.Should().BeTrue(
            "new installs should default to streamlined mode for faster Codex research");
    }

    [Fact]
    public void StreamlinedCodexMode_CanBeDisabled()
    {
        var settings = new AppSettings { StreamlinedCodexMode = false };
        settings.StreamlinedCodexMode.Should().BeFalse();
    }

    // ── ExtractMinimalQueries ──

    [Fact]
    public void ExtractMinimalQueries_ProducesAtLeast2Queries()
    {
        var queries = ResearchJobRunner.ExtractMinimalQueries(
            "What is the capital of France?",
            DomainPack.GeneralResearch);

        queries.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void ExtractMinimalQueries_FirstQueryIsFromPrompt()
    {
        var prompt = "What are the effects of sleep deprivation on memory?";
        var queries = ResearchJobRunner.ExtractMinimalQueries(prompt, DomainPack.GeneralResearch);

        // First query should be derived from the prompt text
        queries[0].Should().Contain("sleep deprivation");
    }

    [Fact]
    public void ExtractMinimalQueries_DetectsSafetySubQuery()
    {
        var prompt = "What are green synthesis methods for silver nanoparticles? " +
                     "What are the safety considerations for handling them at bench scale?";
        var queries = ResearchJobRunner.ExtractMinimalQueries(prompt, DomainPack.ChemistrySafe);

        // Should detect "safety" keyword and extract a sub-query around it
        queries.Should().Contain(q => q.ToLower().Contains("safety"));
    }

    [Fact]
    public void ExtractMinimalQueries_DetectsComparisonSubQuery()
    {
        var prompt = "How do green-synthesized nanoparticles compare to chemically reduced ones?";
        var queries = ResearchJobRunner.ExtractMinimalQueries(prompt, DomainPack.ChemistrySafe);

        queries.Should().Contain(q => q.ToLower().Contains("compare"));
    }

    [Fact]
    public void ExtractMinimalQueries_AddsDomainScopedQuery_ForNonGeneral()
    {
        var prompt = "What are the best resin systems for 3D printing?";
        var queries = ResearchJobRunner.ExtractMinimalQueries(prompt, DomainPack.MakerMaterials);

        // Should have a domain-prefixed query for non-general packs
        queries.Should().Contain(q => q.Contains("MakerMaterials"));
    }

    [Fact]
    public void ExtractMinimalQueries_NoDomainPrefix_ForGeneralResearch()
    {
        var prompt = "What are the effects of climate change on coral reefs?";
        var queries = ResearchJobRunner.ExtractMinimalQueries(prompt, DomainPack.GeneralResearch);

        // Should NOT have a domain-prefixed query for GeneralResearch
        queries.Should().NotContain(q => q.Contains("GeneralResearch"));
    }

    [Fact]
    public void ExtractMinimalQueries_CapsAt3Queries()
    {
        var prompt = "This is a very long research prompt about safety and comparing " +
                     "multiple different methods versus alternatives with extensive detail.";
        var queries = ResearchJobRunner.ExtractMinimalQueries(prompt, DomainPack.ProgrammingResearchIP);

        queries.Should().HaveCountLessThanOrEqualTo(3);
    }

    [Fact]
    public void ExtractMinimalQueries_HandlesShortPrompt()
    {
        var queries = ResearchJobRunner.ExtractMinimalQueries("AI ethics", DomainPack.GeneralResearch);
        queries.Should().HaveCountGreaterThanOrEqualTo(2);
        queries[0].Should().Be("AI ethics");
    }

    [Fact]
    public void ExtractMinimalQueries_HandlesLongPrompt_TruncatesFirstQuery()
    {
        var longPrompt = new string('A', 200) + "? Some extra detail about the question.";
        var queries = ResearchJobRunner.ExtractMinimalQueries(longPrompt, DomainPack.GeneralResearch);

        // First query should be capped around 120 chars or the first sentence
        queries[0].Length.Should().BeLessThanOrEqualTo(201); // 200 + "?"
    }

    [Fact]
    public void ExtractMinimalQueries_ProducesDistinctQueries()
    {
        var prompt = "What is the role of topology in machine learning?";
        var queries = ResearchJobRunner.ExtractMinimalQueries(prompt, DomainPack.Math);

        queries.Should().OnlyHaveUniqueItems();
    }

    // ── LlmService.IsCodexOAuthActive ──

    [Fact]
    public void IsCodexOAuthActive_FalseWhenNoPaidProvider()
    {
        var settings = new AppSettings { UsePaidProvider = false };
        var llm = new LlmService(settings);
        llm.IsCodexOAuthActive.Should().BeFalse();
    }

    [Fact]
    public void IsCodexOAuthActive_FalseWhenProviderIsNotChatGptPlus()
    {
        var settings = new AppSettings
        {
            UsePaidProvider = true,
            PaidProvider = PaidProviderType.OpenAI,
            PaidProviderApiKey = "test-key"
        };
        var llm = new LlmService(settings);
        llm.IsCodexOAuthActive.Should().BeFalse();
    }

    [Fact]
    public void IsCodexOAuthActive_FalseWhenAuthModeIsApiKey()
    {
        var settings = new AppSettings
        {
            UsePaidProvider = true,
            PaidProvider = PaidProviderType.ChatGptPlus,
            ChatGptPlusAuth = ChatGptPlusAuthMode.ApiKey
        };
        var llm = new LlmService(settings);
        llm.IsCodexOAuthActive.Should().BeFalse(
            "ApiKey mode should not trigger streamlined path — it uses OpenAI API, not Codex CLI");
    }

    [Fact]
    public void IsCodexOAuthActive_FalseWhenCodexCliNull()
    {
        var settings = new AppSettings
        {
            UsePaidProvider = true,
            PaidProvider = PaidProviderType.ChatGptPlus,
            ChatGptPlusAuth = ChatGptPlusAuthMode.CodexOAuth
        };
        // No CodexCliService injected
        var llm = new LlmService(settings, codexCli: null);
        llm.IsCodexOAuthActive.Should().BeFalse(
            "without a Codex CLI instance, CodexOAuth cannot be active");
    }

    // ── Full pipeline chemistry prompt test ──

    [Fact]
    public void ExtractMinimalQueries_ChemistryExample_CoversSafetyAndComparison()
    {
        var prompt = "What are the most effective green synthesis methods for producing silver nanoparticles? " +
                     "Focus on plant-extract-mediated synthesis, comparing different plant sources in terms of " +
                     "reduction efficiency, particle size distribution, and stability. What are the safety " +
                     "considerations for handling silver nanoparticles at bench scale? Include information on " +
                     "required PPE, disposal protocols, and any known health hazards. How do green-synthesized " +
                     "nanoparticles compare to chemically reduced ones in antibacterial efficacy?";

        var queries = ResearchJobRunner.ExtractMinimalQueries(prompt, DomainPack.ChemistrySafe);

        queries.Should().HaveCountGreaterThanOrEqualTo(2);
        queries.Should().HaveCountLessThanOrEqualTo(3);

        // Should detect the safety sub-question
        queries.Should().Contain(q => q.ToLower().Contains("safety") || q.ToLower().Contains("ppe"));

        // Should have a domain-scoped query for ChemistrySafe
        queries.Should().Contain(q => q.Contains("ChemistrySafe"));
    }
}
