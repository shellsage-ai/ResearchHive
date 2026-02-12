using FluentAssertions;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Services;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for the new AI model/service selection features:
/// - RoutingStrategy, ApiKeySource, GitHubModels provider
/// - SecureKeyStore (DPAPI)
/// - ResearchTools definitions
/// - LlmService routing
/// - KnownCloudModels
/// </summary>
public class AiModelSelectionTests
{
    #region AppSettings — New Enums / Properties

    [Fact]
    public void RoutingStrategy_HasAllExpectedValues()
    {
        var values = Enum.GetValues<RoutingStrategy>();
        values.Should().Contain(RoutingStrategy.LocalOnly);
        values.Should().Contain(RoutingStrategy.LocalWithCloudFallback);
        values.Should().Contain(RoutingStrategy.CloudPrimary);
        values.Should().Contain(RoutingStrategy.CloudOnly);
    }

    [Fact]
    public void ApiKeySource_HasExpectedValues()
    {
        var values = Enum.GetValues<ApiKeySource>();
        values.Should().Contain(ApiKeySource.Direct);
        values.Should().Contain(ApiKeySource.EnvironmentVariable);
    }

    [Fact]
    public void PaidProviderType_IncludesGitHubModels()
    {
        var values = Enum.GetValues<PaidProviderType>();
        values.Should().Contain(PaidProviderType.GitHubModels);
        values.Should().HaveCountGreaterThanOrEqualTo(7); // None + 6 providers + GitHub
    }

    [Fact]
    public void AppSettings_NewDefaults_AreReasonable()
    {
        var settings = new AppSettings();
        settings.Routing.Should().Be(RoutingStrategy.LocalWithCloudFallback);
        settings.EnableToolCalling.Should().BeTrue();
        settings.MaxToolCallsPerPhase.Should().Be(10);
        settings.KeySource.Should().Be(ApiKeySource.Direct);
        settings.PaidProviderModel.Should().BeNull();
        settings.GitHubPat.Should().BeNull();
        settings.KeyEnvironmentVariable.Should().BeNull();
    }

    [Fact]
    public void AppSettings_Serialization_IncludesNewFields()
    {
        var settings = new AppSettings
        {
            Routing = RoutingStrategy.CloudPrimary,
            PaidProviderModel = "gpt-4o",
            EnableToolCalling = false,
            MaxToolCallsPerPhase = 5,
            KeySource = ApiKeySource.EnvironmentVariable,
            KeyEnvironmentVariable = "OPENAI_API_KEY",
            PaidProvider = PaidProviderType.GitHubModels,
            GitHubPat = "ghp_test123"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Routing.Should().Be(RoutingStrategy.CloudPrimary);
        deserialized.PaidProviderModel.Should().Be("gpt-4o");
        deserialized.EnableToolCalling.Should().BeFalse();
        deserialized.MaxToolCallsPerPhase.Should().Be(5);
        deserialized.KeySource.Should().Be(ApiKeySource.EnvironmentVariable);
        deserialized.KeyEnvironmentVariable.Should().Be("OPENAI_API_KEY");
        deserialized.PaidProvider.Should().Be(PaidProviderType.GitHubModels);
        deserialized.GitHubPat.Should().Be("ghp_test123");
    }

    #endregion

    #region KnownCloudModels

    [Fact]
    public void KnownCloudModels_ContainsAllProviders()
    {
        var models = AppSettings.KnownCloudModels;
        models.Should().ContainKey(PaidProviderType.OpenAI);
        models.Should().ContainKey(PaidProviderType.Anthropic);
        models.Should().ContainKey(PaidProviderType.GoogleGemini);
        models.Should().ContainKey(PaidProviderType.MistralAI);
        models.Should().ContainKey(PaidProviderType.OpenRouter);
        models.Should().ContainKey(PaidProviderType.AzureOpenAI);
        models.Should().ContainKey(PaidProviderType.GitHubModels);
    }

    [Fact]
    public void KnownCloudModels_OpenAI_HasGpt4o()
    {
        AppSettings.KnownCloudModels[PaidProviderType.OpenAI].Should().Contain("gpt-4o");
    }

    [Fact]
    public void KnownCloudModels_GitHubModels_HasPrefixedModels()
    {
        var models = AppSettings.KnownCloudModels[PaidProviderType.GitHubModels];
        models.Should().AllSatisfy(m => m.Should().Contain("/", "GitHub Models use vendor-prefixed names"));
    }

    [Fact]
    public void KnownCloudModels_Anthropic_HasClaude()
    {
        var models = AppSettings.KnownCloudModels[PaidProviderType.Anthropic];
        models.Should().Contain(m => m.Contains("claude"));
    }

    [Fact]
    public void KnownCloudModels_EachProvider_HasAtLeastOneModel()
    {
        foreach (var (provider, models) in AppSettings.KnownCloudModels)
        {
            models.Should().NotBeEmpty($"provider {provider} should have at least one known model");
        }
    }

    #endregion

    #region SecureKeyStore

    [Fact]
    public void SecureKeyStore_SaveAndLoad_Roundtrips()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rh_test_{Guid.NewGuid():N}");
        try
        {
            var store = new SecureKeyStore(tempDir);
            store.SaveKey("test_key", "super_secret_value_123");

            var loaded = store.LoadKey("test_key");
            loaded.Should().Be("super_secret_value_123");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SecureKeyStore_HasKey_ReturnsTrueForSaved()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rh_test_{Guid.NewGuid():N}");
        try
        {
            var store = new SecureKeyStore(tempDir);
            store.HasKey("missing").Should().BeFalse();

            store.SaveKey("exists", "value");
            store.HasKey("exists").Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SecureKeyStore_DeleteKey_RemovesKey()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rh_test_{Guid.NewGuid():N}");
        try
        {
            var store = new SecureKeyStore(tempDir);
            store.SaveKey("to_delete", "value");
            store.HasKey("to_delete").Should().BeTrue();

            store.DeleteKey("to_delete");
            store.HasKey("to_delete").Should().BeFalse();
            store.LoadKey("to_delete").Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SecureKeyStore_LoadKey_MissingKey_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rh_test_{Guid.NewGuid():N}");
        try
        {
            var store = new SecureKeyStore(tempDir);
            store.LoadKey("nonexistent").Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SecureKeyStore_MultipleKeys_IndependentlyStored()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rh_test_{Guid.NewGuid():N}");
        try
        {
            var store = new SecureKeyStore(tempDir);
            store.SaveKey("key1", "value1");
            store.SaveKey("key2", "value2");
            store.SaveKey("key3", "value3");

            store.LoadKey("key1").Should().Be("value1");
            store.LoadKey("key2").Should().Be("value2");
            store.LoadKey("key3").Should().Be("value3");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SecureKeyStore_ResolveApiKey_Direct_ReturnsStoredKey()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rh_test_{Guid.NewGuid():N}");
        try
        {
            var store = new SecureKeyStore(tempDir);
            store.SaveKey("OpenAI", "sk-test-key");

            var resolved = store.ResolveApiKey("OpenAI", ApiKeySource.Direct, null);
            resolved.Should().Be("sk-test-key");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SecureKeyStore_ResolveApiKey_EnvironmentVariable_ReadsEnvVar()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rh_test_{Guid.NewGuid():N}");
        var envVar = $"RH_TEST_KEY_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(envVar, "env-secret-value");
            var store = new SecureKeyStore(tempDir);

            var resolved = store.ResolveApiKey("test", ApiKeySource.EnvironmentVariable, envVar);
            resolved.Should().Be("env-secret-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    #endregion

    #region ResearchTools

    [Fact]
    public void ResearchTools_All_HasFourTools()
    {
        ResearchTools.All.Should().HaveCount(4);
    }

    [Fact]
    public void ResearchTools_All_ContainsExpectedToolNames()
    {
        var names = ResearchTools.All.Select(t => t.Function.Name).ToList();
        names.Should().Contain("search_evidence");
        names.Should().Contain("search_web");
        names.Should().Contain("get_source");
        names.Should().Contain("verify_claim");
    }

    [Fact]
    public void ResearchTools_All_HaveValidStructure()
    {
        foreach (var tool in ResearchTools.All)
        {
            tool.Type.Should().Be("function");
            tool.Function.Name.Should().NotBeNullOrEmpty();
            tool.Function.Description.Should().NotBeNullOrEmpty();
            tool.Function.Parameters.Type.Should().Be("object");
            tool.Function.Parameters.Required.Should().NotBeEmpty();
            tool.Function.Parameters.Properties.Should().NotBeEmpty();
        }
    }

    [Fact]
    public void ResearchTools_ToAnthropicFormat_ReturnsCorrectCount()
    {
        var anthropicTools = ResearchTools.ToAnthropicFormat();
        anthropicTools.Should().HaveCount(4);
    }

    [Fact]
    public void ToolCallFunction_ParseArgs_ValidJson_ReturnsDictionary()
    {
        var tcf = new ToolCallFunction
        {
            Name = "search_evidence",
            Arguments = "{\"query\": \"silver nanoparticles\"}"
        };

        var args = tcf.ParseArgs();
        args.Should().ContainKey("query");
        args["query"].Should().Be("silver nanoparticles");
    }

    [Fact]
    public void ToolCallFunction_ParseArgs_InvalidJson_ReturnsEmpty()
    {
        var tcf = new ToolCallFunction
        {
            Name = "test",
            Arguments = "not json"
        };

        var args = tcf.ParseArgs();
        args.Should().BeEmpty();
    }

    #endregion

    #region LlmService — Routing

    [Fact]
    public async Task LlmService_LocalOnly_DoesNotCallCloud()
    {
        var settings = new AppSettings
        {
            Routing = RoutingStrategy.LocalOnly,
            OllamaBaseUrl = "http://localhost:99999", // unreachable
            UsePaidProvider = true,
            PaidProvider = PaidProviderType.OpenAI,
            PaidProviderApiKey = "sk-test"
        };

        var service = new LlmService(settings);
        // Should return error — Ollama unreachable, cloud not tried on LocalOnly
        var result = await service.GenerateAsync("Create a research plan for testing");
        result.Should().Contain("[LLM_UNAVAILABLE]"); // clear error, not fake content
    }

    [Fact]
    public async Task LlmService_CloudOnly_SkipsOllama()
    {
        var settings = new AppSettings
        {
            Routing = RoutingStrategy.CloudOnly,
            UsePaidProvider = false // No cloud configured
        };

        var service = new LlmService(settings);
        // Should return error message (no cloud configured, no deterministic fallback)
        var result = await service.GenerateAsync("Create a research plan for testing");
        result.Should().Contain("[LLM_UNAVAILABLE]");
    }

    [Fact]
    public async Task LlmService_DeterministicFallback_ReturnsError()
    {
        var settings = new AppSettings
        {
            OllamaBaseUrl = "http://localhost:99999",
            UsePaidProvider = false
        };
        var service = new LlmService(settings);
        var result = await service.GenerateAsync("Create a plan for research");
        result.Should().Contain("[LLM_UNAVAILABLE]");
    }

    [Fact]
    public async Task LlmService_SearchQueryFallback_ReturnsError()
    {
        var settings = new AppSettings
        {
            OllamaBaseUrl = "http://localhost:99999",
            UsePaidProvider = false
        };
        var service = new LlmService(settings);
        var result = await service.GenerateAsync("Generate search queries for topic X");
        result.Should().Contain("[LLM_UNAVAILABLE]");
    }

    [Fact]
    public async Task LlmService_GenerateWithTools_NoCloudProvider_FallsBackToGenerate()
    {
        var settings = new AppSettings
        {
            OllamaBaseUrl = "http://localhost:99999",
            UsePaidProvider = false,
            EnableToolCalling = true
        };

        var service = new LlmService(settings);
        var toolCalled = false;
        var result = await service.GenerateWithToolsAsync(
            "Create a plan for research",
            null,
            ResearchTools.All,
            tc => { toolCalled = true; return Task.FromResult(""); },
            CancellationToken.None);

        toolCalled.Should().BeFalse("no cloud provider = no tool calling");
        result.Should().Contain("[LLM_UNAVAILABLE]"); // clear error, not fake content
    }

    [Fact]
    public async Task LlmService_GenerateWithTools_ToolCallingDisabled_FallsBackToGenerate()
    {
        var settings = new AppSettings
        {
            OllamaBaseUrl = "http://localhost:99999",
            UsePaidProvider = true,
            PaidProvider = PaidProviderType.OpenAI,
            PaidProviderApiKey = "sk-test",
            EnableToolCalling = false
        };

        var service = new LlmService(settings);
        var toolCalled = false;
        var result = await service.GenerateWithToolsAsync(
            "Create a plan for research",
            null,
            ResearchTools.All,
            tc => { toolCalled = true; return Task.FromResult(""); },
            CancellationToken.None);

        toolCalled.Should().BeFalse("tool calling disabled");
        result.Should().NotBeNullOrEmpty();
    }

    #endregion
}
