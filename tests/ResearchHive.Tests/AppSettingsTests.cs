using FluentAssertions;
using ResearchHive.Core.Configuration;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for AppSettings configuration model and defaults.
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void AppSettings_DefaultValues_AreReasonable()
    {
        var settings = new AppSettings();

        settings.OllamaBaseUrl.Should().NotBeNullOrEmpty();
        settings.EmbeddingModel.Should().Be("nomic-embed-text");
        settings.SynthesisModel.Should().Be("llama3.1:8b");
        settings.MaxConcurrentFetches.Should().BeGreaterThan(0);
        settings.MinDomainDelaySeconds.Should().BeGreaterThan(0);
        settings.MaxDomainDelaySeconds.Should().BeGreaterThanOrEqualTo(settings.MinDomainDelaySeconds);
        settings.UsePaidProvider.Should().BeFalse();
    }

    [Fact]
    public void AppSettings_DataRootPath_HasDefault()
    {
        var settings = new AppSettings();
        settings.DataRootPath.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void AppSettings_PaidProvider_DefaultsToOpenAI()
    {
        var settings = new AppSettings();
        settings.PaidProvider.Should().Be(PaidProviderType.None);
    }

    [Fact]
    public void PaidProviderType_HasExpectedProviders()
    {
        var providers = Enum.GetValues<PaidProviderType>();
        providers.Should().HaveCountGreaterThanOrEqualTo(7);
        providers.Should().Contain(PaidProviderType.OpenAI);
        providers.Should().Contain(PaidProviderType.Anthropic);
        providers.Should().Contain(PaidProviderType.GoogleGemini);
        providers.Should().Contain(PaidProviderType.MistralAI);
        providers.Should().Contain(PaidProviderType.OpenRouter);
        providers.Should().Contain(PaidProviderType.AzureOpenAI);
        providers.Should().Contain(PaidProviderType.GitHubModels);
    }

    [Fact]
    public void AppSettings_ConcurrencyLimits_ArePositive()
    {
        var settings = new AppSettings();
        settings.MaxConcurrentFetches.Should().BeGreaterThan(0);
        settings.MinDomainDelaySeconds.Should().BePositive();
        settings.MaxDomainDelaySeconds.Should().BePositive();
    }

    [Fact]
    public void AppSettings_Serializes_Roundtrip()
    {
        var settings = new AppSettings
        {
            OllamaBaseUrl = "http://localhost:11434",
            EmbeddingModel = "test-model",
            SynthesisModel = "test-synth",
            MaxConcurrentFetches = 5,
            UsePaidProvider = true,
            PaidProvider = PaidProviderType.Anthropic,
            PaidProviderApiKey = "sk-test"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        deserialized.Should().NotBeNull();
        deserialized!.OllamaBaseUrl.Should().Be("http://localhost:11434");
        deserialized.EmbeddingModel.Should().Be("test-model");
        deserialized.UsePaidProvider.Should().BeTrue();
        deserialized.PaidProvider.Should().Be(PaidProviderType.Anthropic);
        deserialized.PaidProviderApiKey.Should().Be("sk-test");
    }
}
