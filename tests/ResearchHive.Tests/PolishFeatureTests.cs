using FluentAssertions;
using Markdig;
using Microsoft.Data.Sqlite;
using ResearchHive.Core.Data;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for polish batch: Q&A persistence, pinned evidence persistence,
/// MarkdownViewer table support (via Markdig), and report section splitting.
/// </summary>
public class PolishFeatureTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SessionDb _db;

    public PolishFeatureTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"polish_{Guid.NewGuid():N}.db");
        _db = new SessionDb(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    // ───────────────── Q&A Message Persistence ─────────────────

    [Fact]
    public void SaveQaMessage_PersistsAndRetrievesCorrectly()
    {
        var msg = new QaMessage
        {
            SessionId = "s1",
            Question = "What is the key finding?",
            Answer = "The key finding is X based on sources [1][2].",
            Scope = "session"
        };

        _db.SaveQaMessage(msg);

        var loaded = _db.GetQaMessages();
        loaded.Should().HaveCount(1);
        loaded[0].Question.Should().Be("What is the key finding?");
        loaded[0].Answer.Should().Contain("key finding");
        loaded[0].Scope.Should().Be("session");
        loaded[0].SessionId.Should().Be("s1");
    }

    [Fact]
    public void SaveQaMessage_MultipleMessages_OrderedByTimestamp()
    {
        _db.SaveQaMessage(new QaMessage
        {
            Id = "qa1", SessionId = "s1", Question = "First?", Answer = "A1",
            TimestampUtc = new DateTime(2025, 1, 1, 10, 0, 0)
        });
        _db.SaveQaMessage(new QaMessage
        {
            Id = "qa2", SessionId = "s1", Question = "Second?", Answer = "A2",
            TimestampUtc = new DateTime(2025, 1, 1, 11, 0, 0)
        });

        var loaded = _db.GetQaMessages();
        loaded.Should().HaveCount(2);
        loaded[0].Question.Should().Be("First?");
        loaded[1].Question.Should().Be("Second?");
    }

    [Fact]
    public void DeleteQaMessage_RemovesOnlyTargeted()
    {
        _db.SaveQaMessage(new QaMessage { Id = "qa1", SessionId = "s1", Question = "Q1", Answer = "A1" });
        _db.SaveQaMessage(new QaMessage { Id = "qa2", SessionId = "s1", Question = "Q2", Answer = "A2" });

        _db.DeleteQaMessage("qa1");

        var loaded = _db.GetQaMessages();
        loaded.Should().HaveCount(1);
        loaded[0].Id.Should().Be("qa2");
    }

    [Fact]
    public void SaveQaMessage_ReportScope_Persists()
    {
        _db.SaveQaMessage(new QaMessage
        {
            SessionId = "s1", Question = "About this report?",
            Answer = "Report says X.", Scope = "rpt-abc123"
        });

        var loaded = _db.GetQaMessages();
        loaded[0].Scope.Should().Be("rpt-abc123");
    }

    [Fact]
    public void SaveQaMessage_UpsertOverwritesOnSameId()
    {
        _db.SaveQaMessage(new QaMessage { Id = "qa1", SessionId = "s1", Question = "Q?", Answer = "Old" });
        _db.SaveQaMessage(new QaMessage { Id = "qa1", SessionId = "s1", Question = "Q?", Answer = "Updated" });

        var loaded = _db.GetQaMessages();
        loaded.Should().HaveCount(1);
        loaded[0].Answer.Should().Be("Updated");
    }

    // ───────────────── Pinned Evidence Persistence ─────────────────

    [Fact]
    public void SavePinnedEvidence_PersistsAndRetrieves()
    {
        var pe = new PinnedEvidence
        {
            SessionId = "s1", ChunkId = "c1", SourceId = "snap-1",
            SourceType = "snapshot", Text = "Important evidence text",
            Score = 0.92f, SourceUrl = "https://example.com/article"
        };

        _db.SavePinnedEvidence(pe);

        var loaded = _db.GetPinnedEvidence();
        loaded.Should().HaveCount(1);
        loaded[0].ChunkId.Should().Be("c1");
        loaded[0].Text.Should().Be("Important evidence text");
        loaded[0].Score.Should().BeApproximately(0.92f, 0.01f);
        loaded[0].SourceUrl.Should().Be("https://example.com/article");
    }

    [Fact]
    public void DeletePinnedEvidence_RemovesCorrectEntry()
    {
        var pe1 = new PinnedEvidence { Id = "pin1", SessionId = "s1", ChunkId = "c1", Text = "First" };
        var pe2 = new PinnedEvidence { Id = "pin2", SessionId = "s1", ChunkId = "c2", Text = "Second" };

        _db.SavePinnedEvidence(pe1);
        _db.SavePinnedEvidence(pe2);

        _db.DeletePinnedEvidence("pin1");

        var loaded = _db.GetPinnedEvidence();
        loaded.Should().HaveCount(1);
        loaded[0].Id.Should().Be("pin2");
    }

    [Fact]
    public void GetPinnedEvidence_OrderedByPinnedTime_Descending()
    {
        _db.SavePinnedEvidence(new PinnedEvidence
        {
            Id = "pin1", SessionId = "s1", ChunkId = "c1", Text = "Older",
            PinnedUtc = new DateTime(2025, 1, 1)
        });
        _db.SavePinnedEvidence(new PinnedEvidence
        {
            Id = "pin2", SessionId = "s1", ChunkId = "c2", Text = "Newer",
            PinnedUtc = new DateTime(2025, 6, 1)
        });

        var loaded = _db.GetPinnedEvidence();
        loaded[0].Text.Should().Be("Newer");
        loaded[1].Text.Should().Be("Older");
    }

    // ───────────────── Chunk Count ─────────────────

    [Fact]
    public void GetChunkCount_ReturnsCorrectCount()
    {
        _db.GetChunkCount().Should().Be(0);

        _db.SaveChunk(new Chunk { Id = "ch1", SessionId = "s1", SourceId = "src1", Text = "text1" });
        _db.SaveChunk(new Chunk { Id = "ch2", SessionId = "s1", SourceId = "src1", Text = "text2" });

        _db.GetChunkCount().Should().Be(2);
    }

    // ───────────────── PinnedEvidence Model ─────────────────

    [Fact]
    public void PinnedEvidence_HasCorrectDefaults()
    {
        var pe = new PinnedEvidence();
        pe.Id.Should().NotBeNullOrEmpty();
        pe.SessionId.Should().BeEmpty();
        pe.PinnedUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    // ───────────────── Markdown Table Parsing (Markdig) ─────────────────

    [Fact]
    public void Markdig_ParsesTableIntoTableBlock()
    {
        var markdown = @"| Header A | Header B |
|----------|----------|
| Cell 1   | Cell 2   |
| Cell 3   | Cell 4   |";

        var pipeline = new Markdig.MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var doc = Markdig.Markdown.Parse(markdown, pipeline);

        // Verify Markdig correctly produces a Table block (which our converter now handles)
        doc.Should().ContainItemsAssignableTo<Markdig.Extensions.Tables.Table>();

        var table = doc.OfType<Markdig.Extensions.Tables.Table>().First();
        table.Count.Should().Be(3); // header row + 2 data rows

        var headerRow = table[0] as Markdig.Extensions.Tables.TableRow;
        headerRow.Should().NotBeNull();
        headerRow!.IsHeader.Should().BeTrue();
    }

    [Fact]
    public void Markdig_TableCell_ContainsParagraphWithText()
    {
        var markdown = @"| Name | Value |
|------|-------|
| Temp | 150°C |";

        var pipeline = new Markdig.MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var doc = Markdig.Markdown.Parse(markdown, pipeline);

        var table = doc.OfType<Markdig.Extensions.Tables.Table>().First();
        var dataRow = table[1] as Markdig.Extensions.Tables.TableRow;
        dataRow.Should().NotBeNull();
        dataRow!.Count.Should().Be(2);

        // Cells contain ParagraphBlock with inline text
        var firstCell = dataRow[0] as Markdig.Extensions.Tables.TableCell;
        firstCell.Should().NotBeNull();
        firstCell!.Count.Should().BeGreaterThan(0);
    }

    // ───────────────── Report Section Splitting with Tables ─────────────────

    [Fact]
    public void SplitReportIntoSections_HandlesTableMarkdown()
    {
        var markdown = @"## Comparison

| Method | Efficiency | Size |
|--------|-----------|------|
| Plant A | 85% | 20nm |
| Plant B | 92% | 15nm |

This table shows the comparison data is available.

## Conclusion

The evidence strongly supports green synthesis methods as viable alternatives.";

        var sections = RetrievalService.SplitReportIntoSections(markdown);

        sections.Should().HaveCountGreaterOrEqualTo(2);
        sections.Select(s => s.Heading).Should().Contain("Comparison");
        sections.Select(s => s.Heading).Should().Contain("Conclusion");
        // The Comparison section should contain the table markdown
        sections.First(s => s.Heading == "Comparison").Text.Should().Contain("Plant A");
    }
}
