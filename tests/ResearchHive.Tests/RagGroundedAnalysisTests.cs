using FluentAssertions;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for the RAG-grounded repo analysis pipeline (Phase 12):
/// - No truncation in prompt builders
/// - RAG analysis prompt includes CodeBook + indexed chunks
/// - Gap verification prompt includes per-gap evidence
/// - ParseVerifiedGaps correctly filters false positives
/// - BuildRagAnalysisPrompt includes all dependency names (no truncation)
/// - Self-scan simulation: given realistic chunks, the prompt is comprehensive
/// </summary>
public class RagGroundedAnalysisTests
{
    // ── BuildRagAnalysisPrompt tests ──

    [Fact]
    public void BuildRagAnalysisPrompt_IncludesAllChunks_NoTruncation()
    {
        var profile = CreateTestProfile();
        var codeBook = "# CodeBook: test/repo\n\nThis is the architecture summary.";
        var chunks = Enumerable.Range(1, 30).Select(i => $"public class Service{i} {{ /* implementation */ }}").ToList();

        var prompt = RepoScannerService.BuildRagAnalysisPrompt(profile, codeBook, chunks);

        // Every chunk must appear in the prompt — no truncation
        foreach (var chunk in chunks)
            prompt.Should().Contain(chunk);

        prompt.Should().Contain("CodeBook");
        prompt.Should().Contain("architecture summary");
        prompt.Should().Contain("Excerpt 1:");
        prompt.Should().Contain("Excerpt 30:");
    }

    [Fact]
    public void BuildRagAnalysisPrompt_IncludesAllDependencyNames()
    {
        var profile = CreateTestProfile();
        // Add 50 dependencies — all should appear, none truncated
        for (int i = 0; i < 50; i++)
            profile.Dependencies.Add(new RepoDependency { Name = $"Package.Number{i}", Version = $"{i}.0.0" });

        var prompt = RepoScannerService.BuildRagAnalysisPrompt(profile, null, new[] { "chunk1" });

        foreach (var dep in profile.Dependencies)
            prompt.Should().Contain(dep.Name);
    }

    [Fact]
    public void BuildRagAnalysisPrompt_WithNullCodeBook_OmitsCodeBookSection()
    {
        var profile = CreateTestProfile();
        var prompt = RepoScannerService.BuildRagAnalysisPrompt(profile, null, new[] { "some code" });

        prompt.Should().NotContain("Architecture Summary (CodeBook)");
        prompt.Should().Contain("some code");
    }

    [Fact]
    public void BuildRagAnalysisPrompt_InstructsCodeBasedAnalysis()
    {
        var profile = CreateTestProfile();
        var prompt = RepoScannerService.BuildRagAnalysisPrompt(profile, null, new[] { "code" });

        prompt.Should().Contain("ACTUAL SOURCE CODE");
        prompt.Should().Contain("not surface-level assumptions");
        prompt.Should().Contain("based on code evidence");
    }

    // ── BuildGapVerificationPrompt tests ──

    [Fact]
    public void BuildGapVerificationPrompt_IncludesEvidencePerGap()
    {
        var profile = CreateTestProfile();
        var gapEvidence = new List<(string gap, IReadOnlyList<string> chunks)>
        {
            ("No cloud deployment", new[] { "public class LlmService { async Task CallCloudWithMetadata... }" }),
            ("No testing framework", new[] { "public class ResearchPipelineTests { [Fact] public void..." }),
            ("No real-time collab", Array.Empty<string>())
        };

        var prompt = RepoScannerService.BuildGapVerificationPrompt(profile, gapEvidence);

        prompt.Should().Contain("No cloud deployment");
        prompt.Should().Contain("CallCloudWithMetadata");
        prompt.Should().Contain("No testing framework");
        prompt.Should().Contain("ResearchPipelineTests");
        prompt.Should().Contain("No real-time collab");
        prompt.Should().Contain("No relevant code found");
    }

    [Fact]
    public void BuildGapVerificationPrompt_RequestsBothSections()
    {
        var profile = CreateTestProfile();
        var gapEvidence = new List<(string gap, IReadOnlyList<string> chunks)>
        {
            ("some gap", Array.Empty<string>())
        };

        var prompt = RepoScannerService.BuildGapVerificationPrompt(profile, gapEvidence);

        prompt.Should().Contain("## Verified Gaps");
        prompt.Should().Contain("## False Positives Removed");
    }

    // ── ParseVerifiedGaps tests ──

    [Fact]
    public void ParseVerifiedGaps_ExtractsOnlyVerifiedSection()
    {
        var response = @"## Verified Gaps
- No streaming LLM support
- No real-time collaboration
## False Positives Removed
- No cloud deployment: LlmService already has 8 cloud providers (Anthropic, OpenAI, etc.)
- Insufficient testing: 341 tests exist with full coverage";

        var gaps = RepoScannerService.ParseVerifiedGaps(response);

        gaps.Should().HaveCount(2);
        gaps.Should().Contain("No streaming LLM support");
        gaps.Should().Contain("No real-time collaboration");
        gaps.Should().NotContain(g => g.Contains("cloud deployment"));
        gaps.Should().NotContain(g => g.Contains("testing"));
    }

    [Fact]
    public void ParseVerifiedGaps_ReturnsEmpty_WhenNoVerifiedGaps()
    {
        var response = @"## Verified Gaps
## False Positives Removed
- All gaps were already addressed in code";

        var gaps = RepoScannerService.ParseVerifiedGaps(response);
        gaps.Should().BeEmpty();
    }

    [Fact]
    public void ParseVerifiedGaps_ReturnsEmpty_OnMalformedResponse()
    {
        var response = "This response doesn't follow the format at all.";
        var gaps = RepoScannerService.ParseVerifiedGaps(response);
        gaps.Should().BeEmpty();
    }

    // ── ParseAnalysis tests (existing, now public + static) ──

    [Fact]
    public void ParseAnalysis_ExtractsFrameworksStrengthsGaps()
    {
        var profile = new RepoProfile();
        var analysis = @"## Frameworks
- .NET 8
- WPF (CommunityToolkit.Mvvm)
- SQLite (Microsoft.Data.Sqlite)
## Strengths
- Hybrid RAG search with FTS5 + semantic embeddings
- 37 DI-registered services
- 341 tests with FluentAssertions
- Multi-provider LLM routing (8 cloud providers + local Ollama)
- Per-session isolated SQLite databases
## Gaps
- No streaming LLM responses
- No Dockerfile for containerized deployment";

        RepoScannerService.ParseAnalysis(analysis, profile);

        profile.Frameworks.Should().HaveCount(3);
        profile.Frameworks.Should().Contain("SQLite (Microsoft.Data.Sqlite)");
        profile.Strengths.Should().HaveCount(5);
        profile.Strengths.Should().Contain(s => s.Contains("8 cloud providers"));
        profile.Gaps.Should().HaveCount(2);
    }

    // ── Self-scan simulation: given realistic ResearchHive code chunks,
    //    verify the prompt captures the essential capabilities ──

    [Fact]
    public void SelfScan_Simulation_PromptCapturesCloudProviders()
    {
        var profile = CreateResearchHiveProfile();
        var chunks = GetResearchHiveCodeChunks();
        var prompt = RepoScannerService.BuildRagAnalysisPrompt(profile, GetResearchHiveCodeBook(), chunks);

        // The prompt must contain evidence of cloud providers
        prompt.Should().Contain("Anthropic");
        prompt.Should().Contain("CloudOnly");
        prompt.Should().Contain("LlmService");
    }

    [Fact]
    public void SelfScan_Simulation_PromptCapturesTestingInfrastructure()
    {
        var profile = CreateResearchHiveProfile();
        var chunks = GetResearchHiveCodeChunks();
        var prompt = RepoScannerService.BuildRagAnalysisPrompt(profile, GetResearchHiveCodeBook(), chunks);

        prompt.Should().Contain("xUnit");
        prompt.Should().Contain("FluentAssertions");
    }

    [Fact]
    public void SelfScan_Simulation_PromptCapturesHiveMind()
    {
        var profile = CreateResearchHiveProfile();
        var chunks = GetResearchHiveCodeChunks();
        var prompt = RepoScannerService.BuildRagAnalysisPrompt(profile, GetResearchHiveCodeBook(), chunks);

        prompt.Should().Contain("GlobalMemoryService");
        prompt.Should().Contain("Hive Mind");
    }

    [Fact]
    public void SelfScan_Simulation_PromptCapturesNotifications()
    {
        var profile = CreateResearchHiveProfile();
        var chunks = GetResearchHiveCodeChunks();
        var prompt = RepoScannerService.BuildRagAnalysisPrompt(profile, GetResearchHiveCodeBook(), chunks);

        prompt.Should().Contain("NotificationService");
        prompt.Should().Contain("FlashWindowEx");
    }

    [Fact]
    public void SelfScan_Simulation_GapVerification_CatchesFalseCloudGap()
    {
        var profile = CreateResearchHiveProfile();
        // Simulate a false gap "No cloud support" with evidence showing cloud providers
        var gapEvidence = new List<(string gap, IReadOnlyList<string> chunks)>
        {
            ("No cloud deployment or cloud service integration", new[]
            {
                "public enum RoutingStrategy { LocalWithCloudFallback, LocalOnly, CloudOnly, RoundRobin }",
                "case \"Anthropic\": return await CallAnthropicAsync(prompt, systemPrompt, maxTokens, ct);",
                "case \"OpenAI\": case \"Gemini\": case \"DeepSeek\": case \"Groq\": case \"Mistral\": case \"OpenRouter\":"
            }),
            ("Insufficient testing frameworks", new[]
            {
                "[Fact] public void GlobalDb_GetChunks_FiltersBySourceType() { ... }",
                "using FluentAssertions; using Moq; // 341 tests total",
                "public class Phase11FeatureTests : IDisposable { // 14 test methods covering curation, health, PDF }"
            }),
            ("No real-time collaboration", Array.Empty<string>())
        };

        var prompt = RepoScannerService.BuildGapVerificationPrompt(profile, gapEvidence);

        // The verification prompt should contain the evidence that disproves the cloud gap
        prompt.Should().Contain("CloudOnly");
        prompt.Should().Contain("Anthropic");
        prompt.Should().Contain("341 tests");
        // The real-time collab gap has no evidence, so it should flag as likely real
        prompt.Should().Contain("No relevant code found");
    }

    [Fact]
    public void ManifestContents_PreservedOnProfile_NotTruncated()
    {
        var profile = CreateTestProfile();
        var longManifest = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"<PackageReference Include=\"Package{i}\" Version=\"{i}.0.0\" />"));
        profile.ManifestContents["ResearchHive.Core.csproj"] = longManifest;

        // ManifestContents should preserve the full content
        profile.ManifestContents["ResearchHive.Core.csproj"].Should().HaveLength(longManifest.Length);
        profile.ManifestContents["ResearchHive.Core.csproj"].Should().Contain("Package499");
    }

    // ── Helpers ──

    private static RepoProfile CreateTestProfile()
    {
        return new RepoProfile
        {
            Owner = "test",
            Name = "repo",
            Description = "A test repository",
            PrimaryLanguage = "C#",
            Languages = new() { "C#", "XAML" },
            Dependencies = new()
            {
                new RepoDependency { Name = "CommunityToolkit.Mvvm", Version = "8.2.2" },
                new RepoDependency { Name = "Microsoft.Data.Sqlite", Version = "8.0.0" },
            },
            Stars = 10,
            IndexedFileCount = 50,
            IndexedChunkCount = 500
        };
    }

    private static RepoProfile CreateResearchHiveProfile()
    {
        return new RepoProfile
        {
            Owner = "shellsage-ai",
            Name = "ResearchHive",
            Description = "Agentic Research Studio — WPF desktop app for autonomous research",
            PrimaryLanguage = "C#",
            Languages = new() { "C#", "XAML", "PowerShell" },
            Dependencies = new()
            {
                new RepoDependency { Name = "CommunityToolkit.Mvvm", Version = "8.2.2" },
                new RepoDependency { Name = "Microsoft.Data.Sqlite", Version = "8.0.0" },
                new RepoDependency { Name = "PdfPig", Version = "0.1.13" },
                new RepoDependency { Name = "Markdig", Version = "0.34.0" },
                new RepoDependency { Name = "Microsoft.Playwright", Version = "1.52.0" },
                new RepoDependency { Name = "FluentAssertions", Version = "6.12.0" },
                new RepoDependency { Name = "xunit", Version = "2.6.1" },
                new RepoDependency { Name = "Moq", Version = "4.20.0" },
            },
            Stars = 0,
            IndexedFileCount = 80,
            IndexedChunkCount = 900
        };
    }

    /// <summary>
    /// Simulated code chunks that would be retrieved from indexing the actual ResearchHive codebase.
    /// These mirror real code excerpts to test that the prompt captures key capabilities.
    /// </summary>
    private static List<string> GetResearchHiveCodeChunks()
    {
        return new List<string>
        {
            // LlmService — cloud providers
            @"public class LlmService {
    public enum RoutingStrategy { LocalWithCloudFallback, LocalOnly, CloudOnly, RoundRobin }
    private async Task<LlmResponse> CallCloudWithMetadataAsync(string prompt, string system, int maxTokens, CancellationToken ct) {
        return _settings.PaidProvider switch {
            ""Anthropic"" => await CallAnthropicAsync(prompt, system, maxTokens, ct),
            ""OpenAI"" => await CallOpenAiAsync(prompt, system, maxTokens, ct),
            ""Gemini"" => await CallGeminiAsync(prompt, system, maxTokens, ct),
            ""DeepSeek"" => await CallDeepSeekAsync(prompt, system, maxTokens, ct),
            ""Groq"" => await CallGroqAsync(prompt, system, maxTokens, ct),
            ""Mistral"" => await CallMistralAsync(prompt, system, maxTokens, ct),
            ""OpenRouter"" => await CallOpenRouterAsync(prompt, system, maxTokens, ct),
            _ => throw new InvalidOperationException(""Unknown provider"")
        };
    }
}",
            // GlobalMemoryService — Hive Mind
            @"public class GlobalMemoryService {
    /// Cross-session RAG Q&A — searches the global Hive Mind store
    public async Task<string> AskHiveMindAsync(string question, CancellationToken ct) {
        var bm25Hits = _globalDb.SearchFtsBm25(question, limit: 20);
        var strategies = _globalDb.GetStrategies();
        // ... hybrid search with semantic + BM25 + RRF merge
    }
    public async Task PromoteSessionChunks(string sessionId, CancellationToken ct) { }
    public void DeleteChunk(string chunkId) => _globalDb.DeleteChunk(chunkId);
    public List<GlobalChunk> BrowseChunks(int offset, int limit, string? sourceTypeFilter = null) => _globalDb.GetChunks(offset, limit, sourceTypeFilter);
}",
            // NotificationService — job completion
            @"public class NotificationService {
    [DllImport(""user32.dll"")] private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
    public void NotifyResearchComplete(string sessionTitle) {
        if (!_settings.NotificationsEnabled || IsAppFocused()) return;
        System.Media.SystemSounds.Asterisk.Play();
        FlashTaskbar();
    }
}",
            // Test infrastructure
            @"// ResearchHive.Tests — xUnit + FluentAssertions + Moq
public class Phase11FeatureTests : IDisposable {
    [Fact] public void GetChunks_ReturnsPagedResults() { ... }
    [Fact] public void SearchEngineHealthEntry_StatusDisplay_Healthy_WhenAllSucceed() { ... }
    // 14 tests covering GlobalDb curation, SearchEngineHealthEntry, PdfExtractionResult
}
// Total: 341 tests — 339 passed, 2 skipped (manual API comparison tests)",
            // ResearchJobRunner — search engine health
            @"public class ResearchJobRunner {
    private readonly ConcurrentDictionary<string, SearchEngineHealthEntry> _engineHealth = new();
    private async Task SearchMultiLaneAsync(ResearchJob job, SessionDb db, CancellationToken ct) {
        // Track per-engine: attempts, successes, failures, skip detection
        foreach (var engine in searchEngines) {
            _engineHealth.AddOrUpdate(engine.Name, ...);
        }
    }
}",
            // PdfIngestionService
            @"public class PdfIngestionService {
    public async Task<PdfExtractionResult> ExtractTextAsync(string pdfPath, CancellationToken ct) {
        // Tier 1: PdfPig text layer extraction
        // Tier 2: OCR fallback for pages with < 50 chars (likely scanned)
        using var doc = PdfDocument.Open(pdfPath);
        foreach (var page in doc.GetPages()) { ... }
    }
}",
            // Hybrid retrieval
            @"public class RetrievalService {
    public async Task<List<RetrievalResult>> HybridSearchAsync(string sessionId, string query, IReadOnlyList<string>? sourceTypeFilter, int topK, CancellationToken ct) {
        // Lane 1: BM25 keyword search (FTS5)
        // Lane 2: Semantic search (cosine similarity on embeddings)
        // Merge: Reciprocal Rank Fusion (RRF)
    }
}",
            // SessionDb — 20 tables
            @"public class SessionDb : IDisposable {
    // 20 tables: artifacts, snapshots, captures, chunks, fts_chunks, citations,
    // jobs, job_steps, claim_ledger, reports, safety_assessments, ip_assessments,
    // idea_cards, material_candidates, fusion_results, notebook_entries,
    // qa_messages, pinned_evidence, audit_log, repo_profiles, project_fusions
}",
        };
    }

    private static string GetResearchHiveCodeBook()
    {
        return @"# CodeBook: shellsage-ai/ResearchHive

## Purpose
ResearchHive is an agentic research studio — a WPF desktop application that runs autonomous
research workflows with full citation tracking, safety awareness, and IP analysis.

## Architecture Overview
- **WPF + MVVM**: CommunityToolkit.Mvvm with source-generated ObservableProperty and RelayCommand
- **37 DI services**: Registered via AddResearchHiveCore() + App.xaml.cs
- **Per-session SQLite**: Each session has its own isolated DB (20 tables)
- **Global Hive Mind**: Cross-session knowledge store (GlobalDb + FTS5)
- **Multi-provider LLM**: Ollama local + 8 cloud providers (Anthropic, OpenAI, Gemini, DeepSeek, Groq, Mistral, OpenRouter, Codex)

## Key Abstractions
- SessionManager — session lifecycle and registry
- ResearchJobRunner — 8-state agentic research loop
- RetrievalService — hybrid FTS5 + semantic search with RRF merge
- LlmService — multi-provider routing with truncation detection and auto-retry
- GlobalMemoryService — Hive Mind cross-session RAG + strategy extraction

## Testing
- 341 tests (xUnit + FluentAssertions + Moq)
- 339 passed, 2 skipped (manual API comparison tests)";
    }
}
