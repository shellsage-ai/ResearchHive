using FluentAssertions;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;
using Microsoft.Extensions.Logging;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for Phase 17: Model Tiering, Agentic Codex, Infrastructure Hardening.
///
/// Covers:
/// - ModelTier enum and AppSettings.MiniModelMap
/// - Agentic prompt builder (BuildFullAgenticPrompt)
/// - Agentic response parser (ParseFullAgenticAnalysis + ParseComplementLine)
/// - LlmCircuitBreaker state machine (open/closed/half-open)
/// - LlmService.CalculateBackoffDelay (exponential, capped, jittered)
/// - LlmService.ResolveModelOverride and ResolveMiniModel
/// </summary>
public class Phase17AgenticTests
{
    // ─── ModelTier & MiniModelMap ───

    [Fact]
    public void ModelTier_HasExpectedValues()
    {
        Enum.GetValues<ModelTier>().Should().HaveCount(3);
        ModelTier.Default.Should().Be(default(ModelTier));
    }

    [Fact]
    public void MiniModelMap_ContainsAllProviders()
    {
        AppSettings.MiniModelMap.Should().ContainKey(PaidProviderType.OpenAI);
        AppSettings.MiniModelMap.Should().ContainKey(PaidProviderType.Anthropic);
        AppSettings.MiniModelMap.Should().ContainKey(PaidProviderType.ChatGptPlus);
    }

    [Fact]
    public void MiniModelMap_ChatGptPlus_ReturnsMiniCodex()
    {
        AppSettings.MiniModelMap[PaidProviderType.ChatGptPlus].Should().Be("gpt-5.1-codex-mini");
    }

    [Fact]
    public void CodexMiniModel_DefaultValue()
    {
        var settings = new AppSettings();
        settings.CodexMiniModel.Should().Be("gpt-5.1-codex-mini");
    }

    [Fact]
    public void KnownCloudModels_ChatGptPlus_ContainsMiniCodex()
    {
        AppSettings.KnownCloudModels[PaidProviderType.ChatGptPlus]
            .Should().Contain("gpt-5.1-codex-mini");
    }

    // ─── Agentic Prompt Builder ───

    [Fact]
    public void BuildFullAgenticPrompt_IncludesAllFiveSections()
    {
        var profile = CreateTestProfile();
        var chunks = new[] { "public class MyService { }", "public class MyRepository { }" };

        var prompt = RepoScannerService.BuildFullAgenticPrompt(profile, chunks);

        prompt.Should().Contain("## CodeBook");
        prompt.Should().Contain("## Frameworks");
        prompt.Should().Contain("## Strengths");
        prompt.Should().Contain("## Gaps");
        prompt.Should().Contain("## Complementary Projects");
    }

    [Fact]
    public void BuildFullAgenticPrompt_IncludesWebSearchInstructions()
    {
        var profile = CreateTestProfile();
        var chunks = new[] { "code snippet" };

        var prompt = RepoScannerService.BuildFullAgenticPrompt(profile, chunks);

        // Should instruct Codex to use web search for complements
        prompt.Should().ContainAny("web search", "search the web", "search online", "find real");
    }

    [Fact]
    public void BuildFullAgenticPrompt_IncludesCodeChunks()
    {
        var profile = CreateTestProfile();
        var chunks = new[] { "public class Serializer { }", "public interface IHandler { }" };

        var prompt = RepoScannerService.BuildFullAgenticPrompt(profile, chunks);

        prompt.Should().Contain("Serializer");
        prompt.Should().Contain("IHandler");
    }

    [Fact]
    public void BuildFullAgenticPrompt_IncludesProfileMetadata()
    {
        var profile = CreateTestProfile();
        profile.PrimaryLanguage = "C#";
        profile.Description = "A WPF research app";

        var prompt = RepoScannerService.BuildFullAgenticPrompt(profile, new[] { "code" });

        prompt.Should().Contain("C#");
    }

    // ─── Agentic Response Parser ───

    [Fact]
    public void ParseFullAgenticAnalysis_ParsesAllFiveSections()
    {
        var response = @"## Summary
A .NET 8 WPF desktop application for agentic research workflows with multi-provider LLM routing and RAG-grounded analysis.

## CodeBook
This is a .NET 8 WPF application using CommunityToolkit.Mvvm.

## Frameworks
- .NET 8
- WPF
- CommunityToolkit.Mvvm 8.2.2

## Strengths
- Clean MVVM architecture with proper separation of concerns
- Comprehensive DI setup via ServiceRegistration
- Rich RAG pipeline with hybrid search

## Gaps
- No interface abstractions for core services
- Missing structured logging throughout
- No CI/CD pipeline configuration

## Complementary Projects
### Serilog
- **URL**: https://github.com/serilog/serilog
- **Purpose**: Structured diagnostic logging for .NET
- **What it adds**: Structured logging with sinks, enrichers, and log levels
- **Category**: Logging
- **License**: Apache-2.0
- **Maturity**: Production-ready";

        var (summary, codeBook, frameworks, strengths, gaps, complements) =
            RepoScannerService.ParseFullAgenticAnalysis(response);

        summary.Should().Contain("agentic research");
        codeBook.Should().Contain("CommunityToolkit.Mvvm");
        frameworks.Should().Contain(".NET 8");
        frameworks.Should().Contain("WPF");
        strengths.Should().HaveCountGreaterOrEqualTo(3);
        strengths.Should().Contain(s => s.Contains("MVVM"));
        gaps.Should().HaveCountGreaterOrEqualTo(3);
        gaps.Should().Contain(g => g.Contains("interface"));
        complements.Should().HaveCountGreaterOrEqualTo(1);
        complements[0].Name.Should().Be("Serilog");
    }

    [Fact]
    public void ParseFullAgenticAnalysis_HandlesEmptyResponse()
    {
        var (_, codeBook, frameworks, strengths, gaps, complements) =
            RepoScannerService.ParseFullAgenticAnalysis("");

        codeBook.Should().BeEmpty();
        frameworks.Should().BeEmpty();
        strengths.Should().BeEmpty();
        gaps.Should().BeEmpty();
        complements.Should().BeEmpty();
    }

    [Fact]
    public void ParseFullAgenticAnalysis_HandlesPartialResponse()
    {
        var response = @"## CodeBook
Architecture overview here.

## Strengths
- Good test coverage";

        var (_, codeBook, frameworks, strengths, gaps, complements) =
            RepoScannerService.ParseFullAgenticAnalysis(response);

        codeBook.Should().Contain("Architecture overview");
        strengths.Should().HaveCountGreaterOrEqualTo(1);
        // Missing sections should be empty, not throw
        gaps.Should().BeEmpty();
        complements.Should().BeEmpty();
    }

    [Fact]
    public void ParseFullAgenticAnalysis_ParsesMultipleComplements()
    {
        var response = @"## CodeBook
App summary.

## Frameworks
- .NET 8

## Strengths
- Good architecture

## Gaps
- Missing logging

## Complementary Projects
### Serilog
- **URL**: https://github.com/serilog/serilog
- **Purpose**: Structured logging
- **What it adds**: Log sinks and enrichers
- **Category**: Logging
- **License**: Apache-2.0
- **Maturity**: Production-ready

### Polly
- **URL**: https://github.com/App-vNext/Polly
- **Purpose**: Resilience and fault handling
- **What it adds**: Retry, circuit breaker, timeout policies
- **Category**: Resilience
- **License**: BSD-3-Clause
- **Maturity**: Production-ready";

        var (_, _, _, _, _, complements) =
            RepoScannerService.ParseFullAgenticAnalysis(response);

        complements.Should().HaveCount(2);
        complements[0].Name.Should().Be("Serilog");
        complements[0].Purpose.Should().Contain("logging");
        complements[1].Name.Should().Be("Polly");
        complements[1].Category.Should().Be("Resilience");
    }

    // ─── LlmCircuitBreaker ───

    [Fact]
    public void CircuitBreaker_StartsInClosedState()
    {
        var cb = new LlmCircuitBreaker();
        cb.IsOllamaOpen.Should().BeFalse();
        cb.IsCloudOpen.Should().BeFalse();
    }

    [Fact]
    public void CircuitBreaker_OpensAfterThresholdFailures()
    {
        var cb = new LlmCircuitBreaker(failureThreshold: 3, cooldownSeconds: 60);

        // Record 3 consecutive Ollama failures
        cb.RecordOllamaFailure();
        cb.RecordOllamaFailure();
        cb.IsOllamaOpen.Should().BeFalse("not at threshold yet");

        cb.RecordOllamaFailure();
        cb.IsOllamaOpen.Should().BeTrue("threshold reached");
    }

    [Fact]
    public void CircuitBreaker_SuccessResetsFailureCount()
    {
        var cb = new LlmCircuitBreaker(failureThreshold: 3);

        cb.RecordOllamaFailure();
        cb.RecordOllamaFailure();
        cb.RecordOllamaSuccess(); // Reset

        cb.RecordOllamaFailure();
        cb.IsOllamaOpen.Should().BeFalse("success resets the counter");
    }

    [Fact]
    public void CircuitBreaker_CloudCircuitIndependent()
    {
        var cb = new LlmCircuitBreaker(failureThreshold: 2);

        // Open cloud circuit
        cb.RecordCloudFailure();
        cb.RecordCloudFailure();
        cb.IsCloudOpen.Should().BeTrue();

        // Ollama should still be closed
        cb.IsOllamaOpen.Should().BeFalse();
    }

    [Fact]
    public void CircuitBreaker_DefaultThresholdIsFive()
    {
        var cb = new LlmCircuitBreaker();

        for (int i = 0; i < 4; i++)
            cb.RecordOllamaFailure();

        cb.IsOllamaOpen.Should().BeFalse("4 < default threshold of 5");

        cb.RecordOllamaFailure();
        cb.IsOllamaOpen.Should().BeTrue("5 >= default threshold");
    }

    // ─── LlmService.CalculateBackoffDelay ───

    [Fact]
    public void CalculateBackoffDelay_ReturnsCorrectBaseValues()
    {
        // Use reflection to call the private static method
        var method = typeof(LlmService).GetMethod("CalculateBackoffDelay",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("CalculateBackoffDelay should exist");

        // Attempt 0: ~1000ms base
        var delay0 = (int)method!.Invoke(null, new object[] { 0 })!;
        delay0.Should().BeInRange(750, 1250, "attempt 0 base is 1000ms ± 25% jitter");

        // Attempt 1: ~2000ms base
        var delay1 = (int)method.Invoke(null, new object[] { 1 })!;
        delay1.Should().BeInRange(1500, 2500, "attempt 1 base is 2000ms ± 25% jitter");

        // Attempt 2: ~4000ms base
        var delay2 = (int)method.Invoke(null, new object[] { 2 })!;
        delay2.Should().BeInRange(3000, 5000, "attempt 2 base is 4000ms ± 25% jitter");
    }

    [Fact]
    public void CalculateBackoffDelay_CapsAtEightSeconds()
    {
        var method = typeof(LlmService).GetMethod("CalculateBackoffDelay",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull();

        // Attempt 10: should be capped at ~8000ms, not 1024000ms
        var delay = (int)method!.Invoke(null, new object[] { 10 })!;
        delay.Should().BeLessThan(10_500, "should be capped near 8000ms");
    }

    // ─── LlmService.ResolveMiniModel ───

    [Fact]
    public void ResolveMiniModel_ReturnsCodexMini_ForChatGptPlus()
    {
        var settings = new AppSettings
        {
            UsePaidProvider = true,
            PaidProvider = PaidProviderType.ChatGptPlus,
            CodexMiniModel = "gpt-5.1-codex-mini"
        };
        var llmService = new LlmService(settings, new CodexCliService(settings));

        var result = llmService.ResolveMiniModel();

        result.Should().Be("gpt-5.1-codex-mini");
    }

    [Fact]
    public void ResolveMiniModel_ReturnsOpenAIMini_ForOpenAI()
    {
        var settings = new AppSettings
        {
            UsePaidProvider = true,
            PaidProvider = PaidProviderType.OpenAI
        };
        var llmService = new LlmService(settings, new CodexCliService(settings));

        var result = llmService.ResolveMiniModel();

        result.Should().Be("gpt-4.1-mini");
    }

    // ─── ILlmService Interface Compliance ───

    [Fact]
    public void LlmService_ImplementsILlmService()
    {
        typeof(LlmService).Should().Implement<ILlmService>();
    }

    [Fact]
    public void RetrievalService_ImplementsIRetrievalService()
    {
        typeof(RetrievalService).Should().Implement<IRetrievalService>();
    }

    [Fact]
    public void BrowserSearchService_ImplementsIBrowserSearchService()
    {
        typeof(BrowserSearchService).Should().Implement<IBrowserSearchService>();
    }

    // ─── Agentic Prompt Contains Self-Verification Instructions ───

    [Fact]
    public void BuildFullAgenticPrompt_ContainsSelfVerificationInstructions()
    {
        var profile = CreateTestProfile();
        profile.Gaps.Add("No CI/CD pipeline");

        var prompt = RepoScannerService.BuildFullAgenticPrompt(profile, new[] { "code" });

        // Should include instructions about verifying gaps against code, not just listing potential issues
        prompt.Should().ContainAny("verify", "self-verify", "genuine", "genuinely MISSING", "actually missing");
    }

    // ─── Helper ───

    private static RepoProfile CreateTestProfile()
    {
        return new RepoProfile
        {
            RepoUrl = "https://github.com/test/repo",
            Owner = "test",
            Name = "repo",
            Description = "A test repository",
            PrimaryLanguage = "C#",
            Languages = new List<string> { "C#", "XAML" },
            Stars = 100,
            Forks = 10
        };
    }
}
