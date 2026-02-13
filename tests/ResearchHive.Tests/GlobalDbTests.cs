using FluentAssertions;
using ResearchHive.Core.Data;
using ResearchHive.Core.Models;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for GlobalDb — the cross-session global memory store.
/// </summary>
public class GlobalDbTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GlobalDb _db;

    public GlobalDbTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_global_{Guid.NewGuid():N}.db");
        _db = new GlobalDb(_dbPath);
    }

    [Fact]
    public void SaveChunk_And_Retrieve_RoundTrips()
    {
        var chunk = new GlobalChunk
        {
            Id = "gc1",
            SessionId = "session1",
            JobId = "job1",
            SourceType = "evidence",
            DomainPack = "GeneralResearch",
            Text = "This is evidence about neural networks and machine learning.",
            Tags = new List<string> { "tag1", "session:session1" },
            PromotedUtc = DateTime.UtcNow
        };

        _db.SaveChunk(chunk);
        _db.GetChunkCount().Should().Be(1);
    }

    [Fact]
    public void SaveChunksBatch_SavesMultiple()
    {
        var chunks = Enumerable.Range(0, 10).Select(i => new GlobalChunk
        {
            Id = $"gc_{i}",
            SessionId = "session1",
            SourceType = i < 5 ? "evidence" : "strategy",
            Text = $"Chunk content number {i}",
            Tags = new List<string> { $"num:{i}" },
            PromotedUtc = DateTime.UtcNow
        }).ToList();

        _db.SaveChunksBatch(chunks);
        _db.GetChunkCount().Should().Be(10);
    }

    [Fact]
    public void SearchFtsBm25_FindsMatchingChunks()
    {
        _db.SaveChunk(new GlobalChunk
        {
            Id = "gc_match",
            SourceType = "evidence",
            Text = "Neural networks are powerful tools for machine learning classification tasks.",
            PromotedUtc = DateTime.UtcNow
        });
        _db.SaveChunk(new GlobalChunk
        {
            Id = "gc_nomatch",
            SourceType = "evidence",
            Text = "Cooking recipes for italian pasta dishes.",
            PromotedUtc = DateTime.UtcNow
        });

        var results = _db.SearchFtsBm25("neural networks machine learning");
        results.Should().NotBeEmpty();
        results[0].chunk.Id.Should().Be("gc_match");
    }

    [Fact]
    public void SearchFtsBm25_WithSourceTypeFilter_FiltersCorrectly()
    {
        _db.SaveChunk(new GlobalChunk
        {
            Id = "gc_strategy",
            SourceType = "strategy",
            Text = "Strategy for researching neural networks effectively.",
            PromotedUtc = DateTime.UtcNow
        });
        _db.SaveChunk(new GlobalChunk
        {
            Id = "gc_evidence",
            SourceType = "evidence",
            Text = "Evidence about neural networks performance benchmarks.",
            PromotedUtc = DateTime.UtcNow
        });

        var strategies = _db.SearchFtsBm25("neural networks", sourceTypeFilter: "strategy");
        strategies.Should().HaveCount(1);
        strategies[0].chunk.Id.Should().Be("gc_strategy");

        var evidence = _db.SearchFtsBm25("neural networks", sourceTypeFilter: "evidence");
        evidence.Should().HaveCount(1);
        evidence[0].chunk.Id.Should().Be("gc_evidence");
    }

    [Fact]
    public void GetStrategies_ReturnsOnlyStrategies()
    {
        _db.SaveChunksBatch(new[]
        {
            new GlobalChunk { Id = "s1", SourceType = "strategy", Text = "Strategy 1", DomainPack = "General", PromotedUtc = DateTime.UtcNow },
            new GlobalChunk { Id = "s2", SourceType = "strategy", Text = "Strategy 2", DomainPack = "Math", PromotedUtc = DateTime.UtcNow },
            new GlobalChunk { Id = "e1", SourceType = "evidence", Text = "Evidence 1", PromotedUtc = DateTime.UtcNow },
        });

        var all = _db.GetStrategies();
        all.Should().HaveCount(2);

        var generalOnly = _db.GetStrategies("General");
        generalOnly.Should().HaveCount(1);
        generalOnly[0].Id.Should().Be("s1");
    }

    [Fact]
    public void DeleteChunk_RemovesItAndFts()
    {
        _db.SaveChunk(new GlobalChunk { Id = "del1", SourceType = "evidence", Text = "Some evidence text", PromotedUtc = DateTime.UtcNow });
        _db.GetChunkCount().Should().Be(1);

        _db.DeleteChunk("del1");
        _db.GetChunkCount().Should().Be(0);
    }

    [Fact]
    public void DeleteBySession_RemovesAllSessionChunks()
    {
        _db.SaveChunksBatch(new[]
        {
            new GlobalChunk { Id = "c1", SessionId = "s1", SourceType = "evidence", Text = "From s1", PromotedUtc = DateTime.UtcNow },
            new GlobalChunk { Id = "c2", SessionId = "s1", SourceType = "evidence", Text = "Also from s1", PromotedUtc = DateTime.UtcNow },
            new GlobalChunk { Id = "c3", SessionId = "s2", SourceType = "evidence", Text = "From s2", PromotedUtc = DateTime.UtcNow },
        });

        _db.DeleteBySession("s1");
        _db.GetChunkCount().Should().Be(1);
    }

    [Fact]
    public void SaveChunk_WithEmbedding_RoundTrips()
    {
        var embedding = new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f };
        _db.SaveChunk(new GlobalChunk
        {
            Id = "emb1",
            SourceType = "evidence",
            Text = "Embedded chunk",
            Embedding = embedding,
            PromotedUtc = DateTime.UtcNow
        });

        var results = _db.GetAllChunksWithEmbeddings();
        results.Should().HaveCount(1);
        results[0].Embedding.Should().NotBeNull();
        results[0].Embedding!.Length.Should().Be(5);
        results[0].Embedding![0].Should().BeApproximately(0.1f, 0.001f);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }
}
