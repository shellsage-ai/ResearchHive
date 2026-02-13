using FluentAssertions;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;

namespace ResearchHive.Tests;

/// <summary>
/// Phase 31 tests: FusionPostVerifier (deterministic checks) and
/// PostScanVerifier strength-grounding additions.
/// </summary>
public class Phase31VerifierTests
{
    // ═══════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════

    private static RepoProfile CreateProfile(string owner, string name,
        List<string>? strengths = null, List<string>? infraStrengths = null,
        List<string>? gaps = null, List<string>? frameworks = null,
        List<RepoDependency>? deps = null, List<string>? languages = null,
        List<string>? coreCapabilities = null)
    {
        return new RepoProfile
        {
            Owner = owner,
            Name = name,
            PrimaryLanguage = "C#",
            Strengths = strengths ?? new List<string>(),
            InfrastructureStrengths = infraStrengths ?? new List<string>(),
            Gaps = gaps ?? new List<string>(),
            Frameworks = frameworks ?? new List<string>(),
            Dependencies = deps ?? new List<RepoDependency>(),
            Languages = languages ?? new List<string> { "C#" },
            CoreCapabilities = coreCapabilities ?? new List<string>(),
            ComplementSuggestions = new List<ComplementProject>(),
        };
    }

    private static RepoDependency Dep(string name) => new() { Name = name, Version = "1.0" };

    private static RepoFactSheet CreateFactSheet(
        List<CapabilityFingerprint>? proven = null,
        List<CapabilityFingerprint>? absent = null)
    {
        return new RepoFactSheet
        {
            ProvenCapabilities = proven ?? new List<CapabilityFingerprint>(),
            ConfirmedAbsent = absent ?? new List<CapabilityFingerprint>(),
        };
    }

    // ═══════════════════════════════════════════════════════════
    //  FusionPostVerifier — BuildProjectVocabulary
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void BuildProjectVocabulary_PopulatesTechnologiesFromDepsFrameworksLanguages()
    {
        var profile = CreateProfile("acme", "widgets",
            frameworks: new List<string> { "WPF", "CommunityToolkit.Mvvm" },
            deps: new List<RepoDependency> { Dep("Markdig"), Dep("Microsoft.Data.Sqlite") },
            languages: new List<string> { "C#", "XAML" });

        var vocab = FusionPostVerifier.BuildProjectVocabulary(profile);

        vocab.ProjectName.Should().Be("acme/widgets");
        vocab.ProjectNameShort.Should().Be("widgets");
        vocab.Technologies.Should().Contain("markdig");
        vocab.Technologies.Should().Contain("microsoft.data.sqlite");
        vocab.Technologies.Should().Contain("wpf");
        vocab.Technologies.Should().Contain("communitytoolkit.mvvm");
        vocab.Technologies.Should().Contain("c#");
        vocab.Technologies.Should().Contain("xaml");
    }

    [Fact]
    public void BuildProjectVocabulary_PopulatesFeaturesFromStrengthsAndCapabilities()
    {
        var profile = CreateProfile("acme", "widgets",
            strengths: new List<string> { "Circuit breaker pattern" },
            infraStrengths: new List<string> { "604 xUnit tests" },
            coreCapabilities: new List<string> { "LLM-powered analysis" });

        var vocab = FusionPostVerifier.BuildProjectVocabulary(profile);

        vocab.Features.Should().Contain("circuit breaker pattern");
        vocab.Features.Should().Contain("604 xunit tests");
        vocab.Features.Should().Contain("llm-powered analysis");
    }

    [Fact]
    public void BuildProjectVocabulary_PopulatesGaps()
    {
        var profile = CreateProfile("acme", "widgets",
            gaps: new List<string> { "No CI/CD pipeline", "Missing rate limiting" });

        var vocab = FusionPostVerifier.BuildProjectVocabulary(profile);

        vocab.Gaps.Should().HaveCount(2);
        vocab.Gaps.Should().Contain("no ci/cd pipeline");
    }

    // ═══════════════════════════════════════════════════════════
    //  FusionPostVerifier — ValidateTechStackTable
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ValidateTechStackTable_RemovesFabricatedTechnologies()
    {
        var techStackSection = @"| Technology | Purpose | Source |
|---|---|---|
| WPF | UI Framework | acme/widgets |
| Redis | Caching | acme/widgets |
| Markdig | Markdown parsing | acme/widgets |";

        var profile = CreateProfile("acme", "widgets",
            frameworks: new List<string> { "WPF" },
            deps: new List<RepoDependency> { Dep("Markdig") });

        var vocabs = new List<ProjectVocabulary> { FusionPostVerifier.BuildProjectVocabulary(profile) };
        var result = new FusionVerificationResult();

        var corrected = FusionPostVerifier.ValidateTechStackTable(techStackSection, vocabs, result);

        // Redis should be removed — not in dependencies
        result.TechStackRowsRemoved.Should().HaveCount(1);
        result.TechStackRowsRemoved[0].Should().Contain("Redis");
        corrected.Should().Contain("WPF");
        corrected.Should().Contain("Markdig");
        corrected.Should().NotContain("| Redis");
    }

    [Fact]
    public void ValidateTechStackTable_KeepsAllValidTechnologies()
    {
        var techStackSection = @"| Technology | Purpose | Source |
|---|---|---|
| WPF | UI Framework | acme/widgets |
| Markdig | Markdown | acme/widgets |";

        var profile = CreateProfile("acme", "widgets",
            frameworks: new List<string> { "WPF" },
            deps: new List<RepoDependency> { Dep("Markdig") });

        var vocabs = new List<ProjectVocabulary> { FusionPostVerifier.BuildProjectVocabulary(profile) };
        var result = new FusionVerificationResult();

        FusionPostVerifier.ValidateTechStackTable(techStackSection, vocabs, result);

        result.TechStackRowsRemoved.Should().BeEmpty();
    }

    [Fact]
    public void ValidateTechStackTable_FuzzyMatchHandlesDotsAndDashes()
    {
        var techStackSection = @"| Technology | Purpose | Source |
|---|---|---|
| Community Toolkit MVVM | MVVM helpers | acme/widgets |";

        var profile = CreateProfile("acme", "widgets",
            deps: new List<RepoDependency> { Dep("CommunityToolkit.Mvvm") });

        var vocabs = new List<ProjectVocabulary> { FusionPostVerifier.BuildProjectVocabulary(profile) };
        var result = new FusionVerificationResult();

        FusionPostVerifier.ValidateTechStackTable(techStackSection, vocabs, result);

        // Fuzzy match should keep this row
        result.TechStackRowsRemoved.Should().BeEmpty();
    }

    [Fact]
    public void ValidateTechStackTable_PreservesHeadersAndSeparators()
    {
        var techStackSection = @"| Technology | Purpose | Source |
|---|---|---|
| FakeLib | Something | acme/widgets |";

        var profile = CreateProfile("acme", "widgets");
        var vocabs = new List<ProjectVocabulary> { FusionPostVerifier.BuildProjectVocabulary(profile) };
        var result = new FusionVerificationResult();

        var corrected = FusionPostVerifier.ValidateTechStackTable(techStackSection, vocabs, result);

        // Headers and separators should be preserved
        corrected.Should().Contain("| Technology | Purpose | Source |");
        corrected.Should().Contain("|---|---|---|");
    }

    // ═══════════════════════════════════════════════════════════
    //  FusionPostVerifier — ValidateFeatureMatrix
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ValidateFeatureMatrix_ReAttributesMisattributedFeatures()
    {
        var featureSection = @"| Feature | Source |
|---|---|
| Circuit breaker pattern | wrong-project/other |";

        var profileA = CreateProfile("acme", "widgets",
            strengths: new List<string> { "Circuit breaker pattern via LlmCircuitBreaker" });
        var profileB = CreateProfile("wrong-project", "other");

        var vocabs = new List<ProjectVocabulary>
        {
            FusionPostVerifier.BuildProjectVocabulary(profileA),
            FusionPostVerifier.BuildProjectVocabulary(profileB),
        };
        var result = new FusionVerificationResult();

        var corrected = FusionPostVerifier.ValidateFeatureMatrix(featureSection, vocabs, result);

        result.FeaturesCorrected.Should().ContainSingle()
            .Which.Should().Contain("RE-ATTRIBUTED");
        corrected.Should().Contain("acme/widgets");
    }

    [Fact]
    public void ValidateFeatureMatrix_KeepsCorrectAttributions()
    {
        var featureSection = @"| Feature | Source |
|---|---|
| Circuit breaker pattern | acme/widgets |";

        var profile = CreateProfile("acme", "widgets",
            strengths: new List<string> { "Circuit breaker pattern" });

        var vocabs = new List<ProjectVocabulary> { FusionPostVerifier.BuildProjectVocabulary(profile) };
        var result = new FusionVerificationResult();

        FusionPostVerifier.ValidateFeatureMatrix(featureSection, vocabs, result);

        result.FeaturesCorrected.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════
    //  FusionPostVerifier — ValidateGapsClosed
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ValidateGapsClosed_RemovesFabricatedGapClosure()
    {
        var gapsSection = @"| Gap | Project with gap | Resolved by |
|---|---|---|
| No caching layer | acme/widgets | other/project |";

        // other/project does NOT have caching as a strength
        var profileA = CreateProfile("acme", "widgets",
            gaps: new List<string> { "No caching layer" });
        var profileB = CreateProfile("other", "project",
            strengths: new List<string> { "Good documentation" });

        var profiles = new List<RepoProfile> { profileA, profileB };
        var result = new FusionVerificationResult();

        var corrected = FusionPostVerifier.ValidateGapsClosed(gapsSection, profiles, result);

        result.GapsClosedCorrected.Should().ContainSingle()
            .Which.Should().Contain("FABRICATED");
        corrected.Should().NotContain("No caching layer");
    }

    [Fact]
    public void ValidateGapsClosed_KeepsValidGapClosure()
    {
        var gapsSection = @"| Gap | Project with gap | Resolved by |
|---|---|---|
| No test coverage | acme/widgets | other/project |";

        var profileA = CreateProfile("acme", "widgets",
            gaps: new List<string> { "No test coverage" });
        var profileB = CreateProfile("other", "project",
            strengths: new List<string> { "Comprehensive test coverage with 200 tests" });

        var profiles = new List<RepoProfile> { profileA, profileB };
        var result = new FusionVerificationResult();

        FusionPostVerifier.ValidateGapsClosed(gapsSection, profiles, result);

        result.GapsClosedCorrected.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════
    //  FusionPostVerifier — ValidateProvenance
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ValidateProvenance_RemovesOrphanedEntries()
    {
        var provenance = @"DECISION: Use WPF for UI | FROM: acme/widgets
DECISION: Add Redis caching | FROM: unknown-project";

        var profile = CreateProfile("acme", "widgets");
        var vocabs = new List<ProjectVocabulary> { FusionPostVerifier.BuildProjectVocabulary(profile) };
        var result = new FusionVerificationResult();

        var corrected = FusionPostVerifier.ValidateProvenance(provenance, vocabs, result);

        result.ProvenanceCorrected.Should().ContainSingle()
            .Which.Should().Contain("ORPHANED");
        corrected.Should().Contain("Use WPF for UI");
        corrected.Should().NotContain("unknown-project");
    }

    [Fact]
    public void ValidateProvenance_KeepsValidEntries()
    {
        var provenance = @"DECISION: Use WPF for UI | FROM: acme/widgets
DECISION: Add Markdig | FROM: acme/widgets";

        var profile = CreateProfile("acme", "widgets");
        var vocabs = new List<ProjectVocabulary> { FusionPostVerifier.BuildProjectVocabulary(profile) };
        var result = new FusionVerificationResult();

        FusionPostVerifier.ValidateProvenance(provenance, vocabs, result);

        result.ProvenanceCorrected.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════
    //  FusionPostVerifier — VerifyAsync (integration, no LLM)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task VerifyAsync_IntegrationDetectsMultipleIssues()
    {
        var verifier = new FusionPostVerifier(); // no LLM — prose validation skipped

        var profileA = CreateProfile("acme", "widgets",
            strengths: new List<string> { "Circuit breaker pattern" },
            gaps: new List<string> { "No caching" },
            frameworks: new List<string> { "WPF" },
            deps: new List<RepoDependency> { Dep("Markdig") });

        var profileB = CreateProfile("other", "lib",
            strengths: new List<string> { "Fast logging" },
            deps: new List<RepoDependency> { Dep("Serilog") });

        var sections = new Dictionary<string, string>
        {
            ["TECH_STACK"] = @"| Technology | Purpose | Source |
|---|---|---|
| WPF | UI | acme/widgets |
| Redis | Cache | acme/widgets |",
            ["FEATURE_MATRIX"] = "FEATURE: Logging | SOURCE: acme/widgets",
            ["GAPS_CLOSED"] = @"| Gap | Has Gap | Resolved by |
|---|---|---|
| No caching | acme/widgets | other/lib |",
            ["PROVENANCE"] = "DECISION: Use WPF | FROM: ghost-project",
        };

        var result = await verifier.VerifyAsync(sections, new[] { profileA, profileB });

        // Redis fabricated (not in deps)
        result.TechStackRowsRemoved.Should().NotBeEmpty();
        // "ghost-project" is unknown
        result.ProvenanceCorrected.Should().NotBeEmpty();
        // other/lib doesn't have caching strength
        result.GapsClosedCorrected.Should().NotBeEmpty();
        result.TotalCorrections.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task VerifyAsync_EmptySectionsDoNotCrash()
    {
        var verifier = new FusionPostVerifier();
        var sections = new Dictionary<string, string>();
        var profiles = new List<RepoProfile> { CreateProfile("a", "b") };

        var result = await verifier.VerifyAsync(sections, profiles);

        result.TotalCorrections.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════
    //  FusionVerificationResult — Summary
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void FusionVerificationResult_Summary_ShowsAllCategories()
    {
        var result = new FusionVerificationResult
        {
            TechStackRowsRemoved = { "A" },
            FeaturesCorrected = { "B" },
            GapsClosedCorrected = { "C" },
            ProvenanceCorrected = { "D" },
            ProseCorrections = { "E" },
        };

        result.TotalCorrections.Should().Be(5);
        result.Summary.Should().Contain("fabricated tech");
        result.Summary.Should().Contain("features re-attributed");
        result.Summary.Should().Contain("gap claims");
        result.Summary.Should().Contain("provenance");
        result.Summary.Should().Contain("prose errors");
    }

    [Fact]
    public void FusionVerificationResult_Summary_NoCorrectionsSaysClean()
    {
        var result = new FusionVerificationResult();
        result.Summary.Should().Be("No corrections needed");
    }

    // ═══════════════════════════════════════════════════════════
    //  PostScanVerifier — DeflateDescription
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Robust error handling", "Error handling")]
    [InlineData("Comprehensive test suite", "Test suite")]
    [InlineData("Powerful query engine", "Query engine")]
    [InlineData("Advanced caching layer", "Caching layer")]
    [InlineData("Sophisticated retry logic", "Retry logic")]
    [InlineData("Seamless integration", "Integration")]
    public void DeflateDescription_RemovesVagueAdjectives(string input, string expected)
    {
        var result = PostScanVerifier.DeflateDescription(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Leverages SQLite for storage", "Uses SQLite for storage")]
    [InlineData("Utilizes Markdig for rendering", "Uses Markdig for rendering")]
    [InlineData("Facilitates data export", "Provides data export")]
    [InlineData("Enables research workflows", "Provides research workflows")]
    public void DeflateDescription_ReplacesInflatedVerbs(string input, string expected)
    {
        var result = PostScanVerifier.DeflateDescription(input);
        result.Should().Be(expected);
    }

    [Fact]
    public void DeflateDescription_HighlyScalableBecomeScalable()
    {
        PostScanVerifier.DeflateDescription("Highly scalable architecture")
            .Should().Be("Scalable architecture");
    }

    [Fact]
    public void DeflateDescription_NoOpWhenAlreadyGround()
    {
        var input = "SQLite-based session storage via SessionDb";
        PostScanVerifier.DeflateDescription(input).Should().Be(input);
    }

    [Fact]
    public void DeflateDescription_CleansDoubleSpaces()
    {
        // After removing "robust " there's potential for double space
        PostScanVerifier.DeflateDescription("A robust system design")
            .Should().NotContain("  ");
    }

    // ═══════════════════════════════════════════════════════════
    //  PostScanVerifier — GroundStrengthDescriptions
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void GroundStrengthDescriptions_OverstatementReplacedWithProvenCapability()
    {
        var profile = CreateProfile("acme", "widgets",
            strengths: new List<string> { "Parallel RAG retrieval across multiple sources" });
        var factSheet = CreateFactSheet(proven: new List<CapabilityFingerprint>
        {
            new() { Capability = "RAG retrieval pipeline", Evidence = "RetrievalService.cs" },
        });
        var result = new VerificationResult();

        PostScanVerifier.GroundStrengthDescriptions(profile, factSheet, result);

        result.StrengthsGrounded.Should().ContainSingle()
            .Which.Should().StartWith("GROUNDED:");
        profile.Strengths[0].Should().Contain("RAG retrieval pipeline");
        profile.Strengths[0].Should().Contain("RetrievalService.cs");
    }

    [Fact]
    public void GroundStrengthDescriptions_DeflatesWhenNoMatchingCapability()
    {
        var profile = CreateProfile("acme", "widgets",
            strengths: new List<string> { "Robust error handling" });
        var factSheet = CreateFactSheet(); // no proven capabilities
        var result = new VerificationResult();

        PostScanVerifier.GroundStrengthDescriptions(profile, factSheet, result);

        result.StrengthsGrounded.Should().ContainSingle()
            .Which.Should().StartWith("DEFLATED:");
        profile.Strengths[0].Should().Be("Error handling");
    }

    [Fact]
    public void GroundStrengthDescriptions_NoOpWhenStrengthsAlreadyFactual()
    {
        var profile = CreateProfile("acme", "widgets",
            strengths: new List<string> { "SQLite-based session storage via SessionDb" });
        var factSheet = CreateFactSheet();
        var result = new VerificationResult();

        PostScanVerifier.GroundStrengthDescriptions(profile, factSheet, result);

        result.StrengthsGrounded.Should().BeEmpty();
        profile.Strengths[0].Should().Be("SQLite-based session storage via SessionDb");
    }

    [Fact]
    public void GroundStrengthDescriptions_GroundsBothStrengthsAndInfra()
    {
        var profile = CreateProfile("acme", "widgets",
            strengths: new List<string> { "Robust data processing" },
            infraStrengths: new List<string> { "Comprehensive security measures" });
        var factSheet = CreateFactSheet();
        var result = new VerificationResult();

        PostScanVerifier.GroundStrengthDescriptions(profile, factSheet, result);

        // Both lists should be deflated
        profile.Strengths[0].Should().Be("Data processing");
        profile.InfrastructureStrengths[0].Should().Be("Security measures");
        result.StrengthsGrounded.Should().HaveCount(2);
    }

    [Fact]
    public void GroundStrengthDescriptions_StructuredLoggingPatternMatches()
    {
        var profile = CreateProfile("acme", "widgets",
            infraStrengths: new List<string> { "Structured logging with Serilog" });
        var factSheet = CreateFactSheet(proven: new List<CapabilityFingerprint>
        {
            new() { Capability = "Structured logging", Evidence = "LogService.cs — ILogger<T> injection" },
        });
        var result = new VerificationResult();

        PostScanVerifier.GroundStrengthDescriptions(profile, factSheet, result);

        result.StrengthsGrounded.Should().ContainSingle()
            .Which.Should().StartWith("GROUNDED:");
        profile.InfrastructureStrengths[0].Should().Contain("verified");
    }
}
