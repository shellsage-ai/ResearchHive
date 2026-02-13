using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;

namespace ResearchHive.Core.Services;

/// <summary>
/// Abstraction for LLM generation. Enables mocking in tests and future provider swapping.
/// Supports tiered model routing (Default/Mini/Full) for cost-efficient call distribution.
/// </summary>
public interface ILlmService
{
    /// <summary>True when the active provider is ChatGPT Plus with CodexOAuth and the CLI is available.</summary>
    bool IsCodexOAuthActive { get; }

    /// <summary>True when the active routing strategy targets a cloud/Codex provider with large context windows.</summary>
    bool IsLargeContextProvider { get; }

    /// <summary>The model name used in the most recent LLM call.</summary>
    string? LastModelUsed { get; }

    /// <summary>Fired after each Codex CLI call with captured events.</summary>
    event Action<IReadOnlyList<CodexEvent>>? CodexActivityOccurred;

    /// <summary>Standard text generation with configurable model tier.</summary>
    Task<string> GenerateAsync(string prompt, string? systemPrompt = null, int? maxTokens = null,
        ModelTier tier = ModelTier.Default, CancellationToken ct = default);

    /// <summary>Text generation with truncation metadata.</summary>
    Task<LlmResponse> GenerateWithMetadataAsync(string prompt, string? systemPrompt = null,
        int? maxTokens = null, ModelTier tier = ModelTier.Default, CancellationToken ct = default);

    /// <summary>Generate with JSON format enforcement.</summary>
    Task<string> GenerateJsonAsync(string prompt, string? systemPrompt = null, int? maxTokens = null,
        ModelTier tier = ModelTier.Default, CancellationToken ct = default);

    /// <summary>Generate with tool calling support (OpenAI function-calling / Codex agent).</summary>
    Task<string> GenerateWithToolsAsync(string prompt, string? systemPrompt,
        ToolDefinition[] tools, Func<ToolCall, Task<string>> executeToolCall,
        CancellationToken ct = default);

    /// <summary>
    /// Agentic generation via Codex CLI with web search enabled. For non-Codex providers,
    /// falls back to standard GenerateAsync. Used for the single-call scan pipeline where
    /// Codex autonomously searches the web for complementary projects.
    /// </summary>
    Task<string> GenerateAgenticAsync(string prompt, string? systemPrompt = null,
        int timeoutSeconds = 300, CancellationToken ct = default);

    /// <summary>Resolve the mini model name for the current provider.</summary>
    string? ResolveMiniModel();
}
