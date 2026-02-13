using FluentAssertions;
using Microsoft.Data.Sqlite;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;
using ResearchHive.Core.Data;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for model attribution (Phase 13) — verifying that every AI-generated
/// output carries the name of the LLM model that produced it.
/// </summary>
public class ModelAttributionTests
{
    // ---- LlmResponse ModelName ----

    [Fact]
    public void LlmResponse_CarriesModelName()
    {
        var response = new LlmResponse("Hello", false, "stop", "llama3.2:latest");
        response.ModelName.Should().Be("llama3.2:latest");
    }

    [Fact]
    public void LlmResponse_ModelName_DefaultsToNull()
    {
        var response = new LlmResponse("Hello", false, "stop");
        response.ModelName.Should().BeNull();
    }

    [Fact]
    public void LlmResponse_WithModelName_SupportsEquality()
    {
        var a = new LlmResponse("text", false, "stop", "gpt-4o");
        var b = new LlmResponse("text", false, "stop", "gpt-4o");
        a.Should().Be(b);
    }

    [Fact]
    public void LlmResponse_DifferentModels_AreNotEqual()
    {
        var a = new LlmResponse("text", false, "stop", "gpt-4o");
        var b = new LlmResponse("text", false, "stop", "claude-sonnet-4-20250514");
        a.Should().NotBe(b);
    }

    [Fact]
    public void LlmResponse_Deconstruction_IncludesModelName()
    {
        var (text, truncated, reason, model) = new LlmResponse("hi", false, "stop", "gemini-pro");
        text.Should().Be("hi");
        truncated.Should().BeFalse();
        reason.Should().Be("stop");
        model.Should().Be("gemini-pro");
    }

    // ---- Domain Model Attribution Fields ----

    [Fact]
    public void ResearchJob_HasModelUsedProperty()
    {
        var job = new ResearchJob { ModelUsed = "llama3.2:latest" };
        job.ModelUsed.Should().Be("llama3.2:latest");
    }

    [Fact]
    public void ResearchJob_ModelUsed_DefaultsToNull()
    {
        var job = new ResearchJob();
        job.ModelUsed.Should().BeNull();
    }

    [Fact]
    public void Report_HasModelUsedProperty()
    {
        var report = new Report { ModelUsed = "claude-sonnet-4-20250514" };
        report.ModelUsed.Should().Be("claude-sonnet-4-20250514");
    }

    [Fact]
    public void QaMessage_HasModelUsedProperty()
    {
        var msg = new QaMessage { ModelUsed = "gpt-4o" };
        msg.ModelUsed.Should().Be("gpt-4o");
    }

    [Fact]
    public void RepoProfile_HasAnalysisModelUsedProperty()
    {
        var profile = new RepoProfile { AnalysisModelUsed = "mistral-large-latest" };
        profile.AnalysisModelUsed.Should().Be("mistral-large-latest");
    }

    // ---- DB Persistence ----

    [Fact]
    public void SessionDb_PersistsJobModelUsed()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"model_attr_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new SessionDb(dbPath);
            var job = new ResearchJob
            {
                Id = "j1", SessionId = "s1", Prompt = "test",
                ModelUsed = "llama3.2:latest"
            };
            db.SaveJob(job);
            var loaded = db.GetJob("j1");
            loaded.Should().NotBeNull();
            loaded!.ModelUsed.Should().Be("llama3.2:latest");
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(dbPath); } catch { } }
    }

    [Fact]
    public void SessionDb_PersistsReportModelUsed()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"model_attr_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new SessionDb(dbPath);
            var report = new Report
            {
                Id = "r1", SessionId = "s1", JobId = "j1",
                ReportType = "full", Title = "Test", Content = "Body",
                ModelUsed = "gpt-4o"
            };
            db.SaveReport(report);
            var loaded = db.GetReports().FirstOrDefault(r => r.Id == "r1");
            loaded.Should().NotBeNull();
            loaded!.ModelUsed.Should().Be("gpt-4o");
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(dbPath); } catch { } }
    }

    [Fact]
    public void SessionDb_PersistsQaMessageModelUsed()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"model_attr_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new SessionDb(dbPath);
            var qa = new QaMessage
            {
                Id = "qa1", SessionId = "s1",
                Question = "What?", Answer = "That.",
                ModelUsed = "claude-sonnet-4-20250514"
            };
            db.SaveQaMessage(qa);
            var loaded = db.GetQaMessages().FirstOrDefault(m => m.Id == "qa1");
            loaded.Should().NotBeNull();
            loaded!.ModelUsed.Should().Be("claude-sonnet-4-20250514");
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(dbPath); } catch { } }
    }

    [Fact]
    public void SessionDb_PersistsRepoProfileAnalysisModel()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"model_attr_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new SessionDb(dbPath);
            var profile = new RepoProfile
            {
                Id = "rp1", SessionId = "s1",
                RepoUrl = "https://github.com/test/repo",
                Owner = "test", Name = "repo",
                Description = "Test repo",
                PrimaryLanguage = "C#",
                AnalysisModelUsed = "llama3.2:latest"
            };
            db.SaveRepoProfile(profile);
            var loaded = db.GetRepoProfiles().FirstOrDefault(p => p.Id == "rp1");
            loaded.Should().NotBeNull();
            loaded!.AnalysisModelUsed.Should().Be("llama3.2:latest");
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(dbPath); } catch { } }
    }

    [Fact]
    public void SessionDb_MigrationAddsModelColumns_GracefullyHandlesOldData()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"model_attr_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new SessionDb(dbPath);
            // Save with null model
            var job = new ResearchJob { Id = "j2", SessionId = "s1", Prompt = "test" };
            db.SaveJob(job);
            var loaded = db.GetJob("j2");
            loaded.Should().NotBeNull();
            loaded!.ModelUsed.Should().BeNull();
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(dbPath); } catch { } }
    }

    // ---- Complement Enforcement ----

    [Fact]
    public void ComplementResearchService_MinimumComplements_IsAtLeast5()
    {
        ComplementResearchService.MinimumComplements.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void ComplementParser_ParsesMultipleComplements()
    {
        // Use reflection to access the private ParseComplements method
        var method = typeof(ComplementResearchService)
            .GetMethod("ParseComplements", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("ParseComplements should exist");

        var input = @"## Complement
- Name: ProjectA
- Url: https://github.com/a/a
- Purpose: Testing
- WhatItAdds: Unit tests
- Category: Testing
- License: MIT
- Maturity: Mature

## Complement
- Name: ProjectB
- Url: https://github.com/b/b
- Purpose: Monitoring
- WhatItAdds: Metrics
- Category: Monitoring
- License: Apache-2.0
- Maturity: Growing

## Complement
- Name: ProjectC
- Url: https://github.com/c/c
- Purpose: Security
- WhatItAdds: Vulnerability scanning
- Category: Security
- License: MIT
- Maturity: Mature

## Complement
- Name: ProjectD
- Url: https://github.com/d/d
- Purpose: Docs
- WhatItAdds: Auto documentation
- Category: Documentation
- License: MIT
- Maturity: Early

## Complement
- Name: ProjectE
- Url: https://github.com/e/e
- Purpose: CI/CD
- WhatItAdds: Pipeline automation
- Category: DevOps
- License: Apache-2.0
- Maturity: Mature";

        var result = method!.Invoke(null, new object[] { input }) as List<ComplementProject>;
        result.Should().NotBeNull();
        result!.Count.Should().BeGreaterThanOrEqualTo(5);
        result[0].Name.Should().Be("ProjectA");
        result[4].Name.Should().Be("ProjectE");
        result.Should().OnlyContain(c => !string.IsNullOrEmpty(c.Name));
        result.Should().OnlyContain(c => !string.IsNullOrEmpty(c.Url));
    }

    // ---- LlmResponse providers carry correct model names ----

    [Theory]
    [InlineData("llama3.2:latest")]
    [InlineData("claude-sonnet-4-20250514")]
    [InlineData("gpt-4o")]
    [InlineData("gemini-pro")]
    [InlineData("mistral-large-latest")]
    [InlineData("codex-cli")]
    public void LlmResponse_AcceptsAllProviderModelNames(string modelName)
    {
        var response = new LlmResponse("test output", false, "stop", modelName);
        response.ModelName.Should().Be(modelName);
    }
}
