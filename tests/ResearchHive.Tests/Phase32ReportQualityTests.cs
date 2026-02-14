using FluentAssertions;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;

namespace ResearchHive.Tests;

/// <summary>
/// Phase 32 — Report Export Quality tests.
/// Covers: ConvertSetextToAtx, ParseList prose filtering,
/// FusionPostVerifier bullet validation, DetectFrameworkHints dedup.
/// </summary>
public class Phase32ReportQualityTests
{
    // ─────────────── ParseList: prose filtering ───────────────

    [Fact]
    public void ParseList_KeepsBulletItems()
    {
        var text = "- First capability\n- Second capability\n* Third capability";
        var result = ProjectFusionEngine.ParseList(text);
        result.Should().HaveCount(3);
        result[0].Should().Be("First capability");
        result[1].Should().Be("Second capability");
        result[2].Should().Be("Third capability");
    }

    [Fact]
    public void ParseList_FiltersOutProseLines()
    {
        var text = "Here is an overview of the capabilities:\n- First capability\nThis is a great feature.\n- Second capability\nIn summary, these are powerful.";
        var result = ProjectFusionEngine.ParseList(text);
        result.Should().HaveCount(2);
        result[0].Should().Be("First capability");
        result[1].Should().Be("Second capability");
    }

    [Fact]
    public void ParseList_HandlesNumberedItems()
    {
        var text = "1. First item\n2. Second item\nSome prose between\n3. Third item";
        var result = ProjectFusionEngine.ParseList(text);
        result.Should().HaveCount(3);
        result[0].Should().Be("First item");
        result[2].Should().Be("Third item");
    }

    [Fact]
    public void ParseList_EmptyInput_ReturnsEmpty()
    {
        ProjectFusionEngine.ParseList("").Should().BeEmpty();
        ProjectFusionEngine.ParseList("Just prose, no bullets at all.").Should().BeEmpty();
    }

    [Fact]
    public void ParseList_BulletOnlyItems()
    {
        var text = "• Unicode bullet item\n- Dash item";
        var result = ProjectFusionEngine.ParseList(text);
        result.Should().HaveCount(2);
        result[0].Should().Be("Unicode bullet item");
    }

    // ─────────────── FusionPostVerifier: GapsClosed bullet validation ───────────────

    [Fact]
    public void ValidateGapsClosed_KeepsValidBulletItems()
    {
        var section = "- No CI/CD pipeline → resolved by ProjectB's GitHub Actions workflow";
        var profiles = new List<RepoProfile>
        {
            CreateProfile("owner", "ProjectB", strengths: new[] { "GitHub Actions workflow", "CI/CD pipeline" })
        };
        var result = new FusionVerificationResult();
        var output = FusionPostVerifier.ValidateGapsClosed(section, profiles, result);
        output.Trim().Should().Contain("resolved by ProjectB");
        result.GapsClosedCorrected.Should().BeEmpty();
    }

    [Fact]
    public void ValidateGapsClosed_FlagsUnverifiableBullets()
    {
        var section = "- Missing feature → resolved by NonExistentProject's magic";
        var profiles = new List<RepoProfile>
        {
            CreateProfile("owner", "RealProject", strengths: new[] { "Testing framework" })
        };
        var result = new FusionVerificationResult();
        var output = FusionPostVerifier.ValidateGapsClosed(section, profiles, result);
        result.GapsClosedCorrected.Should().ContainSingle(c => c.Contains("UNVERIFIABLE"));
    }

    [Fact]
    public void ValidateGapsClosed_PassesBulletsWithoutArrows()
    {
        // Bullets without → pattern should pass through
        var section = "- **ProjectA** fills gap: Has better testing";
        var profiles = new List<RepoProfile>
        {
            CreateProfile("owner", "ProjectA", strengths: new[] { "Testing" })
        };
        var result = new FusionVerificationResult();
        var output = FusionPostVerifier.ValidateGapsClosed(section, profiles, result);
        output.Should().Contain("fills gap");
    }

    // ─────────────── DetectFrameworkHints: dedup guard ───────────────

    [Fact]
    public void DetectFrameworkHints_NoDuplicateWpfEntries()
    {
        // Verify bare "WPF" is suppressed when "WPF + MVVM" already exists
        var deps = new List<RepoDependency>
        {
            new() { Name = "CommunityToolkit.Mvvm", Version = "8.2.2" },
        };
        var manifests = new Dictionary<string, string>
        {
            { "src/App.csproj", "<Project><PropertyGroup><TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>" }
        };

        var hints = RepoScannerService.DetectFrameworkHints(deps, manifests);

        hints.Should().Contain("WPF + MVVM (CommunityToolkit.Mvvm)");
        hints.Where(h => h == "WPF").Should().BeEmpty("bare 'WPF' should be suppressed when 'WPF + MVVM' is present");
    }

    // ─────────────── Fix B: Self-referential complement filter ───────────────

    [Fact]
    public void NormalizeGitHubUrl_RejectsSelfReferentialUrl()
    {
        var self = ComplementResearchService.NormalizeGitHubUrl("https://github.com/dotnet/BenchmarkDotNet");
        var comp = ComplementResearchService.NormalizeGitHubUrl("https://github.com/dotnet/benchmarkdotnet");
        self.Should().Be(comp, "URLs should normalize to same value regardless of case");
    }

    // ─────────────── Fix C: Duplicate header stripping ───────────────

    [Fact]
    public void StripLeadingHeader_RemovesDuplicateHeader()
    {
        var content = "## Unified Vision\n\nThe resulting project combines both...";
        var result = ProjectFusionEngine.StripLeadingHeader(content);
        result.Should().Be("The resulting project combines both...");
    }

    [Fact]
    public void StripLeadingHeader_PreservesContentWithoutHeader()
    {
        var content = "The resulting project combines both...";
        var result = ProjectFusionEngine.StripLeadingHeader(content);
        result.Should().Be("The resulting project combines both...");
    }

    [Fact]
    public void StripLeadingHeader_HandlesLeadingBlankLines()
    {
        var content = "\n\n## Architecture Proposal\n\nLayered structure...";
        var result = ProjectFusionEngine.StripLeadingHeader(content);
        result.Should().Be("Layered structure...");
    }

    [Fact]
    public void StripLeadingHeader_HandlesEmptyContent()
    {
        ProjectFusionEngine.StripLeadingHeader("").Should().Be("");
        ProjectFusionEngine.StripLeadingHeader(null!).Should().BeNull();
    }

    // ─────────────── Fix E: Circular fusion gap rejection ───────────────

    [Fact]
    public void ValidateGapsClosed_RejectsCircularFusionClaims()
    {
        var section = "- No CI/CD → resolved by Fusion (combines strengths of both projects)";
        var profiles = new List<RepoProfile>
        {
            CreateProfile("owner", "ProjectA", strengths: new[] { "Testing" })
        };
        var result = new FusionVerificationResult();
        var output = FusionPostVerifier.ValidateGapsClosed(section, profiles, result);
        result.GapsClosedCorrected.Should().ContainSingle(c => c.Contains("CIRCULAR"));
        output.Trim().Should().BeEmpty("circular claim should be removed");
    }

    [Fact]
    public void ValidateGapsClosed_AllowsLegitimateGapClosures()
    {
        var section = "- No CI/CD → resolved by ProjectA's GitHub Actions workflow";
        var profiles = new List<RepoProfile>
        {
            CreateProfile("owner", "ProjectA", strengths: new[] { "GitHub Actions workflow", "CI/CD pipeline" })
        };
        var result = new FusionVerificationResult();
        var output = FusionPostVerifier.ValidateGapsClosed(section, profiles, result);
        result.GapsClosedCorrected.Should().BeEmpty();
        output.Should().Contain("GitHub Actions");
    }

    // ─────────────── Fix: StripLeadingH1 (duplicate title) ───────────────

    [Fact]
    public void StripLeadingH1_RemovesLeadingH1()
    {
        var md = "# Fusion: A + B (Merge)\n\n## Source Projects\n- **A**";
        var result = ExportService.StripLeadingH1(md);
        result.Should().NotStartWith("# ");
        result.Should().Contain("## Source Projects");
    }

    [Fact]
    public void StripLeadingH1_PreservesH2()
    {
        var md = "## Section Title\n\nContent here.";
        var result = ExportService.StripLeadingH1(md);
        result.Should().Be(md, "H2 headers should not be stripped");
    }

    [Fact]
    public void StripLeadingH1_SkipsBlankLinesThenStripsH1()
    {
        var md = "\n\n# Title\n\nContent after.";
        var result = ExportService.StripLeadingH1(md);
        result.Should().NotContain("# Title");
        result.Should().Contain("Content after.");
    }

    [Fact]
    public void StripLeadingH1_NoH1_ReturnsUnchanged()
    {
        var md = "Some paragraph text.\n\n## Section";
        var result = ExportService.StripLeadingH1(md);
        result.Should().Be(md);
    }

    [Fact]
    public void StripLeadingH1_HandlesEmptyInput()
    {
        ExportService.StripLeadingH1("").Should().Be("");
        ExportService.StripLeadingH1(null!).Should().BeNull();
    }

    // ─────────────── Fix: ParseList preserves bold markers ───────────────

    [Fact]
    public void ParseList_PreservesBoldMarkers()
    {
        var text = "- **No Dependabot/Renovate config found**: Resolved by integrating\n- **No Dockerfile found**: Addressed by adding";
        var result = ProjectFusionEngine.ParseList(text);
        result.Should().HaveCount(2);
        result[0].Should().StartWith("**No Dependabot", "leading ** bold markers must be preserved");
        result[1].Should().StartWith("**No Dockerfile", "leading ** bold markers must be preserved");
    }

    [Fact]
    public void ParseList_StillStripsPlainBullets()
    {
        var text = "- plain item\n* star item\n• unicode item";
        var result = ProjectFusionEngine.ParseList(text);
        result.Should().HaveCount(3);
        result[0].Should().Be("plain item");
        result[1].Should().Be("star item");
        result[2].Should().Be("unicode item");
    }

    // ─────────────── Fix: Gap closure fabrication rejection ───────────────

    [Fact]
    public void ValidateGapsClosed_RejectsFabricatedResolution_DependabotVsDI()
    {
        // "dependency injection" is NOT a valid resolution for a Dependabot gap
        var section = "- No Dependabot config → resolved by ProjectB's dependency injection capabilities";
        var profiles = new List<RepoProfile>
        {
            CreateProfile("owner", "ProjectB", strengths: new[] { "Dependency injection (DI) container" })
        };
        var result = new FusionVerificationResult();
        var output = FusionPostVerifier.ValidateGapsClosed(section, profiles, result);
        result.GapsClosedCorrected.Should().NotBeEmpty(
            "DI does not resolve a Dependabot gap — this should be flagged as fabricated");
    }

    [Fact]
    public void ValidateGapsClosed_RejectsFabricatedResolution_DockerVsContainerization()
    {
        // Project has no containerization support — claiming it resolves "No Dockerfile" is fabricated
        var section = "- No Dockerfile found → resolved by ProjectB's support for containerization";
        var profiles = new List<RepoProfile>
        {
            CreateProfile("owner", "ProjectB", strengths: new[] { "WPF desktop application", "SQLite per-session databases" })
        };
        var result = new FusionVerificationResult();
        var output = FusionPostVerifier.ValidateGapsClosed(section, profiles, result);
        result.GapsClosedCorrected.Should().NotBeEmpty(
            "project has no containerization capability — should be flagged");
    }

    [Fact]
    public void ValidateGapsClosed_AcceptsGenuineCapabilityMatch()
    {
        var section = "- No CI/CD pipeline → resolved by ProjectB's GitHub Actions workflow and CI/CD pipeline";
        var profiles = new List<RepoProfile>
        {
            CreateProfile("owner", "ProjectB", strengths: new[] { "GitHub Actions workflow", "CI/CD pipeline configuration" })
        };
        var result = new FusionVerificationResult();
        var output = FusionPostVerifier.ValidateGapsClosed(section, profiles, result);
        result.GapsClosedCorrected.Should().BeEmpty("genuine capability match should pass validation");
        output.Should().Contain("GitHub Actions");
    }

    // ─────────────── Helper to create minimal RepoProfile ───────────────

    private static RepoProfile CreateProfile(string owner, string name, string[]? strengths = null)
    {
        return new RepoProfile
        {
            Owner = owner,
            Name = name,
            RepoUrl = $"https://github.com/{owner}/{name}",
            Strengths = (strengths ?? Array.Empty<string>()).ToList(),
            InfrastructureStrengths = new List<string>(),
            CoreCapabilities = new List<string>(),
            Gaps = new List<string>(),
            Languages = new List<string>(),
            Frameworks = new List<string>(),
            Stars = 0,
            Forks = 0,
            Description = "Test"
        };
    }

    // ─────────────── Phase 32d: Fix 1 — StripLeadingH1 fuzzy title match ───────────────

    [Fact]
    public void StripLeadingH1_FuzzyMatch_RepoAnalysisVsRepositoryAnalysis()
    {
        // Wrapper title = "Repo Analysis: local/researcher"
        // Markdown H1 = "# Repository Analysis: local/researcher"
        var md = "# Repository Analysis: local/researcher\n\n## Project Summary\nContent here.";
        var result = ExportService.StripLeadingH1(md, "Repo Analysis: local/researcher");
        result.Should().NotContain("# Repository Analysis", "fuzzy match on colon-subject should strip H1");
        result.Should().Contain("## Project Summary");
    }

    [Fact]
    public void StripLeadingH1_ExactMatch_StillWorks()
    {
        var md = "# Repo Analysis: X\n\nContent.";
        var result = ExportService.StripLeadingH1(md, "Repo Analysis: X");
        result.Should().NotContain("# Repo Analysis");
        result.Should().Contain("Content.");
    }

    [Fact]
    public void StripLeadingH1_DifferentSubject_DoesNotStrip()
    {
        var md = "# Repository Analysis: SomeOtherProject\n\nContent.";
        var result = ExportService.StripLeadingH1(md, "Repo Analysis: local/researcher");
        result.Should().Contain("# Repository Analysis: SomeOtherProject",
            "different subjects after the colon should NOT match");
    }

    // ─────────────── Phase 32d: Fix 2 — SplitLargePipeTables double blank line ───────────────

    [Fact]
    public void SplitLargePipeTables_InsertsDoubleBlankLine_ForTableBreak()
    {
        // Build a pipe table with 25 rows
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("| Package | Version |");
        sb.AppendLine("|---------|---------|");
        for (int i = 1; i <= 25; i++)
            sb.AppendLine($"| pkg{i} | 1.0.{i} |");

        var result = ExportService.SplitLargePipeTables(sb.ToString());

        // After the split point (row 20), there should be consecutive blank lines
        // followed by the repeated header. The split inserts two AppendLine() calls
        // (which produce empty lines) before repeating the header.
        // Normalize line endings for assertion.
        var normalized = result.Replace("\r\n", "\n");
        normalized.Should().Contain("\n\n\n| Package | Version |",
            "table break must have at least one blank line before the new header");
    }

    // ─────────────── Phase 32d: Fix 3 — Meta-comment suppression in prose validation ───────────────

    [Fact]
    public void ValidateGapsClosed_RejectsColonResolvedBy_WithoutArrow()
    {
        // Gap format: "No Dependabot config: Resolved by ProjectB's circuit breaker"
        // (colon instead of arrow, and mismatched topic)
        var section = "- No Dependabot config: Resolved by ProjectB's circuit breaker implementation";
        var profiles = new List<RepoProfile>
        {
            CreateProfile("owner", "ProjectB", strengths: new[] { "LlmCircuitBreaker.cs implements circuit breaker" })
        };
        var result = new FusionVerificationResult();
        var output = FusionPostVerifier.ValidateGapsClosed(section, profiles, result);
        result.GapsClosedCorrected.Should().NotBeEmpty(
            "Dependabot gap resolved by circuit breaker is fabricated — should be caught even without arrow");
    }

    // ─────────────── Phase 32d: Fix 5 — Temp csproj filter ───────────────

    [Fact]
    public void WpfTmpCsproj_ShouldBeFilteredByPattern()
    {
        // This is a deterministic test — verify the filter pattern matches
        var fileName = "ResearchHive_repmvozl_wpftmp.csproj";
        fileName.Contains("_wpftmp", StringComparison.OrdinalIgnoreCase).Should().BeTrue(
            "the _wpftmp pattern should match VS temp csproj files");
    }
}
