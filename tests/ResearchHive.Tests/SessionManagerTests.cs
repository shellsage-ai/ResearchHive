using FluentAssertions;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;

namespace ResearchHive.Tests;

/// <summary>
/// Integration tests for SessionManager — creates real sessions on disk with temp directories.
/// </summary>
public class SessionManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly SessionManager _manager;

    public SessionManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RH_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _settings = new AppSettings { DataRootPath = _tempDir };
        _manager = new SessionManager(_settings);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void CreateSession_CreatesSessionWithFolderStructure()
    {
        var session = _manager.CreateSession("Test Session", "A test", DomainPack.GeneralResearch);

        session.Should().NotBeNull();
        session.Title.Should().Be("Test Session");
        session.Pack.Should().Be(DomainPack.GeneralResearch);
        session.Status.Should().Be(SessionStatus.Active);
        Directory.Exists(session.WorkspacePath).Should().BeTrue();
        Directory.Exists(Path.Combine(session.WorkspacePath, "Inbox")).Should().BeTrue();
        Directory.Exists(Path.Combine(session.WorkspacePath, "Artifacts")).Should().BeTrue();
        Directory.Exists(Path.Combine(session.WorkspacePath, "Snapshots")).Should().BeTrue();
    }

    [Fact]
    public void CreateSession_SavesSessionJson()
    {
        var session = _manager.CreateSession("JSON Test", "Testing JSON", DomainPack.MakerMaterials);
        var jsonPath = Path.Combine(session.WorkspacePath, "session.json");
        File.Exists(jsonPath).Should().BeTrue();
    }

    [Fact]
    public void GetSession_ReturnsCreatedSession()
    {
        var created = _manager.CreateSession("Get Test", "Desc", DomainPack.GeneralResearch);
        var retrieved = _manager.GetSession(created.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Title.Should().Be("Get Test");
    }

    [Fact]
    public void GetAllSessions_ReturnsAll()
    {
        _manager.CreateSession("Session 1", "Desc", DomainPack.GeneralResearch);
        _manager.CreateSession("Session 2", "Desc", DomainPack.MakerMaterials);

        var all = _manager.GetAllSessions();
        all.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void DeleteSession_RemovesSessionAndFolder()
    {
        var session = _manager.CreateSession("Delete Me", "Desc", DomainPack.GeneralResearch);
        var workspacePath = session.WorkspacePath;

        _manager.DeleteSession(session.Id);

        _manager.GetSession(session.Id).Should().BeNull();
        Directory.Exists(workspacePath).Should().BeFalse();
    }

    [Fact]
    public void GetSessionDb_ReturnsDatabase()
    {
        var session = _manager.CreateSession("DB Test", "Desc", DomainPack.GeneralResearch);
        var db = _manager.GetSessionDb(session.Id);
        db.Should().NotBeNull();
    }

    [Fact]
    public void SessionDb_SaveAndGetJob_Roundtrips()
    {
        var session = _manager.CreateSession("Job Test", "Desc", DomainPack.GeneralResearch);
        var db = _manager.GetSessionDb(session.Id);

        var job = new ResearchJob
        {
            SessionId = session.Id,
            Prompt = "Test research prompt",
            Type = JobType.Research,
            TargetSourceCount = 5,
            State = JobState.Planning
        };

        db.SaveJob(job);
        var retrieved = db.GetJob(job.Id);

        retrieved.Should().NotBeNull();
        retrieved!.Prompt.Should().Be("Test research prompt");
        retrieved.State.Should().Be(JobState.Planning);
        retrieved.TargetSourceCount.Should().Be(5);
    }

    [Fact]
    public void SessionDb_SaveAndGetReport_Roundtrips()
    {
        var session = _manager.CreateSession("Report Test", "Desc", DomainPack.GeneralResearch);
        var db = _manager.GetSessionDb(session.Id);

        var report = new Report
        {
            SessionId = session.Id,
            JobId = "job123",
            Title = "Test Report",
            ReportType = "final",
            Content = "# Test\n\nThis is a test report.",
            Format = "markdown"
        };

        db.SaveReport(report);
        var reports = db.GetReports();

        reports.Should().ContainSingle();
        reports[0].Title.Should().Be("Test Report");
        reports[0].Content.Should().Contain("# Test");
    }

    [Fact]
    public void SessionDb_SaveAndGetNotebookEntry_Roundtrips()
    {
        var session = _manager.CreateSession("Notebook Test", "Desc", DomainPack.GeneralResearch);
        var db = _manager.GetSessionDb(session.Id);

        var entry = new NotebookEntry
        {
            SessionId = session.Id,
            Title = "My Note",
            Content = "Important finding"
        };

        db.SaveNotebookEntry(entry);
        var entries = db.GetNotebookEntries();

        entries.Should().ContainSingle();
        entries[0].Title.Should().Be("My Note");
        entries[0].Content.Should().Be("Important finding");
    }

    [Fact]
    public void SessionDb_SaveAndGetSnapshot_Roundtrips()
    {
        var session = _manager.CreateSession("Snapshot Test", "Desc", DomainPack.GeneralResearch);
        var db = _manager.GetSessionDb(session.Id);

        var snapshot = new Snapshot
        {
            SessionId = session.Id,
            Url = "https://example.com",
            Title = "Example",
            HttpStatus = 200,
            IsBlocked = false
        };

        db.SaveSnapshot(snapshot);
        var snapshots = db.GetSnapshots();

        snapshots.Should().ContainSingle();
        snapshots[0].Url.Should().Be("https://example.com");
        snapshots[0].HttpStatus.Should().Be(200);
    }

    [Fact]
    public void SearchSessions_FindsByTitle()
    {
        _manager.CreateSession("Quantum Computing Research", "About quantum", DomainPack.GeneralResearch);
        _manager.CreateSession("Material Science Study", "About materials", DomainPack.MakerMaterials);

        var results = _manager.SearchSessions("quantum");
        results.Should().ContainSingle();
        results[0].Title.Should().Contain("Quantum");
    }

    [Fact]
    public void UpdateSession_PersistsChanges()
    {
        var session = _manager.CreateSession("Update Test", "Original", DomainPack.GeneralResearch);
        session.Status = SessionStatus.Completed;
        session.LastReportSummary = "All done";
        _manager.UpdateSession(session);

        var retrieved = _manager.GetSession(session.Id);
        retrieved!.Status.Should().Be(SessionStatus.Completed);
        retrieved.LastReportSummary.Should().Be("All done");
    }
}
