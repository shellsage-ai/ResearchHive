using FluentAssertions;
using ResearchHive.Core.Data;
using ResearchHive.Core.Models;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for Phase 11 new features: GlobalDb curation, SearchEngineHealthEntry, PdfIngestionService.
/// </summary>
public class Phase11FeatureTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GlobalDb _db;

    public Phase11FeatureTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_ph11_{Guid.NewGuid():N}.db");
        _db = new GlobalDb(_dbPath);
    }

    // ── GlobalDb.GetChunks pagination tests ──

    [Fact]
    public void GetChunks_ReturnsPagedResults()
    {
        SeedChunks(20);
        var page1 = _db.GetChunks(offset: 0, limit: 10);
        page1.Should().HaveCount(10);

        var page2 = _db.GetChunks(offset: 10, limit: 10);
        page2.Should().HaveCount(10);

        // No overlap
        page1.Select(c => c.Id).Intersect(page2.Select(c => c.Id)).Should().BeEmpty();
    }

    [Fact]
    public void GetChunks_FiltersBySourceType()
    {
        SeedChunks(10, sourceType: "report");
        SeedChunks(5, sourceType: "strategy", idPrefix: "strat_");

        var reports = _db.GetChunks(sourceTypeFilter: "report");
        reports.Should().HaveCount(10);
        reports.Should().OnlyContain(c => c.SourceType == "report");

        var strategies = _db.GetChunks(sourceTypeFilter: "strategy");
        strategies.Should().HaveCount(5);
    }

    [Fact]
    public void GetChunks_FiltersByDomainPack()
    {
        SeedChunks(8, domainPack: "GeneralResearch");
        SeedChunks(4, domainPack: "ProgrammingResearchIP", idPrefix: "prog_");

        var general = _db.GetChunks(domainPackFilter: "GeneralResearch");
        general.Should().HaveCount(8);
    }

    [Fact]
    public void GetChunks_FiltersBySessionId()
    {
        SeedChunks(6, sessionId: "s1");
        SeedChunks(3, sessionId: "s2", idPrefix: "s2_");

        var s1 = _db.GetChunks(sessionIdFilter: "s1");
        s1.Should().HaveCount(6);
    }

    [Fact]
    public void GetChunks_CombinedFilters()
    {
        SeedChunks(5, sourceType: "report", domainPack: "GeneralResearch");
        SeedChunks(3, sourceType: "strategy", domainPack: "GeneralResearch", idPrefix: "s_");
        SeedChunks(4, sourceType: "report", domainPack: "ProgrammingResearchIP", idPrefix: "p_");

        var result = _db.GetChunks(sourceTypeFilter: "report", domainPackFilter: "GeneralResearch");
        result.Should().HaveCount(5);
    }

    [Fact]
    public void GetChunks_OrdersByPromotedUtcDescending()
    {
        var now = DateTime.UtcNow;
        for (int i = 0; i < 5; i++)
        {
            _db.SaveChunk(new GlobalChunk
            {
                Id = $"ordered_{i}",
                SourceType = "evidence",
                Text = $"Chunk {i}",
                Tags = new(),
                PromotedUtc = now.AddMinutes(-i) // 0 = newest, 4 = oldest
            });
        }

        var chunks = _db.GetChunks();
        chunks[0].Id.Should().Be("ordered_0"); // newest first
        chunks[4].Id.Should().Be("ordered_4"); // oldest last
    }

    [Fact]
    public void GetDistinctSourceTypes_ReturnsUniqueTypes()
    {
        SeedChunks(3, sourceType: "report");
        SeedChunks(2, sourceType: "strategy", idPrefix: "s_");
        SeedChunks(1, sourceType: "repo_code", idPrefix: "r_");

        var types = _db.GetDistinctSourceTypes();
        types.Should().BeEquivalentTo(new[] { "repo_code", "report", "strategy" });
    }

    // ── SearchEngineHealthEntry model tests ──

    [Fact]
    public void SearchEngineHealthEntry_StatusDisplay_Idle_WhenNoQueries()
    {
        var entry = new SearchEngineHealthEntry { EngineName = "bing" };
        entry.StatusDisplay.Should().Be("Idle");
        entry.StatusIcon.Should().Be("⏸️");
    }

    [Fact]
    public void SearchEngineHealthEntry_StatusDisplay_Healthy_WhenAllSucceed()
    {
        var entry = new SearchEngineHealthEntry
        {
            EngineName = "bing",
            QueriesAttempted = 5,
            QueriesSucceeded = 5,
            TotalResultsFound = 40
        };
        entry.StatusDisplay.Should().Be("Healthy");
        entry.StatusIcon.Should().Be("✅");
    }

    [Fact]
    public void SearchEngineHealthEntry_StatusDisplay_Degraded_WhenPartialSuccess()
    {
        var entry = new SearchEngineHealthEntry
        {
            EngineName = "yahoo",
            QueriesAttempted = 5,
            QueriesSucceeded = 3,
            TotalResultsFound = 20
        };
        entry.StatusDisplay.Should().Be("Degraded");
        entry.StatusIcon.Should().Be("⚠️");
    }

    [Fact]
    public void SearchEngineHealthEntry_StatusDisplay_Failed_WhenNoSuccess()
    {
        var entry = new SearchEngineHealthEntry
        {
            EngineName = "scholar",
            QueriesAttempted = 3,
            QueriesSucceeded = 0
        };
        entry.StatusDisplay.Should().Be("Failed");
        entry.StatusIcon.Should().Be("❌");
    }

    [Fact]
    public void SearchEngineHealthEntry_StatusDisplay_Skipped_WhenMarkedSkipped()
    {
        var entry = new SearchEngineHealthEntry
        {
            EngineName = "brave",
            QueriesAttempted = 2,
            QueriesSucceeded = 0,
            IsSkipped = true
        };
        entry.StatusDisplay.Should().Be("Skipped");
        entry.StatusIcon.Should().Be("⏭️");
    }

    // ── GlobalDb.DeleteChunk (existing) verified ──

    [Fact]
    public void DeleteChunk_RemovesFromBothTables()
    {
        _db.SaveChunk(new GlobalChunk
        {
            Id = "del1",
            SourceType = "report",
            Text = "Chunk to delete",
            Tags = new(),
            PromotedUtc = DateTime.UtcNow
        });

        _db.GetChunkCount().Should().Be(1);
        _db.DeleteChunk("del1");
        _db.GetChunkCount().Should().Be(0);

        // FTS should also be clean
        var results = _db.SearchFtsBm25("delete", limit: 10);
        results.Should().BeEmpty();
    }

    // ── PdfExtractionResult model tests ──

    [Fact]
    public void PdfExtractionResult_FullText_IncludesPageMarkers()
    {
        // Verify the model shape (the service itself requires actual PDF files)
        var result = new Core.Services.PdfExtractionResult(
            Pages: new[]
            {
                new Core.Services.PdfPageResult(1, "Page one text", Core.Services.ExtractionMethod.TextLayer),
                new Core.Services.PdfPageResult(2, "Page two OCR", Core.Services.ExtractionMethod.Ocr)
            }.ToList(),
            FullText: "--- Page 1 [TextLayer] ---\nPage one text\n\n--- Page 2 [Ocr] ---\nPage two OCR",
            PageCount: 2);

        result.Pages.Should().HaveCount(2);
        result.Pages[0].Method.Should().Be(Core.Services.ExtractionMethod.TextLayer);
        result.Pages[1].Method.Should().Be(Core.Services.ExtractionMethod.Ocr);
        result.FullText.Should().Contain("Page 1 [TextLayer]");
        result.FullText.Should().Contain("Page 2 [Ocr]");
    }

    // ── Helpers ──

    private void SeedChunks(int count, string sourceType = "evidence", string domainPack = "GeneralResearch",
        string sessionId = "test_session", string idPrefix = "gc_")
    {
        var chunks = Enumerable.Range(0, count).Select(i => new GlobalChunk
        {
            Id = $"{idPrefix}{Guid.NewGuid():N}",
            SessionId = sessionId,
            SourceType = sourceType,
            DomainPack = domainPack,
            Text = $"Test chunk content #{i} for {sourceType}",
            Tags = new List<string> { "test" },
            PromotedUtc = DateTime.UtcNow
        }).ToList();

        _db.SaveChunksBatch(chunks);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }
}
