using FluentAssertions;
using Microsoft.Data.Sqlite;
using ResearchHive.Core.Data;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;
using System.Text.Json;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for the 5 differentiating features:
/// 1. Cross-Session Search
/// 2. Citation Verification
/// 3. Contradiction Detection
/// 4. Research Comparison
/// 5. Incremental Research (GetClaimLedger + DB)
/// </summary>
public class NewFeatureTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SessionDb _db;

    public NewFeatureTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"newfeature_{Guid.NewGuid():N}.db");
        _db = new SessionDb(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    // ─────────────── GetClaimLedger ───────────────

    [Fact]
    public void GetClaimLedger_ReturnsEmpty_WhenNoClaimsExist()
    {
        var claims = _db.GetClaimLedger("nonexistent-job");
        claims.Should().BeEmpty();
    }

    [Fact]
    public void GetClaimLedger_PersistsAndRetrieves()
    {
        var claim = new ClaimLedger
        {
            Id = "cl1",
            JobId = "job1",
            Claim = "The speed of light is 299,792 km/s",
            Support = "strong",
            CitationIds = new List<string> { "cit1", "cit2" },
            Explanation = "Well established constant"
        };

        _db.SaveClaim(claim);

        var loaded = _db.GetClaimLedger("job1");
        loaded.Should().HaveCount(1);
        loaded[0].Id.Should().Be("cl1");
        loaded[0].JobId.Should().Be("job1");
        loaded[0].Claim.Should().Be("The speed of light is 299,792 km/s");
        loaded[0].Support.Should().Be("strong");
        loaded[0].CitationIds.Should().Contain("cit1");
        loaded[0].CitationIds.Should().Contain("cit2");
        loaded[0].Explanation.Should().Be("Well established constant");
    }

    [Fact]
    public void GetClaimLedger_MultipleClaims_FiltersByJobId()
    {
        _db.SaveClaim(new ClaimLedger { Id = "c1", JobId = "j1", Claim = "A", Support = "s", CitationIds = new() });
        _db.SaveClaim(new ClaimLedger { Id = "c2", JobId = "j1", Claim = "B", Support = "s", CitationIds = new() });
        _db.SaveClaim(new ClaimLedger { Id = "c3", JobId = "j2", Claim = "C", Support = "s", CitationIds = new() });

        _db.GetClaimLedger("j1").Should().HaveCount(2);
        _db.GetClaimLedger("j2").Should().HaveCount(1);
        _db.GetClaimLedger("j3").Should().BeEmpty();
    }

    [Fact]
    public void GetClaimLedger_HandlesNullExplanation()
    {
        _db.SaveClaim(new ClaimLedger
        {
            Id = "c1", JobId = "j1", Claim = "Test", Support = "medium",
            CitationIds = new List<string> { "a" },
            Explanation = null
        });

        var loaded = _db.GetClaimLedger("j1");
        loaded.Should().HaveCount(1);
        loaded[0].Explanation.Should().BeNullOrEmpty();
    }

    // ─────────────── Citation Verification (Summarize) ───────────────

    [Fact]
    public void VerificationSummary_StatusLabel_ShowsPercentage()
    {
        var verifications = new List<CitationVerification>
        {
            new() { Status = VerificationStatus.Verified, Confidence = 0.95 },
            new() { Status = VerificationStatus.Verified, Confidence = 0.87 },
            new() { Status = VerificationStatus.Plausible, Confidence = 0.5 },
            new() { Status = VerificationStatus.Unverified, Confidence = 0.1 },
        };

        var summary = CitationVerificationService.Summarize(verifications);
        summary.Total.Should().Be(4);
        summary.Verified.Should().Be(2);
        summary.Plausible.Should().Be(1);
        summary.Unverified.Should().Be(1);
        summary.NoSource.Should().Be(0);
        summary.StatusLabel.Should().Contain("2/4 verified");
    }

    [Fact]
    public void VerificationSummary_EmptyList_ShowsNoVerifications()
    {
        var summary = CitationVerificationService.Summarize(new List<CitationVerification>());
        summary.Total.Should().Be(0);
        summary.StatusLabel.Should().Contain("No citations");
    }

    // ─────────────── Contradiction Detection (ScoreContradiction) ───────────────

    [Fact]
    public void Contradiction_TypeEnum_HasExpectedValues()
    {
        Enum.GetValues<ContradictionType>().Should().HaveCount(3);
        ContradictionType.DirectContradiction.Should().BeDefined();
        ContradictionType.NumericDisagreement.Should().BeDefined();
        ContradictionType.InterpretationDifference.Should().BeDefined();
    }

    [Fact]
    public void Contradiction_Model_HasRequiredProperties()
    {
        var c = new Contradiction
        {
            ChunkA = new Chunk { Text = "The temperature is 100°C" },
            ChunkB = new Chunk { Text = "The temperature is 200°C" },
            TopicSimilarity = 0.95,
            ContradictionScore = 0.85,
            Type = ContradictionType.NumericDisagreement,
            Summary = "Temperature values differ",
            LlmVerified = false
        };

        c.ChunkA.Text.Should().Contain("100");
        c.ChunkB.Text.Should().Contain("200");
        c.ContradictionScore.Should().BeGreaterThan(0.8);
        c.Type.Should().Be(ContradictionType.NumericDisagreement);
    }

    // ─────────────── Research Comparison ───────────────

    [Fact]
    public void ResearchComparison_Model_HasAllFields()
    {
        var comparison = new ResearchComparison
        {
            SessionIdA = "s1",
            SessionIdB = "s2",
            SourceCountA = 5,
            SourceCountB = 10,
            CitationCountA = 12,
            CitationCountB = 20,
            SummaryMarkdown = "# Comparison\n| Metric | A | B |"
        };

        comparison.SourceCountA.Should().Be(5);
        comparison.SourceCountB.Should().Be(10);
        comparison.SummaryMarkdown.Should().Contain("Comparison");
    }

    [Fact]
    public void SectionDiff_Model_TracksOverlap()
    {
        var diff = new SectionDiff
        {
            Heading = "Introduction",
            WordCountA = 200,
            WordCountB = 350,
            ContentOverlap = 0.45,
            InA = true,
            InB = true
        };

        diff.Heading.Should().Be("Introduction");
        diff.WordCountA.Should().Be(200);
        diff.WordCountB.Should().Be(350);
        diff.ContentOverlap.Should().BeApproximately(0.45, 0.001);
    }

    // ─────────────── Cross-Session Search ───────────────

    [Fact]
    public void CrossSessionResult_Model_HasSessionContext()
    {
        var result = new CrossSessionResult
        {
            SessionId = "sess-1",
            SessionTitle = "Climate Research",
            DomainPack = DomainPack.GeneralResearch,
            Chunk = new Chunk { Text = "Global temperatures rose by 1.1°C" },
            SourceUrl = "https://example.com/climate"
        };

        result.SessionTitle.Should().Be("Climate Research");
        result.Chunk.Text.Should().Contain("1.1°C");
        result.SourceUrl.Should().StartWith("https://");
    }

    [Fact]
    public void GlobalStats_Model_AggregatesCorrectly()
    {
        var stats = new GlobalStats
        {
            TotalSessions = 10,
            TotalEvidence = 500,
            TotalReports = 25,
            TotalSnapshots = 200,
            SessionsByDomain = new Dictionary<string, int>
            {
                ["GeneralResearch"] = 6,
                ["ProgrammingResearchIP"] = 4
            }
        };

        stats.TotalSessions.Should().Be(10);
        stats.SessionsByDomain.Should().HaveCount(2);
        stats.SessionsByDomain["GeneralResearch"].Should().Be(6);
    }

    // ─────────────── Citation Extraction Regression ───────────────

    [Fact]
    public void VerificationStatus_Enum_HasAllValues()
    {
        Enum.GetValues<VerificationStatus>().Should().HaveCount(4);
        VerificationStatus.Verified.Should().BeDefined();
        VerificationStatus.Plausible.Should().BeDefined();
        VerificationStatus.Unverified.Should().BeDefined();
        VerificationStatus.NoSource.Should().BeDefined();
    }

    [Fact]
    public void CitationVerification_Model_CapturesAllFields()
    {
        var v = new CitationVerification
        {
            CitationLabel = "1",
            ClaimText = "The sky is blue according to Rayleigh scattering",
            SourceExcerpt = "...Rayleigh scattering causes the sky to appear blue...",
            SourceId = "src1",
            Status = VerificationStatus.Verified,
            Confidence = 0.92,
            Method = "text-overlap",
            Note = "High keyword match"
        };

        v.CitationLabel.Should().Be("1");
        v.ClaimText.Should().Contain("Rayleigh");
        v.Status.Should().Be(VerificationStatus.Verified);
        v.Confidence.Should().BeGreaterThan(0.9);
    }

    // ─────────────── DB Integration: Claim Ledger Round-Trip ───────────────

    [Fact]
    public void SaveClaim_UpdatesExistingRecord()
    {
        var claim = new ClaimLedger
        {
            Id = "cl1", JobId = "j1", Claim = "Version 1",
            Support = "weak", CitationIds = new List<string> { "c1" }
        };
        _db.SaveClaim(claim);

        claim.Claim = "Version 2";
        claim.Support = "strong";
        _db.SaveClaim(claim);

        var loaded = _db.GetClaimLedger("j1");
        loaded.Should().HaveCount(1);
        loaded[0].Claim.Should().Be("Version 2");
        loaded[0].Support.Should().Be("strong");
    }

    [Fact]
    public void ClaimLedger_CitationIds_SerializesAsJson()
    {
        var ids = new List<string> { "a", "b", "c" };
        _db.SaveClaim(new ClaimLedger
        {
            Id = "cl1", JobId = "j1", Claim = "T",
            Support = "s", CitationIds = ids
        });

        var loaded = _db.GetClaimLedger("j1");
        loaded[0].CitationIds.Should().BeEquivalentTo(new[] { "a", "b", "c" });
    }

    // ─────────────── CrossSessionReportResult Model ───────────────

    [Fact]
    public void CrossSessionReportResult_Model_Complete()
    {
        var r = new CrossSessionReportResult
        {
            SessionId = "s1",
            SessionTitle = "Test",
            ReportTitle = "Summary",
            ReportType = "full",
            Snippet = "Key findings include...",
            CreatedUtc = DateTime.UtcNow
        };

        r.ReportType.Should().Be("full");
        r.Snippet.Should().Contain("Key findings");
    }
}
