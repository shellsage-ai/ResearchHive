using FluentAssertions;
using Microsoft.Data.Sqlite;
using ResearchHive.Core.Data;
using ResearchHive.Core.Models;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for SessionDb delete operations — ensures jobs, reports, snapshots,
/// notebook entries, idea cards, material candidates, fusion results, artifacts,
/// and captures can be cleanly removed without affecting unrelated data.
/// </summary>
public class SessionDbDeleteTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SessionDb _db;

    public SessionDbDeleteTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        _db = new SessionDb(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        // Clear SQLite connection pool so Windows releases the file lock
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best-effort cleanup */ }
    }

    // ---- Job delete tests ----

    [Fact]
    public void DeleteJob_RemovesJobAndRelated()
    {
        var job = CreateJob("job1");
        _db.SaveJob(job);
        _db.SaveJobStep(new JobStep { JobId = "job1", Action = "Test", Detail = "detail" });
        _db.SaveReport(new Report { SessionId = "s1", JobId = "job1", Title = "R1", Content = "c", FilePath = "f" });
        _db.SaveCitation(new Citation { SessionId = "s1", JobId = "job1", Excerpt = "e", Label = "[1]", SourceId = "src" });
        _db.SaveClaim(new ClaimLedger { JobId = "job1", Claim = "test claim" });
        _db.SaveIdeaCard(new IdeaCard { SessionId = "s1", JobId = "job1", Title = "Idea" });
        _db.SaveMaterialCandidate(new MaterialCandidate { SessionId = "s1", JobId = "job1", Name = "Steel" });
        _db.SaveFusionResult(new FusionResult { SessionId = "s1", JobId = "job1", Proposal = "fused" });

        _db.DeleteJob("job1");

        _db.GetJob("job1").Should().BeNull();
        _db.GetReports("job1").Should().BeEmpty();
        _db.GetJobSteps("job1").Should().BeEmpty();
        _db.GetCitations("job1").Should().BeEmpty();
        _db.GetIdeaCards("job1").Should().BeEmpty();
        _db.GetMaterialCandidates("job1").Should().BeEmpty();
        _db.GetFusionResults("job1").Should().BeEmpty();
    }

    [Fact]
    public void DeleteJob_DoesNotAffectOtherJobs()
    {
        var job1 = CreateJob("job1");
        var job2 = CreateJob("job2");
        _db.SaveJob(job1);
        _db.SaveJob(job2);
        _db.SaveReport(new Report { SessionId = "s1", JobId = "job1", Title = "R1", Content = "c", FilePath = "f1" });
        _db.SaveReport(new Report { SessionId = "s1", JobId = "job2", Title = "R2", Content = "c", FilePath = "f2" });

        _db.DeleteJob("job1");

        _db.GetJob("job2").Should().NotBeNull();
        _db.GetReports("job2").Should().HaveCount(1);
    }

    [Fact]
    public void DeleteJob_NonExistentId_DoesNotThrow()
    {
        var act = () => _db.DeleteJob("nonexistent");
        act.Should().NotThrow();
    }

    // ---- Report delete tests ----

    [Fact]
    public void DeleteReport_RemovesSingleReport()
    {
        var r1 = new Report { Id = "r1", SessionId = "s1", JobId = "j1", Title = "Report 1", Content = "c1", FilePath = "f1" };
        var r2 = new Report { Id = "r2", SessionId = "s1", JobId = "j1", Title = "Report 2", Content = "c2", FilePath = "f2" };
        _db.SaveReport(r1);
        _db.SaveReport(r2);

        _db.DeleteReport("r1");

        var reports = _db.GetReports();
        reports.Should().HaveCount(1);
        reports[0].Id.Should().Be("r2");
    }

    // ---- Snapshot delete tests ----

    [Fact]
    public void DeleteSnapshot_RemovesSnapshotAndChunks()
    {
        var snap = new Snapshot { Id = "snap1", SessionId = "s1", Url = "https://example.com", CanonicalUrl = "https://example.com",
            Title = "Example", BundlePath = "b", HtmlPath = "h", TextPath = "t", ScreenshotPath = "s", ContentHash = "hash" };
        _db.SaveSnapshot(snap);

        var chunk = new Chunk { Id = "c1", SessionId = "s1", SourceId = "snap1", SourceType = "snapshot", Text = "hello world" };
        _db.SaveChunk(chunk);

        _db.DeleteSnapshot("snap1");

        _db.GetSnapshot("snap1").Should().BeNull();
        _db.GetAllChunks().Should().BeEmpty();
    }

    [Fact]
    public void DeleteSnapshot_DoesNotAffectOtherSnapshots()
    {
        var snap1 = new Snapshot { Id = "snap1", SessionId = "s1", Url = "https://a.com", CanonicalUrl = "https://a.com",
            Title = "A", BundlePath = "b", HtmlPath = "h", TextPath = "t", ScreenshotPath = "s", ContentHash = "h1" };
        var snap2 = new Snapshot { Id = "snap2", SessionId = "s1", Url = "https://b.com", CanonicalUrl = "https://b.com",
            Title = "B", BundlePath = "b", HtmlPath = "h", TextPath = "t", ScreenshotPath = "s", ContentHash = "h2" };
        _db.SaveSnapshot(snap1);
        _db.SaveSnapshot(snap2);
        _db.SaveChunk(new Chunk { Id = "c1", SessionId = "s1", SourceId = "snap1", SourceType = "snapshot", Text = "text a" });
        _db.SaveChunk(new Chunk { Id = "c2", SessionId = "s1", SourceId = "snap2", SourceType = "snapshot", Text = "text b" });

        _db.DeleteSnapshot("snap1");

        _db.GetSnapshot("snap2").Should().NotBeNull();
        _db.GetAllChunks().Should().HaveCount(1);
        _db.GetAllChunks()[0].SourceId.Should().Be("snap2");
    }

    // ---- Notebook entry delete tests ----

    [Fact]
    public void DeleteNotebookEntry_RemovesCorrectEntry()
    {
        var n1 = new NotebookEntry { Id = "n1", SessionId = "s1", Title = "Note 1", Content = "c1" };
        var n2 = new NotebookEntry { Id = "n2", SessionId = "s1", Title = "Note 2", Content = "c2" };
        _db.SaveNotebookEntry(n1);
        _db.SaveNotebookEntry(n2);

        _db.DeleteNotebookEntry("n1");

        var entries = _db.GetNotebookEntries();
        entries.Should().HaveCount(1);
        entries[0].Id.Should().Be("n2");
    }

    // ---- Idea card delete tests ----

    [Fact]
    public void DeleteIdeaCard_RemovesCard()
    {
        var card = new IdeaCard { Id = "ic1", SessionId = "s1", JobId = "j1", Title = "Idea" };
        _db.SaveIdeaCard(card);

        _db.DeleteIdeaCard("ic1");

        _db.GetIdeaCards().Should().BeEmpty();
    }

    // ---- Material candidate delete tests ----

    [Fact]
    public void DeleteMaterialCandidate_RemovesCandidate()
    {
        var mc = new MaterialCandidate { Id = "mc1", SessionId = "s1", JobId = "j1", Name = "Aluminum" };
        _db.SaveMaterialCandidate(mc);

        _db.DeleteMaterialCandidate("mc1");

        _db.GetMaterialCandidates().Should().BeEmpty();
    }

    // ---- Fusion result delete tests ----

    [Fact]
    public void DeleteFusionResult_RemovesResult()
    {
        var fr = new FusionResult { Id = "fr1", SessionId = "s1", JobId = "j1", Proposal = "proposal" };
        _db.SaveFusionResult(fr);

        _db.DeleteFusionResult("fr1");

        _db.GetFusionResults().Should().BeEmpty();
    }

    // ---- Artifact delete tests ----

    [Fact]
    public void DeleteArtifact_RemovesArtifactAndChunks()
    {
        var art = new Artifact { Id = "art1", SessionId = "s1", OriginalName = "file.pdf",
            ContentType = "application/pdf", StorePath = "/tmp/x", ContentHash = "h" };
        _db.SaveArtifact(art);
        _db.SaveChunk(new Chunk { Id = "ac1", SessionId = "s1", SourceId = "art1", SourceType = "artifact", Text = "pdf text" });

        _db.DeleteArtifact("art1");

        _db.GetArtifacts().Should().BeEmpty();
        _db.GetAllChunks().Should().BeEmpty();
    }

    // ---- Capture delete tests ----

    [Fact]
    public void DeleteCapture_RemovesCaptureAndChunks()
    {
        var cap = new Capture { Id = "cap1", SessionId = "s1", ImagePath = "/img.png", SourceDescription = "test" };
        _db.SaveCapture(cap);
        _db.SaveChunk(new Chunk { Id = "cc1", SessionId = "s1", SourceId = "cap1", SourceType = "capture", Text = "ocr text" });

        _db.DeleteCapture("cap1");

        _db.GetCaptures().Should().BeEmpty();
        _db.GetAllChunks().Should().BeEmpty();
    }

    // ---- Cascade / integration tests ----

    [Fact]
    public void DeleteJob_CascadesCleansAllRelatedEntities()
    {
        // Full integration: create a complete job with all related data
        var job = CreateJob("fullJob");
        _db.SaveJob(job);
        _db.SaveJobStep(new JobStep { Id = "s1", JobId = "fullJob", Action = "Plan", Detail = "d" });
        _db.SaveJobStep(new JobStep { Id = "s2", JobId = "fullJob", Action = "Search", Detail = "d" });
        _db.SaveReport(new Report { Id = "r1", SessionId = "s1", JobId = "fullJob", Title = "Exec", Content = "c", FilePath = "f" });
        _db.SaveReport(new Report { Id = "r2", SessionId = "s1", JobId = "fullJob", Title = "Full", Content = "c", FilePath = "f" });
        _db.SaveReport(new Report { Id = "r3", SessionId = "s1", JobId = "fullJob", Title = "Activity", Content = "c", FilePath = "f" });
        _db.SaveCitation(new Citation { Id = "c1", SessionId = "s1", JobId = "fullJob", SourceId = "x", Excerpt = "e", Label = "[1]" });
        _db.SaveCitation(new Citation { Id = "c2", SessionId = "s1", JobId = "fullJob", SourceId = "y", Excerpt = "e", Label = "[2]" });
        _db.SaveClaim(new ClaimLedger { Id = "cl1", JobId = "fullJob", Claim = "claim1" });
        _db.SaveIdeaCard(new IdeaCard { Id = "i1", SessionId = "s1", JobId = "fullJob", Title = "Idea" });
        _db.SaveMaterialCandidate(new MaterialCandidate { Id = "m1", SessionId = "s1", JobId = "fullJob", Name = "Mat" });
        _db.SaveFusionResult(new FusionResult { Id = "f1", SessionId = "s1", JobId = "fullJob", Proposal = "p" });

        _db.DeleteJob("fullJob");

        _db.GetJob("fullJob").Should().BeNull();
        _db.GetJobs().Should().BeEmpty();
        _db.GetReports("fullJob").Should().BeEmpty();
        _db.GetJobSteps("fullJob").Should().BeEmpty();
        _db.GetCitations("fullJob").Should().BeEmpty();
        _db.GetIdeaCards("fullJob").Should().BeEmpty();
        _db.GetMaterialCandidates("fullJob").Should().BeEmpty();
        _db.GetFusionResults("fullJob").Should().BeEmpty();
    }

    [Fact]
    public void MultipleDeletes_LeaveDbClean()
    {
        // Save multiple entities across types
        _db.SaveNotebookEntry(new NotebookEntry { Id = "n1", SessionId = "s1", Title = "A", Content = "a" });
        _db.SaveNotebookEntry(new NotebookEntry { Id = "n2", SessionId = "s1", Title = "B", Content = "b" });
        _db.SaveNotebookEntry(new NotebookEntry { Id = "n3", SessionId = "s1", Title = "C", Content = "c" });

        _db.DeleteNotebookEntry("n1");
        _db.DeleteNotebookEntry("n2");
        _db.DeleteNotebookEntry("n3");

        _db.GetNotebookEntries().Should().BeEmpty();
    }

    private static ResearchJob CreateJob(string id) => new()
    {
        Id = id,
        SessionId = "s1",
        Prompt = "test prompt",
        State = JobState.Completed,
        Type = JobType.Research
    };
}
