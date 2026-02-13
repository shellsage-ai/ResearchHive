using FluentAssertions;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Data;
using ResearchHive.Core.Models;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for SessionDb new repo profile columns (code_book, tree_sha, indexed_file_count, indexed_chunk_count)
/// and the migration path.
/// </summary>
public class SessionDbRepoProfileTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SessionDb _db;

    public SessionDbRepoProfileTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_session_{Guid.NewGuid():N}.db");
        _db = new SessionDb(_dbPath);
    }

    [Fact]
    public void SaveRepoProfile_WithNewFields_RoundTrips()
    {
        var profile = new RepoProfile
        {
            Id = "rp1",
            SessionId = "s1",
            RepoUrl = "https://github.com/test/repo",
            Owner = "test",
            Name = "repo",
            Description = "A test repo",
            PrimaryLanguage = "C#",
            CodeBook = "# CodeBook: test/repo\n\nThis is the architecture summary.",
            TreeSha = "abc123def456",
            IndexedFileCount = 42,
            IndexedChunkCount = 250,
        };

        _db.SaveRepoProfile(profile);

        var profiles = _db.GetRepoProfiles();
        profiles.Should().HaveCount(1);
        var loaded = profiles[0];
        loaded.CodeBook.Should().Be("# CodeBook: test/repo\n\nThis is the architecture summary.");
        loaded.TreeSha.Should().Be("abc123def456");
        loaded.IndexedFileCount.Should().Be(42);
        loaded.IndexedChunkCount.Should().Be(250);
    }

    [Fact]
    public void SaveRepoProfile_WithNullCodeBook_DefaultsToEmpty()
    {
        var profile = new RepoProfile
        {
            Id = "rp2",
            SessionId = "s1",
            RepoUrl = "https://github.com/test/repo2",
            Owner = "test",
            Name = "repo2",
            Description = "Test",
            PrimaryLanguage = "Python",
        };

        _db.SaveRepoProfile(profile);

        var loaded = _db.GetRepoProfiles()[0];
        loaded.CodeBook.Should().BeEmpty();
        loaded.TreeSha.Should().BeEmpty();
        loaded.IndexedFileCount.Should().Be(0);
        loaded.IndexedChunkCount.Should().Be(0);
    }

    [Fact]
    public void SaveRepoProfile_UpdateExisting_UpdatesNewFields()
    {
        var profile = new RepoProfile
        {
            Id = "rp3",
            SessionId = "s1",
            RepoUrl = "https://github.com/test/repo3",
            Owner = "test",
            Name = "repo3",
            Description = "Test",
            PrimaryLanguage = "Go",
        };

        _db.SaveRepoProfile(profile);

        // Update with new fields
        profile.TreeSha = "newsha789";
        profile.IndexedFileCount = 15;
        profile.IndexedChunkCount = 80;
        profile.CodeBook = "Updated CodeBook content";
        _db.SaveRepoProfile(profile);

        var loaded = _db.GetRepoProfiles()[0];
        loaded.TreeSha.Should().Be("newsha789");
        loaded.IndexedFileCount.Should().Be(15);
        loaded.IndexedChunkCount.Should().Be(80);
        loaded.CodeBook.Should().Be("Updated CodeBook content");
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }
}
