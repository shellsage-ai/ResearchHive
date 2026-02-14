using FluentAssertions;
using Markdig;
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

    // ── Markdown depth-safety tests ──

    [Fact]
    public void FlattenMarkdownNesting_CapsBlockquoteDepth()
    {
        // 8-deep blockquote should be flattened to 4
        var deep = ">>>>>>>>> deeply quoted text";
        var result = ExportService.FlattenMarkdownNesting(deep);
        // Count leading '>' characters
        int depth = 0;
        foreach (char c in result)
        {
            if (c == '>') depth++;
            else break;
        }
        depth.Should().BeLessOrEqualTo(4, "blockquote nesting should be capped at 4");
        result.Should().Contain("deeply quoted text");
    }

    [Fact]
    public void FlattenMarkdownNesting_CapsListIndentation()
    {
        // 24-space indented list item should be flattened to 12
        var deep = "                        - deeply nested item";
        var result = ExportService.FlattenMarkdownNesting(deep);
        var match = System.Text.RegularExpressions.Regex.Match(result, @"^(\s+)-");
        match.Success.Should().BeTrue();
        match.Groups[1].Value.Length.Should().BeLessOrEqualTo(12, "list indentation should be capped");
        result.Should().Contain("deeply nested item");
    }

    [Fact]
    public void FlattenMarkdownNesting_PreservesNormalContent()
    {
        var normal = "# Title\n\n> A quote\n\n- Item 1\n  - Item 2\n\nParagraph.";
        var result = ExportService.FlattenMarkdownNesting(normal);
        result.Should().Be(normal);
    }

    [Fact]
    public void SafeMarkdownToHtml_HandlesNormalMarkdown()
    {
        var pipeline = new Markdig.MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var html = ExportService.SafeMarkdownToHtml("# Hello\n\nWorld", pipeline);
        html.Should().Contain("Hello</h1>");
        html.Should().Contain("World");
    }

    [Fact]
    public void SafeMarkdownToHtml_EmptyInput_ReturnsEmpty()
    {
        var pipeline = new Markdig.MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        ExportService.SafeMarkdownToHtml("", pipeline).Should().BeEmpty();
        ExportService.SafeMarkdownToHtml(null!, pipeline).Should().BeEmpty();
    }

    [Fact]
    public void SafeMarkdownToHtml_DeeplyNestedBlockquotes_DoesNotThrow()
    {
        // Build a markdown string with 20-deep blockquotes mixed with lists
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 20; i++)
        {
            sb.Append(new string('>', i + 1));
            sb.AppendLine($" Level {i + 1}");
            sb.Append(new string('>', i + 1));
            sb.AppendLine($" - list inside quote level {i + 1}");
        }
        var pipeline = new Markdig.MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var html = ExportService.SafeMarkdownToHtml(sb.ToString(), pipeline);
        html.Should().NotBeNullOrEmpty("should produce HTML even with deeply nested markdown");
        html.Should().Contain("Level 1");
    }

    [Fact]
    public async Task ExportPacket_DeeplyNestedReport_DoesNotThrow()
    {
        var session = _manager.CreateSession("Deep Test", "Nested md", DomainPack.GeneralResearch);
        var db = _manager.GetSessionDb(session.Id);

        // Create a report with deeply nested blockquotes + lists
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Analysis Report");
        for (int i = 0; i < 15; i++)
        {
            sb.Append(new string('>', i + 1));
            sb.AppendLine($" Nested analysis layer {i + 1}");
            sb.Append(new string(' ', (i + 1) * 4));
            sb.AppendLine($"- Finding at depth {i + 1}");
        }
        db.SaveReport(new Report
        {
            SessionId = session.Id,
            JobId = "job-deep",
            Title = "Deep Analysis",
            ReportType = "analysis",
            Content = sb.ToString(),
            Format = "markdown"
        });

        var folder = await _export.ExportResearchPacketAsync(session.Id, _outputDir);
        Directory.Exists(folder).Should().BeTrue("packet folder should be created");
        var reportFiles = Directory.GetFiles(Path.Combine(folder, "reports"), "*.html");
        reportFiles.Should().NotBeEmpty("report HTML should be generated");

        var html = await File.ReadAllTextAsync(reportFiles[0]);
        html.Should().Contain("Analysis Report");
    }

    // ─────────────── Phase 32 — ConvertSetextToAtx tests ───────────────

    [Fact]
    public void ConvertSetextToAtx_ConvertsH1_SetextToAtx()
    {
        var input = "My Title\n========";
        var result = ExportService.ConvertSetextToAtx(input);
        result.Should().Be("# My Title");
    }

    [Fact]
    public void ConvertSetextToAtx_ConvertsH2_SetextToAtx()
    {
        var input = "Some text before\n\nSubtitle\n--------\n\nParagraph after";
        var result = ExportService.ConvertSetextToAtx(input);
        result.Should().Contain("## Subtitle");
        result.Should().NotContain("--------");
        result.Should().Contain("Some text before");
        result.Should().Contain("Paragraph after");
    }

    [Fact]
    public void ConvertSetextToAtx_RemovesOrphanedSeparators()
    {
        // === with no preceding text should be removed
        var input = "Hello\n\n=========\n\nWorld";
        var result = ExportService.ConvertSetextToAtx(input);
        result.Should().NotContain("===");
        result.Should().Contain("Hello");
        result.Should().Contain("World");
    }

    [Fact]
    public void ConvertSetextToAtx_PreservesNormalContent()
    {
        var input = "Regular paragraph\nwith continuation\n\n- list item";
        var result = ExportService.ConvertSetextToAtx(input);
        result.Should().Be(input);
    }

    [Fact]
    public void ConvertSetextToAtx_HandlesNullAndEmpty()
    {
        ExportService.ConvertSetextToAtx(null!).Should().BeNull();
        ExportService.ConvertSetextToAtx("").Should().Be("");
    }

    [Fact]
    public void ConvertSetextToAtx_DoesNotConvertListItemUnderlines()
    {
        // A --- line preceded by a list item should be treated as orphaned, not a header
        var input = "- First item\n---\n- Second item";
        var result = ExportService.ConvertSetextToAtx(input);
        result.Should().NotContain("## ");
    }

    [Fact]
    public void FlattenMarkdownNesting_IntegratesSetextConversion()
    {
        var input = "Title\n=====\n\nSome content\n\nSection\n-------\n\nMore text";
        var result = ExportService.FlattenMarkdownNesting(input);
        result.Should().Contain("# Title");
        result.Should().Contain("## Section");
        result.Should().NotContain("=====");
        result.Should().NotContain("-------");
    }

    // ─────────────── Bracket-escaping in pipe-table cells ───────────────

    [Fact]
    public void EscapeTableCellBrackets_EscapesBareVersionBrackets()
    {
        var row = "| Perfolizer | [0.3.0,) | src/BDN.csproj |";
        var result = ExportService.EscapeTableCellBrackets(row);
        result.Should().Contain("\\[0.3.0,)");
        result.Should().Contain("Perfolizer");
    }

    [Fact]
    public void EscapeTableCellBrackets_SkipsBacktickWrappedCells()
    {
        var row = "| Package | `[2.8.0]` | file.csproj |";
        var result = ExportService.EscapeTableCellBrackets(row);
        // Cell with backtick should be left as-is
        result.Should().Contain("`[2.8.0]`");
    }

    [Fact]
    public void EscapeTableCellBrackets_DoesNotDoubleEscape()
    {
        var row = "| Package | \\[2.8.0\\] | file.csproj |";
        var result = ExportService.EscapeTableCellBrackets(row);
        result.Should().NotContain("\\\\[");
    }

    [Fact]
    public void FlattenMarkdownNesting_EscapesBracketsInTableRows()
    {
        var md = "## Dependencies\n| Package | Version | Manifest |\n|---------|---------|----------|\n| Foo | [2.8.0] | a.csproj |\n| Bar | [5.0.0,) | b.csproj |";
        var result = ExportService.FlattenMarkdownNesting(md);
        result.Should().Contain("\\[2.8.0]", "opening bracket in table data rows should be escaped");
        result.Should().Contain("\\[5.0.0,)", "opening bracket in version range should be escaped");
        result.Should().Contain("|---------|", "separator rows should NOT be modified");
    }

    [Fact]
    public void SafeMarkdownToHtml_BracketVersionTable_DoesNotFallbackToPre()
    {
        // Simulate BenchmarkDotNet-style dependency table with NuGet version ranges
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Repository Analysis: dotnet/BenchmarkDotNet");
        sb.AppendLine();
        sb.AppendLine("## Dependencies");
        sb.AppendLine("| Package | Version | Manifest |");
        sb.AppendLine("|---------|---------|----------|");
        sb.AppendLine("| BenchmarkDotNet.Annotations | [2.8.0] | src/BDN.csproj |");
        sb.AppendLine("| Perfolizer | [0.3.0,) | src/BDN.csproj |");
        sb.AppendLine("| Microsoft.Diagnostics.Tracing.TraceEvent | [3.1.8,) | src/BDN.csproj |");
        sb.AppendLine("| CommandLineParser | [2.9.1] | src/BDN.csproj |");
        sb.AppendLine("| Iced | [1.17.0] | src/BDN.csproj |");
        sb.AppendLine("| Microsoft.CodeAnalysis.CSharp | [4.8.0] | src/BDN.csproj |");
        for (int i = 0; i < 22; i++)
            sb.AppendLine($"| Package{i} | [1.{i}.0] | src/BDN.csproj |");
        sb.AppendLine();
        sb.AppendLine("## Strengths");
        sb.AppendLine("- ✅ Mature benchmarking framework");

        var pipeline = new Markdig.MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var html = ExportService.SafeMarkdownToHtml(sb.ToString(), pipeline);

        html.Should().NotStartWith("<pre>", "should NOT fall back to <pre> for bracket-laden tables");
        html.Should().Contain("<table>", "table should be rendered as HTML table");
        html.Should().Contain("BenchmarkDotNet.Annotations");
    }

    [Fact]
    public void SafeMarkdownToHtml_BenchmarkDotNetReport_BacktickWrappedVersions_DoesNotCrash()
    {
        // Reproduce the EXACT crash: a full BenchmarkDotNet repo analysis report
        // as generated by RepoIntelligenceJobRunner.  The 30-row dependency pipe
        // table with real package names, backtick-wrapped bracket versions, and
        // long .csproj manifest paths exceeds Markdig's internal inline-depth
        // limit (System.ArgumentException: "too deeply nested … very large table").
        var md = @"# Repository Analysis: dotnet/BenchmarkDotNet

**URL:** https://github.com/dotnet/BenchmarkDotNet
**Primary Language:** C#
**Stars:** 11356 | **Forks:** 1042 | **Open Issues:** 202
**Topics:** benchmark, benchmarking, c-sharp, csharp, dotnet, hacktoberfest, performance
**Last Updated:** 2026-02-13

> Powerful .NET library for benchmarking

## Project Summary
BenchmarkDotNet is a .NET library for benchmarking and performance measurement of C# code.

## Languages
- C#
- Shell
- R
- F#
- PowerShell

## Frameworks & Key Technologies
- .NET Standard (netstandard2.0)
- Mono

## Dependencies
| Package | Version | Manifest |
|---------|---------|----------|
| Microsoft.CodeAnalysis.CSharp | `[2.8.0]` | src/BenchmarkDotNet.Analyzers/BenchmarkDotNet.Analyzers.csproj |
| Microsoft.CodeAnalysis.CSharp | `[3.8.0]` | src/BenchmarkDotNet.Analyzers/BenchmarkDotNet.Analyzers.csproj |
| Microsoft.CodeAnalysis.CSharp | `[4.8.0]` | src/BenchmarkDotNet.Analyzers/BenchmarkDotNet.Analyzers.csproj |
| Microsoft.CodeAnalysis.CSharp | `[5.0.0,)` | src/BenchmarkDotNet.Analyzers/BenchmarkDotNet.Analyzers.csproj |
| PolySharp | 1.15.0 | src/BenchmarkDotNet.Annotations/BenchmarkDotNet.Annotations.csproj |
| BenchmarkDotNet.Weaver | $(Version)$(WeaverVersionSuffix) | src/BenchmarkDotNet.Annotations/BenchmarkDotNet.Annotations.csproj |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.1.28 | src/BenchmarkDotNet.Diagnostics.Windows/BenchmarkDotNet.Diagnostics.Windows.csproj |
| JetBrains.Profiler.SelfApi | 2.5.15 | src/BenchmarkDotNet.Diagnostics.dotMemory/BenchmarkDotNet.Diagnostics.dotMemory.csproj |
| JetBrains.Profiler.SelfApi | 2.5.15 | src/BenchmarkDotNet.Diagnostics.dotTrace/BenchmarkDotNet.Diagnostics.dotTrace.csproj |
| ScottPlot | 5.0.55 | src/BenchmarkDotNet.Exporters.Plotting/BenchmarkDotNet.Exporters.Plotting.csproj |
| Microsoft.TestPlatform.AdapterUtilities | 17.8.0 | src/BenchmarkDotNet.TestAdapter/BenchmarkDotNet.TestAdapter.csproj |
| Microsoft.TestPlatform.ObjectModel | 17.8.0 | src/BenchmarkDotNet.TestAdapter/BenchmarkDotNet.TestAdapter.csproj |
| Microsoft.TestPlatform.TranslationLayer | 17.8.0 | src/BenchmarkDotNet.TestAdapter/BenchmarkDotNet.TestAdapter.csproj |
| AsmResolver.DotNet | 6.0.0-beta.5 | src/BenchmarkDotNet.Weaver/BenchmarkDotNet.Weaver.csproj |
| Microsoft.Build.Framework | 18.0.2 | src/BenchmarkDotNet.Weaver/BenchmarkDotNet.Weaver.csproj |
| Microsoft.Build.Utilities.Core | 18.0.2 | src/BenchmarkDotNet.Weaver/BenchmarkDotNet.Weaver.csproj |
| CommandLineParser | 2.9.1 | src/BenchmarkDotNet/BenchmarkDotNet.csproj |
| Gee.External.Capstone | 2.3.0 | src/BenchmarkDotNet/BenchmarkDotNet.csproj |
| Iced | 1.21.0 | src/BenchmarkDotNet/BenchmarkDotNet.csproj |
| Microsoft.Diagnostics.Runtime | 3.1.512801 | src/BenchmarkDotNet/BenchmarkDotNet.csproj |
| Perfolizer | `[0.6.6]` | src/BenchmarkDotNet/BenchmarkDotNet.csproj |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.1.28 | src/BenchmarkDotNet/BenchmarkDotNet.csproj |
| Microsoft.DotNet.PlatformAbstractions | 3.1.6 | src/BenchmarkDotNet/BenchmarkDotNet.csproj |
| Microsoft.CodeAnalysis.CSharp | 5.0.0 | src/BenchmarkDotNet/BenchmarkDotNet.csproj |
| System.Management | 10.0.1 | src/BenchmarkDotNet/BenchmarkDotNet.csproj |
| System.Text.Json | 10.0.1 | src/BenchmarkDotNet/BenchmarkDotNet.csproj |
| Microsoft.Win32.Registry | 5.0.0 | src/BenchmarkDotNet/BenchmarkDotNet.csproj |
| System.Buffers | 4.6.1 | src/BenchmarkDotNet/BenchmarkDotNet.csproj |
| System.Numerics.Vectors | 4.6.1 | src/BenchmarkDotNet/BenchmarkDotNet.csproj |
| System.Reflection.Emit | 4.7.0 | src/BenchmarkDotNet/BenchmarkDotNet.csproj |
_...and 40 more_

## Strengths
- ✅ Automatic warmup and iteration counting
- ✅ Statistical analysis and visualization of benchmark results
- ✅ Support for multiple runtimes (e.g. .NET Core, .NET Framework, Mono)
- ✅ Easy-to-use API for writing benchmarks
- ✅ Integration with popular IDEs like Visual Studio
- ✅ Customizable output formats (e.g. CSV, JSON, HTML)
- ✅ Built-in support for common benchmarking scenarios (e.g. throughput, latency)

## Gaps & Improvement Opportunities
- 🔸 No Dependabot/Renovate config found
- 🔸 No Dockerfile found

## Complementary Projects

### dotnet-reportgenerator
- **URL:** https://github.com/dotnet/reportgenerator
- **Purpose:** Generates reports from code coverage and test results.
- **What it adds:** Adds reporting capabilities to BenchmarkDotNet.
- **Category:** Documentation|Testing | **License:** MIT | **Maturity:** Mature

### dotnet-OpenTelemetry
- **URL:** https://github.com/dotnet/opentelemetry-dotnet
- **Purpose:** Provides OpenTelemetry for .NET.
- **What it adds:** Adds distributed tracing capabilities to BenchmarkDotNet.
- **Category:** Monitoring|DevOps | **License:** Apache-2.0 | **Maturity:** Mature

### dotnet-trace
- **URL:** https://github.com/dotnet/tracing/tree/main/src/Trace
- **Purpose:** Provides a .NET tracing API.
- **What it adds:** Adds tracing capabilities to BenchmarkDotNet.
- **Category:** Monitoring|DevOps | **License:** MIT | **Maturity:** Mature

### dotnet-PerfView
- **URL:** https://github.com/dotnet/perfview
- **Purpose:** Provides a .NET performance analysis tool.
- **What it adds:** Adds performance analysis capabilities to BenchmarkDotNet.
- **Category:** Performance|Testing | **License:** MIT | **Maturity:** Mature

### dotnet-Profiler
- **URL:** https://github.com/dotnet/profiler
- **Purpose:** Provides a .NET profiler.
- **What it adds:** Adds profiling capabilities to BenchmarkDotNet.
- **Category:** Performance|Testing | **License:** MIT | **Maturity:** Mature

## Pipeline Telemetry
**Total Duration:** 106.2s | **LLM Calls:** 5 (75.1s) | **RAG Queries:** 23 | **Web Searches:** 12 | **GitHub API Calls:** 50

| # | Purpose | Model | Duration | Prompt | Response |
|---|---------|-------|----------|--------|----------|
| 1 | Identity Scan | llama3.1:8b | 2.6s | — | — |
| 2 | CodeBook Generation | llama3.1:8b | 12.2s | — | 2.4K chars |
| 3 | RAG-Grounded Analysis | llama3.1:8b | 15.9s | 100.6K chars | 1.2K chars |
| 4 | Gap Verification | llama3.1:8b | 17.2s | 62.5K chars | 1.7K chars |
| 5 | Complement Evaluation | llama3.1:8b | 27.2s | — | — |

| Phase | Duration |
|-------|----------|
| Metadata Scan | 10.7s |
| Clone + Index | 1.8s |
| Identity Scan | 2.6s |
| Deterministic Fact Sheet | 0.1s |
| CodeBook Generation | 12.2s |
| RAG Analysis | 16.2s |
| Gap Verification | 17.4s |
| Complement Research | 33.2s |
| Post-Scan Verification | 7.8s |
| Self-Validation | 4.1s |
";

        var pipeline = new Markdig.MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var html = ExportService.SafeMarkdownToHtml(md, pipeline);

        // PRIMARY ASSERTION: must NOT fall back to <pre>
        html.Should().NotStartWith("<pre>",
            "Markdig must not crash on a 30-row dependency table");
        html.Should().Contain("<table", "dependency table should render as HTML");
        html.Should().Contain("BenchmarkDotNet");
        html.Should().Contain("and 40 more");
    }
}
