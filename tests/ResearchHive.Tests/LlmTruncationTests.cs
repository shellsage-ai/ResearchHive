using FluentAssertions;
using ResearchHive.Core.Models;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for the LlmResponse model and truncation detection patterns.
/// </summary>
public class LlmTruncationTests
{
    [Fact]
    public void LlmResponse_WhenNotTruncated_ReportsCorrectly()
    {
        var response = new LlmResponse("Hello world", false, "stop");
        response.Text.Should().Be("Hello world");
        response.WasTruncated.Should().BeFalse();
        response.FinishReason.Should().Be("stop");
    }

    [Fact]
    public void LlmResponse_WhenTruncated_ReportsCorrectly()
    {
        var response = new LlmResponse("Truncated output...", true, "length");
        response.WasTruncated.Should().BeTrue();
        response.FinishReason.Should().Be("length");
    }

    [Fact]
    public void LlmResponse_Record_SupportsEquality()
    {
        var a = new LlmResponse("test", false, "stop");
        var b = new LlmResponse("test", false, "stop");
        a.Should().Be(b);
    }

    [Fact]
    public void LlmResponse_Record_SupportsDeconstruction()
    {
        var (text, wasTruncated, finishReason) = new LlmResponse("hello", true, "length");
        text.Should().Be("hello");
        wasTruncated.Should().BeTrue();
        finishReason.Should().Be("length");
    }

    [Fact]
    public void GlobalChunk_DefaultValues_AreCorrect()
    {
        var chunk = new GlobalChunk();
        chunk.Id.Should().NotBeNullOrEmpty();
        chunk.Tags.Should().NotBeNull().And.BeEmpty();
        chunk.PromotedUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void MemoryScope_HasAllExpectedValues()
    {
        var scopes = Enum.GetValues<MemoryScope>();
        scopes.Should().Contain(MemoryScope.ThisSession);
        scopes.Should().Contain(MemoryScope.ThisRepo);
        scopes.Should().Contain(MemoryScope.ThisDomain);
        scopes.Should().Contain(MemoryScope.HiveMind);
    }
}
