using FluentAssertions;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for ExportService — verifies ZIP, HTML, and research packet exports.
/// Uses real temp directories and SessionManager instances.
/// </summary>
public class ExportServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _outputDir;
    private readonly AppSettings _settings;
    private readonly SessionManager _manager;
    private readonly ExportService _export;

    public ExportServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RH_Export_{Guid.NewGuid():N}");
        _outputDir = Path.Combine(_tempDir, "output");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_outputDir);

        _settings = new AppSettings { DataRootPath = _tempDir };
        _manager = new SessionManager(_settings);
        _export = new ExportService(_manager);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private Session CreateSessionWithContent(bool withEvidence = false)
    {
        var session = _manager.CreateSession("Export Test", "Test desc", DomainPack.GeneralResearch);
        var db = _manager.GetSessionDb(session.Id);

        db.SaveReport(new Report
        {
            SessionId = session.Id,
            JobId = "job1",
            Title = "Final Report",
            ReportType = "final",
            Content = "# Summary\n\nThis is **bold** and *italic* research.",
            Format = "markdown"
        });

        db.SaveReport(new Report
        {
            SessionId = session.Id,
            JobId = "job1",
            Title = "Activity Report - Does it work?",
            ReportType = "activity",
            Content = "## Activity\n\nStep 1: searched. Step 2: found results.",
            Format = "markdown"
        });

        // Snapshot with evidence files on disk
        if (withEvidence)
        {
            var snapshotDir = Path.Combine(session.WorkspacePath, "Snapshots", "snap001");
            Directory.CreateDirectory(snapshotDir);
            File.WriteAllText(Path.Combine(snapshotDir, "page.html"), "<html><body>Source content</body></html>");
            File.WriteAllText(Path.Combine(snapshotDir, "page.txt"), "Source content in plain text");

            db.SaveSnapshot(new Snapshot
            {
                Id = "snap001",
                SessionId = session.Id,
                Url = "https://example.com/article",
                Title = "Example Article",
                HttpStatus = 200,
                IsBlocked = false,
                BundlePath = snapshotDir,
                HtmlPath = Path.Combine(snapshotDir, "page.html"),
                TextPath = Path.Combine(snapshotDir, "page.txt")
            });
        }
        else
        {
            db.SaveSnapshot(new Snapshot
            {
                SessionId = session.Id,
                Url = "https://example.com/article",
                Title = "Example Article",
                HttpStatus = 200,
                IsBlocked = false
            });
        }

        // Blocked snapshot — should NOT appear in evidence
        db.SaveSnapshot(new Snapshot
        {
            SessionId = session.Id,
            Url = "https://blocked.com/page",
            Title = "Blocked Page",
            HttpStatus = 0,
            IsBlocked = true,
            BlockReason = "Access denied"
        });

        db.SaveNotebookEntry(new NotebookEntry
        {
            SessionId = session.Id,
            Title = "Research Note",
            Content = "Key finding: the hypothesis is supported."
        });

        return session;
    }

    // ── ZIP Export ──

    [Fact]
    public void ExportSessionToZip_CreatesZipFile()
    {
        var session = _manager.CreateSession("Zip Test", "Zipping", DomainPack.GeneralResearch);
        File.WriteAllText(Path.Combine(session.WorkspacePath, "test.txt"), "test content");

        var zipPath = _export.ExportSessionToZip(session.Id, _outputDir);

        File.Exists(zipPath).Should().BeTrue();
        zipPath.Should().EndWith(".zip");
        new FileInfo(zipPath).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ExportSessionToZip_ThrowsForMissingSession()
    {
        var act = () => _export.ExportSessionToZip("nonexistent", _outputDir);
        act.Should().Throw<InvalidOperationException>();
    }

    // ── Single Report Export ──

    [Fact]
    public async Task ExportReportAsync_CreatesMarkdownFiles()
    {
        var session = CreateSessionWithContent();
        var db = _manager.GetSessionDb(session.Id);
        var reports = db.GetReports();

        await _export.ExportReportAsync(session.Id, reports[0].JobId, _outputDir);

        var mdFiles = Directory.GetFiles(_outputDir, "*.md");
        mdFiles.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExportReportAsHtmlAsync_CreatesHtmlFile()
    {
        var session = CreateSessionWithContent();
        var db = _manager.GetSessionDb(session.Id);
        var report = db.GetReports().First(r => r.Title == "Final Report");

        var htmlPath = await _export.ExportReportAsHtmlAsync(session.Id, report.Id, _outputDir);

        File.Exists(htmlPath).Should().BeTrue();
        htmlPath.Should().EndWith(".html");

        var content = await File.ReadAllTextAsync(htmlPath);
        content.Should().Contain("<!DOCTYPE html>");
        content.Should().Contain("Final Report");
        content.Should().Contain("bold");
        content.Should().Contain("ResearchHive");
    }

    [Fact]
    public async Task ExportReportAsHtmlAsync_ThrowsForMissingReport()
    {
        var session = CreateSessionWithContent();
        var act = () => _export.ExportReportAsHtmlAsync(session.Id, "nonexistent", _outputDir);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Research Packet ──

    [Fact]
    public async Task ExportPacket_ReturnsFolderPath()
    {
        var session = CreateSessionWithContent(withEvidence: true);
        var result = await _export.ExportResearchPacketAsync(session.Id, _outputDir);

        Directory.Exists(result).Should().BeTrue("method should return the folder path, not the zip");
        result.Should().NotEndWith(".zip");
    }

    [Fact]
    public async Task ExportPacket_AlsoCreatesZip()
    {
        var session = CreateSessionWithContent(withEvidence: true);
        var folderPath = await _export.ExportResearchPacketAsync(session.Id, _outputDir);

        var zipPath = folderPath + ".zip";
        File.Exists(zipPath).Should().BeTrue("a .zip file should also be created for sharing");
    }

    [Fact]
    public async Task ExportPacket_IndexHtml_IsValidHtml()
    {
        var session = CreateSessionWithContent(withEvidence: true);
        var folder = await _export.ExportResearchPacketAsync(session.Id, _outputDir);

        var indexPath = Path.Combine(folder, "index.html");
        File.Exists(indexPath).Should().BeTrue();

        var html = await File.ReadAllTextAsync(indexPath);
        html.Should().Contain("<!DOCTYPE html>");
        html.Should().Contain("<html");
        html.Should().Contain("</html>");
        html.Should().Contain("<style>");
    }

    [Fact]
    public async Task ExportPacket_IndexHtml_ShowsDisplayName()
    {
        var session = CreateSessionWithContent(withEvidence: true);
        var folder = await _export.ExportResearchPacketAsync(session.Id, _outputDir);

        var html = await File.ReadAllTextAsync(Path.Combine(folder, "index.html"));
        html.Should().Contain("General Research", "DomainPack should use display name, not raw enum");
        html.Should().NotContain("GeneralResearch", "raw enum name should not appear");
    }

    [Fact]
    public async Task ExportPacket_IndexHtml_ContainsReportLinks()
    {
        var session = CreateSessionWithContent(withEvidence: true);
        var folder = await _export.ExportResearchPacketAsync(session.Id, _outputDir);

        var html = await File.ReadAllTextAsync(Path.Combine(folder, "index.html"));
        html.Should().Contain("Final Report");
        html.Should().Contain("Activity Report");
        html.Should().Contain("href=\"reports/");
    }

    [Fact]
    public async Task ExportPacket_IndexHtml_ReportLinksMatchFiles()
    {
        var session = CreateSessionWithContent(withEvidence: true);
        var folder = await _export.ExportResearchPacketAsync(session.Id, _outputDir);

        var reportFiles = Directory.GetFiles(Path.Combine(folder, "reports"), "*.html");
        reportFiles.Should().HaveCount(2, "both reports should be exported");

        var html = await File.ReadAllTextAsync(Path.Combine(folder, "index.html"));
        foreach (var reportFile in reportFiles)
        {
            var fileName = Path.GetFileName(reportFile);
            // The href should contain the URL-encoded version of the filename
            var encodedName = Uri.EscapeDataString(fileName);
            html.Should().Contain($"reports/{encodedName}",
                $"index.html should link to report '{fileName}'");
        }
    }

    [Fact]
    public async Task ExportPacket_ReportFileNames_HaveNoDoubleUnderscores()
    {
        var session = CreateSessionWithContent(withEvidence: true);
        var folder = await _export.ExportResearchPacketAsync(session.Id, _outputDir);

        var reportFiles = Directory.GetFiles(Path.Combine(folder, "reports"), "*.html");
        foreach (var file in reportFiles)
        {
            var name = Path.GetFileName(file);
            name.Should().NotContain("__",
                "sanitized filenames should not have double underscores");
        }
    }

    [Fact]
    public async Task ExportPacket_EvidenceFolder_ContainsSnapshotFiles()
    {
        var session = CreateSessionWithContent(withEvidence: true);
        var folder = await _export.ExportResearchPacketAsync(session.Id, _outputDir);

        var evidenceDir = Path.Combine(folder, "evidence");
        Directory.Exists(evidenceDir).Should().BeTrue();
        var evidenceFiles = Directory.GetFiles(evidenceDir);
        evidenceFiles.Should().NotBeEmpty("captured snapshots should be copied to evidence");

        // Should have both .txt and .html for the non-blocked snapshot
        evidenceFiles.Should().Contain(f => f.EndsWith(".txt"), "text extraction should be in evidence");
        evidenceFiles.Should().Contain(f => f.EndsWith(".html"), "HTML snapshot should be in evidence");
    }

    [Fact]
    public async Task ExportPacket_EvidenceFolder_ExcludesBlockedSnapshots()
    {
        var session = CreateSessionWithContent(withEvidence: true);
        var folder = await _export.ExportResearchPacketAsync(session.Id, _outputDir);

        var evidenceDir = Path.Combine(folder, "evidence");
        var evidenceFiles = Directory.GetFiles(evidenceDir);
        // Only the non-blocked snapshot's files; "Blocked Page" should not appear
        foreach (var f in evidenceFiles)
        {
            Path.GetFileName(f).Should().NotContain("Blocked",
                "blocked snapshots should not appear in evidence");
        }
    }

    [Fact]
    public async Task ExportPacket_IndexHtml_ShowsEvidenceSection()
    {
        var session = CreateSessionWithContent(withEvidence: true);
        var folder = await _export.ExportResearchPacketAsync(session.Id, _outputDir);

        var html = await File.ReadAllTextAsync(Path.Combine(folder, "index.html"));
        html.Should().Contain("Evidence");
        html.Should().Contain("href=\"evidence/");
    }

    [Fact]
    public async Task ExportPacket_SourcesCsv_ContainsAllSnapshots()
    {
        var session = CreateSessionWithContent(withEvidence: true);
        var folder = await _export.ExportResearchPacketAsync(session.Id, _outputDir);

        var csv = await File.ReadAllTextAsync(Path.Combine(folder, "sources.csv"));
        csv.Should().Contain("URL,Title,CapturedUtc,HttpStatus,IsBlocked,BlockReason");
        csv.Should().Contain("https://example.com/article");
        csv.Should().Contain("https://blocked.com/page");
        csv.Should().Contain("Access denied");
    }

    [Fact]
    public async Task ExportPacket_NotebookHtml_IsCreated()
    {
        var session = CreateSessionWithContent(withEvidence: true);
        var folder = await _export.ExportResearchPacketAsync(session.Id, _outputDir);

        File.Exists(Path.Combine(folder, "notebook.html")).Should().BeTrue();
        var html = await File.ReadAllTextAsync(Path.Combine(folder, "notebook.html"));
        html.Should().Contain("Research Note");
        html.Should().Contain("hypothesis is supported");
    }

    [Fact]
    public async Task ExportPacket_IndexHtml_StatsAreAccurate()
    {
        var session = CreateSessionWithContent(withEvidence: true);
        var folder = await _export.ExportResearchPacketAsync(session.Id, _outputDir);

        var html = await File.ReadAllTextAsync(Path.Combine(folder, "index.html"));
        // 2 reports
        html.Should().Contain(">2<", "should show 2 reports in stats");
        // 2 snapshots (1 real + 1 blocked)
        html.Should().Contain("Sources");
    }

    // ── Package Application ──

    [Fact]
    public void PackageApplication_CreatesPackageFolder()
    {
        var packageDir = _export.PackageApplication(_outputDir);

        Directory.Exists(packageDir).Should().BeTrue();
        File.Exists(Path.Combine(packageDir, "run.bat")).Should().BeTrue();
        File.Exists(Path.Combine(packageDir, "README.md")).Should().BeTrue();
        File.Exists(Path.Combine(packageDir, "appsettings.json")).Should().BeTrue();
    }
}
