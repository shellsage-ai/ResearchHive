using FluentAssertions;
using Microsoft.Data.Sqlite;
using ResearchHive.Core.Data;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;
using System.Text.Json;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for Repo Intelligence & Project Fusion feature:
/// - RepoProfile and ProjectFusionArtifact model serialization
/// - SessionDb CRUD for repo_profiles and project_fusions tables
/// - RepoScannerService URL parsing
/// - ProjectFusionEngine section parsing helpers
/// - Domain pack enum and tab visibility
/// </summary>
public class RepoIntelligenceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SessionDb _db;

    public RepoIntelligenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"repointel_{Guid.NewGuid():N}.db");
        _db = new SessionDb(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    // ─────────────── Model Tests ───────────────

    [Fact]
    public void RepoProfile_DefaultValues_AreInitialized()
    {
        var profile = new RepoProfile();
        profile.Id.Should().NotBeNullOrEmpty();
        profile.Languages.Should().BeEmpty();
        profile.Frameworks.Should().BeEmpty();
        profile.Dependencies.Should().BeEmpty();
        profile.Topics.Should().BeEmpty();
        profile.Strengths.Should().BeEmpty();
        profile.Gaps.Should().BeEmpty();
        profile.ComplementSuggestions.Should().BeEmpty();
    }

    [Fact]
    public void ProjectFusionArtifact_DefaultValues_AreInitialized()
    {
        var artifact = new ProjectFusionArtifact();
        artifact.Id.Should().NotBeNullOrEmpty();
        artifact.Inputs.Should().BeEmpty();
        artifact.FeatureMatrix.Should().BeEmpty();
        artifact.GapsClosed.Should().BeEmpty();
        artifact.NewGaps.Should().BeEmpty();
        artifact.ProvenanceMap.Should().BeEmpty();
    }

    [Fact]
    public void DomainPack_RepoIntelligence_Exists()
    {
        var pack = DomainPack.RepoIntelligence;
        pack.ToDisplayName().Should().Be("Repo Intelligence & Fusion");
    }

    [Fact]
    public void JobType_RepoAnalysis_And_ProjectFusion_Exist()
    {
        Enum.IsDefined(typeof(JobType), JobType.RepoAnalysis).Should().BeTrue();
        Enum.IsDefined(typeof(JobType), JobType.ProjectFusion).Should().BeTrue();
    }

    [Fact]
    public void ProjectFusionGoal_AllValues_Exist()
    {
        var goals = Enum.GetValues<ProjectFusionGoal>();
        goals.Should().Contain(ProjectFusionGoal.Merge);
        goals.Should().Contain(ProjectFusionGoal.Extend);
        goals.Should().Contain(ProjectFusionGoal.Compare);
        goals.Should().Contain(ProjectFusionGoal.Architect);
    }

    // ─────────────── RepoProfile DB CRUD ───────────────

    [Fact]
    public void SaveRepoProfile_PersistsAndRetrieves()
    {
        var profile = new RepoProfile
        {
            Id = "rp001",
            SessionId = "sess1",
            RepoUrl = "https://github.com/owner/repo",
            Owner = "owner",
            Name = "repo",
            Description = "A test repo",
            PrimaryLanguage = "C#",
            Languages = new List<string> { "C#", "TypeScript" },
            Frameworks = new List<string> { ".NET 8", "React" },
            Dependencies = new List<RepoDependency>
            {
                new() { Name = "Newtonsoft.Json", Version = "13.0.3", License = "MIT", ManifestFile = "project.csproj" }
            },
            Stars = 1234,
            Forks = 56,
            OpenIssues = 7,
            Topics = new List<string> { "dotnet", "research" },
            LastCommitUtc = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            ReadmeContent = "# Repo\nA cool project",
            Strengths = new List<string> { "Fast", "Well documented" },
            Gaps = new List<string> { "No tests", "Missing CI/CD" },
            ComplementSuggestions = new List<ComplementProject>
            {
                new() { Name = "TestHelper", Url = "https://github.com/x/test", Purpose = "testing", WhatItAdds = "unit tests", Category = "Testing", License = "MIT", Maturity = "Mature" }
            }
        };

        _db.SaveRepoProfile(profile);

        var loaded = _db.GetRepoProfiles();
        loaded.Should().HaveCount(1);

        var p = loaded[0];
        p.Id.Should().Be("rp001");
        p.SessionId.Should().Be("sess1");
        p.Owner.Should().Be("owner");
        p.Name.Should().Be("repo");
        p.PrimaryLanguage.Should().Be("C#");
        p.Languages.Should().Contain("TypeScript");
        p.Frameworks.Should().Contain(".NET 8");
        p.Dependencies.Should().HaveCount(1);
        p.Dependencies[0].Name.Should().Be("Newtonsoft.Json");
        p.Stars.Should().Be(1234);
        p.Forks.Should().Be(56);
        p.Topics.Should().Contain("research");
        p.Strengths.Should().Contain("Fast");
        p.Gaps.Should().Contain("Missing CI/CD");
        p.ComplementSuggestions.Should().HaveCount(1);
        p.ComplementSuggestions[0].Name.Should().Be("TestHelper");
    }

    [Fact]
    public void DeleteRepoProfile_RemovesRecord()
    {
        var profile = new RepoProfile
        {
            Id = "rp_del",
            SessionId = "sess1",
            Owner = "o",
            Name = "n"
        };
        _db.SaveRepoProfile(profile);
        _db.GetRepoProfiles().Should().HaveCount(1);

        _db.DeleteRepoProfile("rp_del");
        _db.GetRepoProfiles().Should().BeEmpty();
    }

    [Fact]
    public void SaveRepoProfile_Upserts_OnConflict()
    {
        var profile = new RepoProfile
        {
            Id = "rp_up",
            SessionId = "sess1",
            Owner = "owner",
            Name = "repo",
            Stars = 10
        };
        _db.SaveRepoProfile(profile);
        profile.Stars = 999;
        _db.SaveRepoProfile(profile);

        var loaded = _db.GetRepoProfiles();
        loaded.Should().HaveCount(1);
        loaded[0].Stars.Should().Be(999);
    }

    // ─────────────── ProjectFusion DB CRUD ───────────────

    [Fact]
    public void SaveProjectFusion_PersistsAndRetrieves()
    {
        var artifact = new ProjectFusionArtifact
        {
            Id = "pf001",
            SessionId = "sess1",
            JobId = "job1",
            Title = "Fusion: A + B (Merge)",
            InputSummary = "A, B",
            Inputs = new List<ProjectFusionInput>
            {
                new() { Id = "rp1", Type = FusionInputType.RepoProfile, Title = "Repo A" }
            },
            Goal = ProjectFusionGoal.Merge,
            UnifiedVision = "A unified vision text",
            ArchitectureProposal = "Microservices with API gateway",
            TechStackDecisions = ".NET 8, PostgreSQL, Redis",
            FeatureMatrix = new Dictionary<string, string>
            {
                { "Auth", "Repo A" }, { "Caching", "Repo B" }
            },
            GapsClosed = new List<string> { "No auth (now from A)" },
            NewGaps = new List<string> { "Monitoring not addressed" },
            ProvenanceMap = new Dictionary<string, string>
            {
                { "Use microservices", "Repo A architecture" }
            }
        };

        _db.SaveProjectFusion(artifact);

        var loaded = _db.GetProjectFusions();
        loaded.Should().HaveCount(1);

        var f = loaded[0];
        f.Id.Should().Be("pf001");
        f.Title.Should().Be("Fusion: A + B (Merge)");
        f.Goal.Should().Be(ProjectFusionGoal.Merge);
        f.FeatureMatrix.Should().ContainKey("Auth");
        f.FeatureMatrix["Auth"].Should().Be("Repo A");
        f.GapsClosed.Should().Contain("No auth (now from A)");
        f.NewGaps.Should().Contain("Monitoring not addressed");
        f.ProvenanceMap.Should().ContainKey("Use microservices");
        f.Inputs.Should().HaveCount(1);
        f.Inputs[0].Type.Should().Be(FusionInputType.RepoProfile);
    }

    [Fact]
    public void DeleteProjectFusion_RemovesRecord()
    {
        var artifact = new ProjectFusionArtifact
        {
            Id = "pf_del",
            SessionId = "sess1",
            Title = "Test"
        };
        _db.SaveProjectFusion(artifact);
        _db.GetProjectFusions().Should().HaveCount(1);

        _db.DeleteProjectFusion("pf_del");
        _db.GetProjectFusions().Should().BeEmpty();
    }

    [Fact]
    public void SaveProjectFusion_Upserts_OnConflict()
    {
        var artifact = new ProjectFusionArtifact
        {
            Id = "pf_up",
            SessionId = "sess1",
            Title = "v1"
        };
        _db.SaveProjectFusion(artifact);
        artifact.Title = "v2";
        _db.SaveProjectFusion(artifact);

        var loaded = _db.GetProjectFusions();
        loaded.Should().HaveCount(1);
        loaded[0].Title.Should().Be("v2");
    }

    // ─────────────── RepoScannerService URL Parsing ───────────────

    [Theory]
    [InlineData("https://github.com/owner/repo", "owner", "repo")]
    [InlineData("https://github.com/owner/repo/", "owner", "repo")]
    [InlineData("https://github.com/owner/repo.git", "owner", "repo")]
    [InlineData("https://github.com/Microsoft/vscode", "Microsoft", "vscode")]
    public void ParseRepoUrl_ExtractsOwnerAndRepo(string url, string expectedOwner, string expectedRepo)
    {
        var (owner, repo) = RepoScannerService.ParseRepoUrl(url);
        owner.Should().Be(expectedOwner);
        repo.Should().Be(expectedRepo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("https://example.com")]
    public void ParseRepoUrl_Throws_ForInvalidUrls(string url)
    {
        Action act = () => RepoScannerService.ParseRepoUrl(url);
        act.Should().Throw<Exception>();
    }

    // ─────────────── JSON Serialization Round-Trip ───────────────

    [Fact]
    public void RepoDependency_SerializeDeserialize_RoundTrip()
    {
        var dep = new RepoDependency
        {
            Name = "Newtonsoft.Json",
            Version = "13.0.3",
            License = "MIT",
            ManifestFile = "package.json"
        };
        var json = JsonSerializer.Serialize(dep);
        var loaded = JsonSerializer.Deserialize<RepoDependency>(json);
        loaded!.Name.Should().Be("Newtonsoft.Json");
        loaded.Version.Should().Be("13.0.3");
    }

    [Fact]
    public void ComplementProject_SerializeDeserialize_RoundTrip()
    {
        var cp = new ComplementProject
        {
            Name = "TestLib",
            Url = "https://github.com/x/testlib",
            Purpose = "Unit testing",
            WhatItAdds = "test framework",
            Category = "Testing",
            License = "MIT",
            Maturity = "Stable"
        };
        var json = JsonSerializer.Serialize(cp);
        var loaded = JsonSerializer.Deserialize<ComplementProject>(json);
        loaded!.Name.Should().Be("TestLib");
        loaded.Maturity.Should().Be("Stable");
    }

    [Fact]
    public void ProjectFusionInput_SerializeDeserialize_RoundTrip()
    {
        var input = new ProjectFusionInput
        {
            Id = "rp1",
            Type = FusionInputType.RepoProfile,
            Title = "My Repo"
        };
        var json = JsonSerializer.Serialize(input);
        var loaded = JsonSerializer.Deserialize<ProjectFusionInput>(json);
        loaded!.Type.Should().Be(FusionInputType.RepoProfile);
        loaded.Title.Should().Be("My Repo");
    }

    // ─────────────── Multiple Profiles in Same Session ───────────────

    [Fact]
    public void MultipleProfiles_SaveAndRetrieve()
    {
        for (int i = 0; i < 5; i++)
        {
            _db.SaveRepoProfile(new RepoProfile
            {
                Id = $"rp_{i}",
                SessionId = "sess1",
                Owner = $"owner{i}",
                Name = $"repo{i}",
                Stars = i * 100
            });
        }

        var loaded = _db.GetRepoProfiles();
        loaded.Should().HaveCount(5);
        loaded.Select(p => p.Stars).Should().BeEquivalentTo(new[] { 0, 100, 200, 300, 400 });
    }

    // ─────────────── FusionInputType Enum Coverage ───────────────

    [Fact]
    public void FusionInputType_HasExpectedValues()
    {
        Enum.GetValues<FusionInputType>().Should().HaveCount(2);
        Enum.IsDefined(typeof(FusionInputType), FusionInputType.RepoProfile).Should().BeTrue();
        Enum.IsDefined(typeof(FusionInputType), FusionInputType.FusionArtifact).Should().BeTrue();
    }

    // ─────────────── Empty Collections Serialization ───────────────

    [Fact]
    public void RepoProfile_WithEmptyCollections_PersistsCleanly()
    {
        var profile = new RepoProfile
        {
            Id = "rp_empty",
            SessionId = "sess1",
            Owner = "o",
            Name = "n"
        };
        _db.SaveRepoProfile(profile);

        var loaded = _db.GetRepoProfiles()[0];
        loaded.Languages.Should().BeEmpty();
        loaded.Frameworks.Should().BeEmpty();
        loaded.Dependencies.Should().BeEmpty();
        loaded.Topics.Should().BeEmpty();
        loaded.Strengths.Should().BeEmpty();
        loaded.Gaps.Should().BeEmpty();
        loaded.ComplementSuggestions.Should().BeEmpty();
        loaded.TopLevelEntries.Should().BeEmpty();
    }

    [Fact]
    public void ProjectFusion_WithEmptyCollections_PersistsCleanly()
    {
        var artifact = new ProjectFusionArtifact
        {
            Id = "pf_empty",
            SessionId = "sess1",
            Title = "Empty"
        };
        _db.SaveProjectFusion(artifact);

        var loaded = _db.GetProjectFusions()[0];
        loaded.Inputs.Should().BeEmpty();
        loaded.FeatureMatrix.Should().BeEmpty();
        loaded.GapsClosed.Should().BeEmpty();
        loaded.NewGaps.Should().BeEmpty();
        loaded.ProvenanceMap.Should().BeEmpty();
    }

    // ─────────────── RepoEntry & TopLevelEntries ───────────────

    [Fact]
    public void RepoEntry_DisplayIcon_ReturnsCorrectIcon()
    {
        new RepoEntry { Name = "src", Type = "dir" }.DisplayIcon.Should().Be("📁");
        new RepoEntry { Name = "README.md", Type = "file" }.DisplayIcon.Should().Be("📄");
    }

    [Fact]
    public void RepoProfile_TopLevelEntries_PersistsAndRetrieves()
    {
        var profile = new RepoProfile
        {
            Id = "rp_proof",
            SessionId = "sess1",
            Owner = "microsoft",
            Name = "semantic-kernel",
            LastCommitUtc = DateTime.UtcNow.AddDays(-3),
            TopLevelEntries = new List<RepoEntry>
            {
                new() { Name = "src", Type = "dir" },
                new() { Name = "README.md", Type = "file" },
                new() { Name = "package.json", Type = "file" }
            }
        };
        _db.SaveRepoProfile(profile);

        var loaded = _db.GetRepoProfiles()[0];
        loaded.TopLevelEntries.Should().HaveCount(3);
        loaded.TopLevelEntries[0].Name.Should().Be("src");
        loaded.TopLevelEntries[0].Type.Should().Be("dir");
        loaded.TopLevelEntries[1].Name.Should().Be("README.md");
        loaded.TopLevelEntries[2].Name.Should().Be("package.json");
    }

    [Fact]
    public void RepoProfile_EmptyTopLevelEntries_PersistsCleanly()
    {
        var profile = new RepoProfile
        {
            Id = "rp_no_entries",
            SessionId = "sess1",
            Owner = "o",
            Name = "n"
        };
        _db.SaveRepoProfile(profile);

        var loaded = _db.GetRepoProfiles()[0];
        loaded.TopLevelEntries.Should().BeEmpty();
    }

    [Fact]
    public void RepoScannerService_ParseRepoUrl_Works()
    {
        var (owner, repo) = RepoScannerService.ParseRepoUrl("https://github.com/microsoft/semantic-kernel");
        owner.Should().Be("microsoft");
        repo.Should().Be("semantic-kernel");

        var (o2, r2) = RepoScannerService.ParseRepoUrl("https://github.com/dotnet/runtime.git");
        o2.Should().Be("dotnet");
        r2.Should().Be("runtime");
    }
}
