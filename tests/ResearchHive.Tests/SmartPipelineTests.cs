using FluentAssertions;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for Phase 16: Smart Pipeline — Codex Consolidation, Ollama JSON Output, Parallelism.
///
/// Covers:
/// - Consolidated analysis prompt builder (combines CodeBook + Analysis + Gap Verification)
/// - Consolidated analysis parser (extracts CodeBook, Frameworks, Strengths, Gaps from single response)
/// - JSON complement prompt builder + parser for Ollama structured output
/// - LlmService.IsLargeContextProvider routing logic
/// - Existing pipeline compatibility (separate calls still work for local models)
/// </summary>
public class SmartPipelineTests
{
    // ─── Consolidated Analysis Prompt ───

    [Fact]
    public void BuildConsolidatedAnalysisPrompt_IncludesAllChunks()
    {
        var profile = CreateTestProfile();
        var chunks = Enumerable.Range(1, 20).Select(i => $"public class Service{i} {{ }}").ToList();

        var prompt = RepoScannerService.BuildConsolidatedAnalysisPrompt(profile, chunks);

        foreach (var chunk in chunks)
            prompt.Should().Contain(chunk);
        prompt.Should().Contain("Excerpt 1:");
        prompt.Should().Contain("Excerpt 20:");
    }

    [Fact]
    public void BuildConsolidatedAnalysisPrompt_RequestsAllFourSections()
    {
        var profile = CreateTestProfile();
        var prompt = RepoScannerService.BuildConsolidatedAnalysisPrompt(profile, new[] { "code" });

        prompt.Should().Contain("## CodeBook");
        prompt.Should().Contain("## Frameworks");
        prompt.Should().Contain("## Strengths");
        prompt.Should().Contain("## Gaps");
    }

    [Fact]
    public void BuildConsolidatedAnalysisPrompt_IncludesAntiHallucinationRules()
    {
        var profile = CreateTestProfile();
        var prompt = RepoScannerService.BuildConsolidatedAnalysisPrompt(profile, new[] { "code" });

        prompt.Should().Contain("Self-verify");
        prompt.Should().Contain("MISSING");
        prompt.Should().Contain("NOT critiques");
        prompt.Should().Contain("real class names");
    }

    [Fact]
    public void BuildConsolidatedAnalysisPrompt_IncludesProfileMetadata()
    {
        var profile = CreateTestProfile();
        profile.Dependencies.Add(new RepoDependency { Name = "CommunityToolkit.Mvvm", Version = "8.2.2" });
        profile.IndexedFileCount = 42;
        profile.IndexedChunkCount = 256;

        var prompt = RepoScannerService.BuildConsolidatedAnalysisPrompt(profile, new[] { "code" });

        prompt.Should().Contain("CommunityToolkit.Mvvm");
        prompt.Should().Contain("42 files");
        prompt.Should().Contain("256 chunks");
    }

    // ─── Consolidated Analysis Parser ───

    [Fact]
    public void ParseConsolidatedAnalysis_ExtractsAllSections()
    {
        var response = @"## CodeBook
### Purpose
A WPF desktop application for agentic research.

### Architecture Overview
MVVM pattern with CommunityToolkit.Mvvm.

## Frameworks
- .NET 8
- WPF
- SQLite

## Strengths
- Multi-provider LLM support with 9 providers
- RAG-grounded analysis with hybrid search
- Comprehensive telemetry pipeline

## Gaps
- No CI/CD pipeline configuration
- No automated security scanning
- Missing API rate limiting";

        var (codeBook, frameworks, strengths, gaps) = RepoScannerService.ParseConsolidatedAnalysis(response);

        codeBook.Should().Contain("WPF desktop application");
        codeBook.Should().Contain("MVVM pattern");
        frameworks.Should().HaveCount(3);
        frameworks.Should().Contain(".NET 8");
        strengths.Should().HaveCount(3);
        strengths.Should().Contain(s => s.Contains("Multi-provider"));
        gaps.Should().HaveCount(3);
        gaps.Should().Contain(g => g.Contains("CI/CD"));
    }

    [Fact]
    public void ParseConsolidatedAnalysis_PreservesSubheadingsInCodeBook()
    {
        var response = @"## CodeBook
### Purpose
Research tool.

### Key Abstractions
- LlmService — routes to providers
- SessionManager — manages sessions

## Frameworks
- .NET 8

## Strengths
- Good architecture

## Gaps
- No tests";

        var (codeBook, _, _, _) = RepoScannerService.ParseConsolidatedAnalysis(response);

        codeBook.Should().Contain("### Purpose");
        codeBook.Should().Contain("### Key Abstractions");
        codeBook.Should().Contain("LlmService");
    }

    [Fact]
    public void ParseConsolidatedAnalysis_HandlesEmptyResponse()
    {
        var (codeBook, frameworks, strengths, gaps) = RepoScannerService.ParseConsolidatedAnalysis("");

        codeBook.Should().BeEmpty();
        frameworks.Should().BeEmpty();
        strengths.Should().BeEmpty();
        gaps.Should().BeEmpty();
    }

    [Fact]
    public void ParseConsolidatedAnalysis_HandlesPartialResponse()
    {
        var response = @"## CodeBook
Some architecture notes.

## Strengths
- Good design";

        var (codeBook, frameworks, strengths, gaps) = RepoScannerService.ParseConsolidatedAnalysis(response);

        codeBook.Should().Contain("architecture notes");
        frameworks.Should().BeEmpty();
        strengths.Should().HaveCount(1);
        gaps.Should().BeEmpty();
    }

    // ─── JSON Complement Prompt + Parser ───

    [Fact]
    public void BuildJsonComplementPrompt_RequestsJsonFormat()
    {
        var profile = CreateTestProfile();
        var enriched = new List<(string topic, List<(string url, string description)> entries)>
        {
            ("testing", new List<(string, string)> { ("https://github.com/xunit/xunit", "Unit testing framework | 4000 stars") })
        };

        var prompt = RepoScannerService.BuildJsonComplementPrompt(profile, enriched, 5);

        prompt.Should().Contain("JSON");
        prompt.Should().Contain("complements");
        prompt.Should().Contain("xunit");
        prompt.Should().Contain("DIVERSITY");
    }

    [Fact]
    public void ParseJsonComplements_ParsesValidJson()
    {
        var json = @"{
  ""complements"": [
    {
      ""name"": ""xunit"",
      ""url"": ""https://github.com/xunit/xunit"",
      ""purpose"": ""Unit testing framework"",
      ""what_it_adds"": ""Structured test execution"",
      ""category"": ""Testing"",
      ""license"": ""Apache-2.0"",
      ""maturity"": ""Mature""
    },
    {
      ""name"": ""coverlet"",
      ""url"": ""https://github.com/coverlet-coverage/coverlet"",
      ""purpose"": ""Code coverage tool"",
      ""what_it_adds"": ""Coverage metrics"",
      ""category"": ""Testing"",
      ""license"": ""MIT"",
      ""maturity"": ""Mature""
    }
  ]
}";

        var results = RepoScannerService.ParseJsonComplements(json);

        results.Should().HaveCount(2);
        results[0].Name.Should().Be("xunit");
        results[0].Category.Should().Be("Testing");
        results[0].License.Should().Be("Apache-2.0");
        results[1].Name.Should().Be("coverlet");
        results[1].WhatItAdds.Should().Be("Coverage metrics");
    }

    [Fact]
    public void ParseJsonComplements_HandlesCodeBlockWrapping()
    {
        var json = @"```json
{
  ""complements"": [
    {
      ""name"": ""serilog"",
      ""url"": ""https://github.com/serilog/serilog"",
      ""purpose"": ""Structured logging"",
      ""what_it_adds"": ""Structured log output"",
      ""category"": ""Monitoring"",
      ""license"": ""Apache-2.0"",
      ""maturity"": ""Mature""
    }
  ]
}
```";

        var results = RepoScannerService.ParseJsonComplements(json);
        results.Should().HaveCount(1);
        results[0].Name.Should().Be("serilog");
    }

    [Fact]
    public void ParseJsonComplements_ReturnsEmptyOnInvalidJson()
    {
        RepoScannerService.ParseJsonComplements("not json at all").Should().BeEmpty();
        RepoScannerService.ParseJsonComplements("{}").Should().BeEmpty();
        RepoScannerService.ParseJsonComplements("").Should().BeEmpty();
    }

    [Fact]
    public void ParseJsonComplements_SkipsEmptyNameEntries()
    {
        var json = @"{
  ""complements"": [
    { ""name"": """", ""url"": ""https://github.com/empty/empty"", ""purpose"": ""test"" },
    { ""name"": ""valid"", ""url"": ""https://github.com/valid/repo"", ""purpose"": ""works"" }
  ]
}";

        var results = RepoScannerService.ParseJsonComplements(json);
        results.Should().HaveCount(1);
        results[0].Name.Should().Be("valid");
    }

    // ─── LlmService.IsLargeContextProvider ───

    [Fact]
    public void IsLargeContextProvider_TrueForCloudOnly()
    {
        var settings = new AppSettings { Routing = RoutingStrategy.CloudOnly };
        var svc = new LlmService(settings);
        svc.IsLargeContextProvider.Should().BeTrue();
    }

    [Fact]
    public void IsLargeContextProvider_TrueForCloudPrimary()
    {
        var settings = new AppSettings { Routing = RoutingStrategy.CloudPrimary };
        var svc = new LlmService(settings);
        svc.IsLargeContextProvider.Should().BeTrue();
    }

    [Fact]
    public void IsLargeContextProvider_FalseForLocalOnly()
    {
        var settings = new AppSettings { Routing = RoutingStrategy.LocalOnly };
        var svc = new LlmService(settings);
        svc.IsLargeContextProvider.Should().BeFalse();
    }

    [Fact]
    public void IsLargeContextProvider_FalseForLocalWithCloudFallback()
    {
        var settings = new AppSettings { Routing = RoutingStrategy.LocalWithCloudFallback };
        var svc = new LlmService(settings);
        svc.IsLargeContextProvider.Should().BeFalse();
    }

    // ─── Consolidated vs Separate Pipeline Compatibility ───

    [Fact]
    public void ExistingParseAnalysis_StillWorks()
    {
        // Ensure the separate pipeline (Ollama path) still works unchanged
        var profile = CreateTestProfile();
        var response = @"## Frameworks
- .NET 8
- WPF
## Strengths
- Good architecture with MVVM pattern
- Multi-provider LLM support
## Gaps
- No CI/CD pipeline
- No automated testing infrastructure";

        RepoScannerService.ParseAnalysis(response, profile);

        profile.Frameworks.Should().Contain(".NET 8");
        profile.Strengths.Should().HaveCount(2);
        profile.Gaps.Should().HaveCount(2);
    }

    [Fact]
    public void ExistingParseVerifiedGaps_StillWorks()
    {
        // Ensure the separate gap verification (Ollama path) still works
        var response = @"## Verified Gaps
- No CI/CD pipeline configuration
- No automated security scanning
## False Positives Removed
- No cloud integration: FALSE — LlmService supports 9 providers";

        var gaps = RepoScannerService.ParseVerifiedGaps(response);
        gaps.Should().HaveCount(2);
        gaps.Should().Contain("No CI/CD pipeline configuration");
    }

    [Fact]
    public void ExistingParseComplements_StillWorks()
    {
        // Ensure the markdown complement parser still works as fallback
        var response = @"## Complement
- Name: xunit
- Url: https://github.com/xunit/xunit
- Purpose: Unit testing framework
- WhatItAdds: Test infrastructure
- Category: Testing
- License: Apache-2.0
- Maturity: Mature";

        var complements = ParseComplementsViaReflection(response);
        complements.Should().HaveCount(1);
        complements[0].Name.Should().Be("xunit");
    }

    // ─── Consolidated Prompt — Larger Scale ───

    [Fact]
    public void BuildConsolidatedAnalysisPrompt_Handles40Chunks()
    {
        var profile = CreateTestProfile();
        var chunks = Enumerable.Range(1, 40).Select(i =>
            $"public class Service{i} {{ public async Task ProcessAsync() {{ /* implementation {i} */ }} }}").ToList();

        var prompt = RepoScannerService.BuildConsolidatedAnalysisPrompt(profile, chunks);

        prompt.Should().Contain("Excerpt 1:");
        prompt.Should().Contain("Excerpt 40:");
        prompt.Should().Contain("Service1");
        prompt.Should().Contain("Service40");
        // Should be a substantial prompt for cloud models
        prompt.Length.Should().BeGreaterThan(2000);
    }

    [Fact]
    public void ParseConsolidatedAnalysis_HandlesRealWorldResponse()
    {
        // Simulate a realistic Codex response
        var response = @"## CodeBook

### Purpose
ResearchHive is a WPF .NET 8 desktop application that orchestrates multi-agent agentic research workflows. It combines local Ollama and cloud LLM providers with web search, RAG retrieval, and automated report generation.

### Architecture Overview
The application follows an MVVM pattern using CommunityToolkit.Mvvm 8.2.2. Core services include:
- `LlmService` — Multi-provider LLM routing (9 providers: Ollama, OpenAI, Anthropic, Gemini, Mistral, OpenRouter, Azure, GitHub Models, Codex CLI)
- `ResearchJobRunner` — Orchestrates research workflows with plan/decompose/search/synthesize phases
- `RepoIntelligenceJobRunner` — 7-phase repo analysis pipeline

### Key Abstractions
- `SessionManager` — Manages per-session SQLite databases
- `RetrievalService` — Hybrid semantic + keyword search
- `EmbeddingService` — Ollama embeddings with fallback to trigram hashing

### Data Flow
User → ViewModel → JobRunner → LlmService → Cloud/Local → RAG → Report

### Extension Points
Add domain packs via the `DomainPack` enum. Add LLM providers in `LlmService`.

### Build & Run
`dotnet build`, `dotnet run --project src/ResearchHive`

### Notable Design Decisions
Streaming Codex CLI via stdin for unbounded prompt size. SemaphoreSlim-based parallelism.

## Frameworks
- .NET 8
- WPF + MVVM (CommunityToolkit.Mvvm)
- SQLite (Microsoft.Data.Sqlite)
- xUnit + FluentAssertions

## Strengths
- Multi-provider LLM routing with auto-fallback (LlmService supports 9 providers)
- RAG-grounded analysis using 18 diverse queries for broad codebase coverage
- Comprehensive pipeline telemetry with per-phase timing and LLM call records
- Deterministic framework detection (40+ package mappings) supplements LLM analysis
- 391 tests with 389 passing — strong test coverage
- Smart parallelism: RAG queries, gap evidence, GitHub enrichment all use Task.WhenAll

## Gaps
- No CI/CD pipeline configuration (no .github/workflows or Azure DevOps files)
- No automated security scanning or dependency vulnerability checking
- No API rate limiting for GitHub API calls (relies on PAT quotas)
- No internationalization or localization support
- No user authentication or multi-user support (single-user desktop app)
- Missing structured logging (no Serilog/NLog integration despite being a complex application)";

        var (codeBook, frameworks, strengths, gaps) = RepoScannerService.ParseConsolidatedAnalysis(response);

        codeBook.Should().Contain("WPF .NET 8");
        codeBook.Should().Contain("LlmService");
        codeBook.Should().Contain("SessionManager");
        codeBook.Should().Contain("### Purpose");
        codeBook.Should().Contain("### Architecture Overview");

        frameworks.Should().HaveCountGreaterOrEqualTo(3);
        frameworks.Should().Contain(".NET 8");

        strengths.Should().HaveCountGreaterOrEqualTo(5);
        strengths.Should().Contain(s => s.Contains("9 providers") || s.Contains("Multi-provider"));
        strengths.Should().Contain(s => s.Contains("391 tests") || s.Contains("test coverage"));

        gaps.Should().HaveCountGreaterOrEqualTo(5);
        gaps.Should().Contain(g => g.Contains("CI/CD"));
        gaps.Should().Contain(g => g.Contains("security"));
    }

    // ─── Helpers ───

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

    /// <summary>
    /// Call the private ParseComplements method via the existing public code path for backward compat testing.
    /// Since ParseComplements is private, we test it indirectly by verifying the markdown format still parses.
    /// </summary>
    private static List<ComplementProject> ParseComplementsViaReflection(string response)
    {
        // Use reflection to call the private static method
        var method = typeof(ComplementResearchService).GetMethod("ParseComplements",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (method == null) throw new InvalidOperationException("ParseComplements method not found");
        return (List<ComplementProject>)method.Invoke(null, new object[] { response })!;
    }
}
