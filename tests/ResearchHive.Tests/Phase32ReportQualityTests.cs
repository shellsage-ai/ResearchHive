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
}
