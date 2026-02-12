using FluentAssertions;
using ResearchHive.Core.Models;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for the data model types: Session, ResearchJob, JobState transitions,
/// JobProgressEventArgs, SourceHealthEntry, etc.
/// </summary>
public class ModelTests
{
    [Fact]
    public void Session_DefaultValues_AreCorrect()
    {
        var session = new Session();
        session.Id.Should().NotBeNullOrEmpty();
        session.Tags.Should().NotBeNull().And.BeEmpty();
        session.Status.Should().Be(SessionStatus.Active);
        session.CreatedUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ResearchJob_DefaultValues_AreCorrect()
    {
        var job = new ResearchJob();
        job.Id.Should().NotBeNullOrEmpty();
        job.State.Should().Be(JobState.Pending);
        job.AcquiredSourceIds.Should().NotBeNull().And.BeEmpty();
        job.Steps.Should().NotBeNull();
        job.ReplayEntries.Should().NotBeNull();
    }

    [Fact]
    public void JobState_HasAllExpectedValues()
    {
        var states = Enum.GetValues<JobState>();
        states.Should().Contain(JobState.Pending);
        states.Should().Contain(JobState.Planning);
        states.Should().Contain(JobState.Searching);
        states.Should().Contain(JobState.Acquiring);
        states.Should().Contain(JobState.Extracting);
        states.Should().Contain(JobState.Evaluating);
        states.Should().Contain(JobState.Drafting);
        states.Should().Contain(JobState.Validating);
        states.Should().Contain(JobState.Reporting);
        states.Should().Contain(JobState.Completed);
        states.Should().Contain(JobState.Failed);
        states.Should().Contain(JobState.Paused);
        states.Should().Contain(JobState.Cancelled);
    }

    [Fact]
    public void SourceFetchStatus_HasAllExpectedValues()
    {
        var statuses = Enum.GetValues<SourceFetchStatus>();
        statuses.Should().Contain(SourceFetchStatus.Success);
        statuses.Should().Contain(SourceFetchStatus.Blocked);
        statuses.Should().Contain(SourceFetchStatus.Timeout);
        statuses.Should().Contain(SourceFetchStatus.Paywall);
        statuses.Should().Contain(SourceFetchStatus.Error);
        statuses.Should().Contain(SourceFetchStatus.CircuitBroken);
    }

    [Fact]
    public void JobProgressEventArgs_DefaultValues()
    {
        var args = new JobProgressEventArgs();
        args.JobId.Should().BeEmpty();
        args.SourceHealth.Should().NotBeNull();
        args.CoverageScore.Should().Be(0);
        args.SourcesFound.Should().Be(0);
    }

    [Fact]
    public void SourceHealthEntry_CanBeCreated()
    {
        var entry = new SourceHealthEntry
        {
            Url = "https://example.com",
            Title = "Example",
            Status = SourceFetchStatus.Success,
            HttpStatus = 200,
            Reason = null
        };

        entry.Url.Should().Be("https://example.com");
        entry.Status.Should().Be(SourceFetchStatus.Success);
        entry.TimestampUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Report_DefaultValues()
    {
        var report = new Report();
        report.Id.Should().NotBeNullOrEmpty();
        report.Format.Should().Be("markdown");
        report.CreatedUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ClaimLedger_DefaultCollections()
    {
        var ledger = new ClaimLedger();
        ledger.CitationIds.Should().NotBeNull().And.BeEmpty();
        ledger.Claim.Should().BeEmpty();
        ledger.Support.Should().BeEmpty();
    }

    [Fact]
    public void IdeaCard_DefaultValues()
    {
        var card = new IdeaCard();
        card.Id.Should().NotBeNullOrEmpty();
        card.Risks.Should().NotBeNull();
        card.ScoreBreakdown.Should().NotBeNull();
    }

    [Fact]
    public void MaterialCandidate_DefaultValues()
    {
        var candidate = new MaterialCandidate();
        candidate.Id.Should().NotBeNullOrEmpty();
        candidate.Properties.Should().NotBeNull();
        candidate.TestChecklist.Should().NotBeNull();
    }

    [Fact]
    public void JobStep_TracksTimestamp()
    {
        var step = new JobStep
        {
            Action = "Search",
            Detail = "Searching for sources",
            StateAfter = JobState.Searching
        };

        step.TimestampUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Snapshot_BlockState()
    {
        var snap = new Snapshot
        {
            Url = "https://example.com",
            IsBlocked = true,
            BlockReason = "robots.txt"
        };

        snap.IsBlocked.Should().BeTrue();
        snap.BlockReason.Should().Be("robots.txt");
    }

    [Fact]
    public void FusionMode_HasAllModes()
    {
        var modes = Enum.GetValues<FusionMode>();
        modes.Should().Contain(FusionMode.Blend);
        modes.Should().Contain(FusionMode.CrossApply);
        modes.Should().Contain(FusionMode.Substitute);
        modes.Should().Contain(FusionMode.Optimize);
    }

    // ── Display Name Tests ──

    [Theory]
    [InlineData(DomainPack.GeneralResearch, "General Research")]
    [InlineData(DomainPack.HistoryPhilosophy, "History & Philosophy")]
    [InlineData(DomainPack.Math, "Math")]
    [InlineData(DomainPack.MakerMaterials, "Maker / Materials")]
    [InlineData(DomainPack.ChemistrySafe, "Chemistry (Safe)")]
    [InlineData(DomainPack.ProgrammingResearchIP, "Programming Research & IP")]
    public void DomainPack_ToDisplayName_ReturnsHumanReadable(DomainPack pack, string expected)
    {
        pack.ToDisplayName().Should().Be(expected);
    }

    [Theory]
    [InlineData(SessionStatus.Active, "Active")]
    [InlineData(SessionStatus.Paused, "Paused")]
    [InlineData(SessionStatus.Completed, "Completed")]
    [InlineData(SessionStatus.Archived, "Archived")]
    public void SessionStatus_ToDisplayName_ReturnsHumanReadable(SessionStatus status, string expected)
    {
        status.ToDisplayName().Should().Be(expected);
    }

    [Fact]
    public void DomainPack_AllValues_HaveDisplayNames()
    {
        foreach (var pack in Enum.GetValues<DomainPack>())
        {
            var display = pack.ToDisplayName();
            display.Should().NotBeNullOrEmpty();
            // Display names should contain spaces or special chars (not raw PascalCase)
            // except "Math" which is a single word
            if (pack != DomainPack.Math)
                display.Should().Contain(" ", $"{pack} display name should have spaces");
        }
    }
}
