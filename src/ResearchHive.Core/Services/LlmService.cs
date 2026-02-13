using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ResearchHive.Core.Services;

/// <summary>
/// LLM service for synthesis/planning. Supports:
/// - Local Ollama (primary by default)
/// - Cloud providers: OpenAI, Anthropic, Gemini, Mistral, OpenRouter, Azure, GitHub Models
/// - Configurable routing (LocalOnly / LocalWithCloudFallback / CloudPrimary / CloudOnly)
/// - Tool calling for research agents (search_evidence, search_web, get_source, verify_claim)
/// - Model tiering (Default/Mini/Full) for cost-efficient call routing
/// - Circuit breaker for provider fault isolation
/// - Exponential backoff with jitter for transient failures
/// </summary>
public class LlmService : ILlmService
{
    private readonly AppSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly CodexCliService? _codexCli;
    private readonly ILogger<LlmService>? _logger;
    private readonly LlmCircuitBreaker _circuitBreaker;
    private bool _ollamaAvailable = true;
    private DateTime _lastOllamaRetryTime = DateTime.MinValue;
    private static readonly TimeSpan OllamaRetryInterval = TimeSpan.FromSeconds(60);
    private static readonly Random _jitterRng = new();

    /// <summary>
    /// Fired after each Codex CLI call with the events captured during that call.
    /// Subscribers (e.g. ResearchJobRunner) surface these in the activity log.
    /// </summary>
    public event Action<IReadOnlyList<CodexEvent>>? CodexActivityOccurred;

    public LlmService(AppSettings settings, CodexCliService? codexCli = null,
        ILogger<LlmService>? logger = null, LlmCircuitBreaker? circuitBreaker = null)
    {
        _settings = settings;
        _codexCli = codexCli;
        _logger = logger;
        _circuitBreaker = circuitBreaker ?? new LlmCircuitBreaker();
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    /// <summary>True when the active provider is ChatGPT Plus with CodexOAuth and the CLI is available.</summary>
    public bool IsCodexOAuthActive =>
        _settings.UsePaidProvider
        && _settings.PaidProvider == PaidProviderType.ChatGptPlus
        && _settings.ChatGptPlusAuth == ChatGptPlusAuthMode.CodexOAuth
        && _codexCli is { IsAvailable: true, IsAuthenticated: true };

    /// <summary>The model name used in the most recent LLM call. Null if no call has been made yet.</summary>
    public string? LastModelUsed { get; private set; }

    /// <summary>
    /// True when the active routing strategy targets a cloud/Codex provider with large context windows (&gt;32K).
    /// Used by the scan pipeline to decide whether to consolidate multiple LLM calls into one.
    /// </summary>
    public bool IsLargeContextProvider =>
        IsCodexOAuthActive ||
        _settings.Routing == RoutingStrategy.CloudOnly ||
        _settings.Routing == RoutingStrategy.CloudPrimary;

    /// <summary>Notify subscribers of Codex events from the last call.</summary>
    private void RaiseCodexActivity()
    {
        if (_codexCli?.LastCallEvents is { Count: > 0 } events)
            CodexActivityOccurred?.Invoke(events);
    }

    /// <summary>
    /// Standard text generation — no tool calling.
    /// Routes based on <see cref="AppSettings.Routing"/>.
    /// </summary>
    /// <param name="tier">Model tier — Mini routes to cheaper models for routine tasks.</param>
    /// <param name="maxTokens">Optional token limit for Ollama (num_predict) and cloud (max_tokens). Defaults to 4000 for cloud, unbounded for local.</param>
    public async Task<string> GenerateAsync(string prompt, string? systemPrompt = null, int? maxTokens = null,
        ModelTier tier = ModelTier.Default, CancellationToken ct = default)
    {
        var response = await GenerateWithMetadataAsync(prompt, systemPrompt, maxTokens, tier, ct);
        return response.Text;
    }

    /// <summary>
    /// Text generation with truncation metadata. Returns <see cref="LlmResponse"/> containing
    /// the text, whether it was truncated, and the raw finish_reason from the provider.
    /// Automatically retries once with doubled token budget if truncation is detected and
    /// maxTokens was not explicitly set by the caller.
    /// </summary>
    public async Task<LlmResponse> GenerateWithMetadataAsync(string prompt, string? systemPrompt = null,
        int? maxTokens = null, ModelTier tier = ModelTier.Default, CancellationToken ct = default)
    {
        var modelOverride = ResolveModelOverride(tier);
        var result = await RouteGenerationAsync(prompt, systemPrompt, maxTokens, ct, modelOverride: modelOverride);
        
        // Auto-retry on truncation when caller didn't set an explicit limit
        if (result.WasTruncated && !maxTokens.HasValue)
        {
            var retryTokens = Math.Min(8000, (maxTokens ?? 4000) * 2);
            result = await RouteGenerationAsync(prompt, systemPrompt, retryTokens, ct, modelOverride: modelOverride);
        }

        LastModelUsed = result.ModelName;
        return result;
    }

    /// <summary>
    /// Generate with JSON format enforcement. For Ollama, this adds the <c>format: "json"</c>
    /// parameter to ensure valid JSON output. Cloud providers handle JSON naturally via prompt instructions.
    /// Use this when the prompt requests structured JSON output (e.g., complement evaluation, analysis).
    /// </summary>
    public async Task<string> GenerateJsonAsync(string prompt, string? systemPrompt = null, int? maxTokens = null,
        ModelTier tier = ModelTier.Default, CancellationToken ct = default)
    {
        var modelOverride = ResolveModelOverride(tier);
        var result = await RouteGenerationAsync(prompt, systemPrompt, maxTokens, ct, useJsonFormat: true, modelOverride: modelOverride);

        if (result.WasTruncated && !maxTokens.HasValue)
        {
            var retryTokens = Math.Min(8000, (maxTokens ?? 4000) * 2);
            result = await RouteGenerationAsync(prompt, systemPrompt, retryTokens, ct, useJsonFormat: true, modelOverride: modelOverride);
        }

        LastModelUsed = result.ModelName;
        return result.Text;
    }

    /// <summary>
    /// Agentic generation via Codex CLI with web search enabled.
    /// Makes a single agentic call where Codex autonomously searches the web.
    /// For non-Codex providers, falls back to standard GenerateAsync.
    /// </summary>
    public async Task<string> GenerateAgenticAsync(string prompt, string? systemPrompt = null,
        int timeoutSeconds = 300, CancellationToken ct = default)
    {
        if (!IsCodexOAuthActive || _codexCli == null)
        {
            _logger?.LogInformation("GenerateAgenticAsync: Codex not active, falling back to GenerateAsync");
            return await GenerateAsync(prompt, systemPrompt, ct: ct);
        }

        var fullPrompt = systemPrompt != null ? $"{systemPrompt}\n\n{prompt}" : prompt;
        _logger?.LogInformation("GenerateAgenticAsync: Invoking Codex with web search (timeout: {Timeout}s)", timeoutSeconds);

        // Retry with backoff: 2 attempts for agentic calls
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var result = await _codexCli.GenerateWithToolsAsync(
                    fullPrompt, enableSearch: true, sandbox: "read-only",
                    timeoutSeconds: timeoutSeconds, ct: ct);
                RaiseCodexActivity();
                if (result != null)
                {
                    _circuitBreaker.RecordCloudSuccess();
                    LastModelUsed = "codex-cli-agentic";
                    _logger?.LogInformation("GenerateAgenticAsync: Success on attempt {Attempt}, response {Length} chars",
                        attempt + 1, result.Length);
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "GenerateAgenticAsync: Attempt {Attempt} failed", attempt + 1);
            }

            if (attempt < 1)
            {
                var delay = CalculateBackoffDelay(attempt);
                _logger?.LogInformation("GenerateAgenticAsync: Retrying after {Delay}ms", delay);
                await Task.Delay(delay, ct);
            }
        }

        _circuitBreaker.RecordCloudFailure();
        _logger?.LogWarning("GenerateAgenticAsync: Both attempts failed, returning empty — caller will use fallback path");
        return string.Empty;
    }

    /// <summary>Resolve the mini model name for the current provider.</summary>
    public string? ResolveMiniModel()
    {
        if (IsCodexOAuthActive)
            return _settings.CodexMiniModel;
        var provider = ResolveActiveCloudProvider();
        if (provider != PaidProviderType.None && AppSettings.MiniModelMap.TryGetValue(provider, out var mini))
            return mini;
        return null; // Ollama — no mini variant, use SynthesisModel
    }

    private async Task<LlmResponse> RouteGenerationAsync(string prompt, string? systemPrompt, int? maxTokens,
        CancellationToken ct, bool useJsonFormat = false, string? modelOverride = null)
    {
        var routing = _settings.Routing;

        // Periodic retry: if Ollama was unavailable, re-check every 60s
        if (!_ollamaAvailable && (DateTime.UtcNow - _lastOllamaRetryTime) > OllamaRetryInterval)
        {
            _ollamaAvailable = true;
            _lastOllamaRetryTime = DateTime.UtcNow;
        }

        LlmResponse unavailable(string msg) => new(msg, false, "error", null);

        // Route based on strategy
        switch (routing)
        {
            case RoutingStrategy.LocalOnly:
            {
                if (!_circuitBreaker.AllowOllamaCall())
                    return unavailable("[LLM_UNAVAILABLE] Ollama circuit breaker is open — too many consecutive failures.");
                var localResult = await TryOllamaWithMetadata(prompt, systemPrompt, maxTokens, ct, useJsonFormat);
                if (localResult != null) return localResult;
                return unavailable("[LLM_UNAVAILABLE] Ollama is not running or did not respond. " +
                       "Start Ollama with a model loaded, or switch to a cloud provider in Settings.");
            }

            case RoutingStrategy.CloudOnly:
            {
                if (!_circuitBreaker.AllowCloudCall())
                    return unavailable("[LLM_UNAVAILABLE] Cloud circuit breaker is open — too many consecutive failures.");
                var cloudResult = await TryCloudWithMetadata(prompt, systemPrompt, maxTokens, ct, modelOverride);
                if (cloudResult != null) return cloudResult;
                return unavailable("[LLM_UNAVAILABLE] Cloud AI returned no response. Check your provider settings, authentication, and connectivity. " +
                       $"Provider: {_settings.PaidProvider}, Auth: {_settings.ChatGptPlusAuth}");
            }

            case RoutingStrategy.CloudPrimary:
            {
                var cpResult = _circuitBreaker.AllowCloudCall()
                    ? await TryCloudWithMetadata(prompt, systemPrompt, maxTokens, ct, modelOverride)
                    : null;
                cpResult ??= _circuitBreaker.AllowOllamaCall()
                    ? await TryOllamaWithMetadata(prompt, systemPrompt, maxTokens, ct, useJsonFormat)
                    : null;
                if (cpResult != null) return cpResult;
                return unavailable("[LLM_UNAVAILABLE] Neither cloud provider nor Ollama responded. " +
                       $"Cloud: {_settings.PaidProvider}, Auth: {_settings.ChatGptPlusAuth}. Check both connections.");
            }

            case RoutingStrategy.LocalWithCloudFallback:
            default:
            {
                var lcResult = _circuitBreaker.AllowOllamaCall()
                    ? await TryOllamaWithMetadata(prompt, systemPrompt, maxTokens, ct, useJsonFormat)
                    : null;
                lcResult ??= _circuitBreaker.AllowCloudCall()
                    ? await TryCloudWithMetadata(prompt, systemPrompt, maxTokens, ct, modelOverride)
                    : null;
                if (lcResult != null) return lcResult;
                return unavailable("[LLM_UNAVAILABLE] Neither Ollama nor cloud provider responded. " +
                       "Start Ollama or configure a cloud provider in Settings.");
            }
        }
    }

    /// <summary>
    /// Generate with tool calling support. The model can invoke tools and the caller
    /// provides results. Loops until the model produces a final text response or
    /// max tool calls are exhausted.
    /// </summary>
    /// <param name="prompt">User prompt</param>
    /// <param name="systemPrompt">System prompt</param>
    /// <param name="tools">Tool definitions (OpenAI format)</param>
    /// <param name="executeToolCall">Callback to execute a tool call and return the result string</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The model's final text response after tool use</returns>
    public async Task<string> GenerateWithToolsAsync(
        string prompt,
        string? systemPrompt,
        ToolDefinition[] tools,
        Func<ToolCall, Task<string>> executeToolCall,
        CancellationToken ct = default)
    {
        if (!_settings.EnableToolCalling || tools.Length == 0)
            return await GenerateAsync(prompt, systemPrompt, ct: ct);

        // Tool calling only works with chat-completion-compatible providers
        var provider = ResolveActiveCloudProvider();
        if (provider == PaidProviderType.None)
        {
            // Ollama doesn't support OpenAI-style tool calling — fall back to standard
            return await GenerateAsync(prompt, systemPrompt, ct: ct);
        }

        // ChatGPT Plus via Codex CLI — uses native agent tool calling (web search, commands)
        // rather than OpenAI API function-calling format (only in CodexOAuth mode)
        if (provider == PaidProviderType.ChatGptPlus
            && _settings.ChatGptPlusAuth == ChatGptPlusAuthMode.CodexOAuth)
        {
            if (_codexCli == null || !_codexCli.IsAvailable || !_codexCli.IsAuthenticated)
                return await GenerateAsync(prompt, systemPrompt, ct: ct);
            var fullPrompt = systemPrompt != null ? $"{systemPrompt}\n\n{prompt}" : prompt;

            // Streamlined mode: enable web search so Codex does its own research alongside our evidence.
            // Standard mode: disable web search so the model uses OUR evidence with proper [N] citations.
            var useWebSearch = _settings.StreamlinedCodexMode;

            // Try Codex, then retry once after a brief pause if it fails
            var result = useWebSearch
                ? await _codexCli.GenerateWithToolsAsync(fullPrompt, ct: ct)
                : await _codexCli.GenerateAsync(fullPrompt, ct: ct);
            RaiseCodexActivity();
            if (result != null) { LastModelUsed = "codex-cli"; return result; }
            // Retry once — transient failures (rate limits, token refresh) often resolve quickly
            await Task.Delay(2000, ct);
            result = useWebSearch
                ? await _codexCli.GenerateWithToolsAsync(fullPrompt, ct: ct)
                : await _codexCli.GenerateAsync(fullPrompt, ct: ct);
            RaiseCodexActivity();
            if (result != null) { LastModelUsed = "codex-cli"; return result; }
            return await GenerateAsync(prompt, systemPrompt, ct: ct);
        }

        var apiKey = ResolveApiKey();
        if (string.IsNullOrEmpty(apiKey))
            return await GenerateAsync(prompt, systemPrompt, ct: ct);

        var messages = new List<object>();
        if (systemPrompt != null)
            messages.Add(new { role = "system", content = systemPrompt });
        messages.Add(new { role = "user", content = prompt });

        var maxCalls = _settings.MaxToolCallsPerPhase;
        var toolCallCount = 0;

        // Anthropic has a different tool-calling format
        if (provider == PaidProviderType.Anthropic)
        {
            var anthropicResult = await AnthropicToolLoop(messages, tools, executeToolCall, apiKey, maxCalls, ct);
            LastModelUsed = _settings.PaidProviderModel ?? "claude-sonnet-4-20250514";
            return anthropicResult;
        }

        // OpenAI-compatible tool loop (OpenAI, GitHub Models, Mistral, OpenRouter, Azure)
        var (url, headers, modelName) = BuildCloudEndpoint(provider, apiKey);

        while (toolCallCount < maxCalls && !ct.IsCancellationRequested)
        {
            var chatReq = new
            {
                model = modelName,
                messages,
                temperature = 0.3,
                max_tokens = 4000,
                tools,
                tool_choice = "auto"
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            foreach (var (key, val) in headers)
                req.Headers.TryAddWithoutValidation(key, val);
            req.Content = new StringContent(
                JsonSerializer.Serialize(chatReq), Encoding.UTF8, "application/json");

            var resp = await _httpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var choice = doc.RootElement.GetProperty("choices")[0];
            var message = choice.GetProperty("message");

            // Check for tool calls
            if (message.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.GetArrayLength() > 0)
            {
                // Add assistant message with tool calls to conversation
                messages.Add(JsonSerializer.Deserialize<object>(message.GetRawText())!);

                foreach (var tcEl in toolCallsEl.EnumerateArray())
                {
                    var tc = JsonSerializer.Deserialize<ToolCall>(tcEl.GetRawText()) ?? new ToolCall();
                    toolCallCount++;

                    var result = await executeToolCall(tc);
                    messages.Add(new
                    {
                        role = "tool",
                        tool_call_id = tc.Id,
                        content = result
                    });
                }
            }
            else
            {
                // Final text response
                LastModelUsed = modelName;
                return message.TryGetProperty("content", out var content)
                    ? content.GetString() ?? ""
                    : "";
            }
        }

        // Max tool calls exhausted — ask the model for a final answer
        messages.Add(new { role = "user", content = "Please provide your final answer based on all the information gathered." });
        var finalReq = new { model = modelName, messages, temperature = 0.3, max_tokens = 4000 };
        using var finalHttpReq = new HttpRequestMessage(HttpMethod.Post, url);
        foreach (var (key, val) in headers)
            finalHttpReq.Headers.TryAddWithoutValidation(key, val);
        finalHttpReq.Content = new StringContent(
            JsonSerializer.Serialize(finalReq), Encoding.UTF8, "application/json");
        var finalResp = await _httpClient.SendAsync(finalHttpReq, ct);
        var finalJson = await finalResp.Content.ReadAsStringAsync(ct);
        using var finalDoc = JsonDocument.Parse(finalJson);
        LastModelUsed = modelName;
        return finalDoc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    #region Anthropic Tool Loop

    private async Task<string> AnthropicToolLoop(
        List<object> messages,
        ToolDefinition[] tools,
        Func<ToolCall, Task<string>> executeToolCall,
        string apiKey,
        int maxCalls,
        CancellationToken ct)
    {
        var endpoint = (_settings.PaidProviderEndpoint ?? "https://api.anthropic.com/v1") + "/messages";
        var model = _settings.PaidProviderModel ?? "claude-sonnet-4-20250514";
        var systemPrompt = "";
        var anthropicMessages = new List<object>();

        foreach (var msg in messages)
        {
            var msgJson = JsonSerializer.Serialize(msg);
            using var msgDoc = JsonDocument.Parse(msgJson);
            var role = msgDoc.RootElement.GetProperty("role").GetString();
            var content = msgDoc.RootElement.GetProperty("content").GetString();
            if (role == "system") systemPrompt = content ?? "";
            else anthropicMessages.Add(new { role, content });
        }

        var toolCallCount = 0;
        var anthropicTools = ResearchTools.ToAnthropicFormat();

        while (toolCallCount < maxCalls && !ct.IsCancellationRequested)
        {
            var reqBody = new
            {
                model,
                max_tokens = 4000,
                system = systemPrompt,
                messages = anthropicMessages,
                tools = anthropicTools
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = new StringContent(
                JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");

            var resp = await _httpClient.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var stopReason = doc.RootElement.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : "end_turn";

            if (stopReason == "tool_use")
            {
                var contentBlocks = doc.RootElement.GetProperty("content");
                var assistantContent = new List<object>();
                var toolResults = new List<object>();

                foreach (var block in contentBlocks.EnumerateArray())
                {
                    var blockType = block.GetProperty("type").GetString();
                    if (blockType == "text")
                    {
                        assistantContent.Add(new { type = "text", text = block.GetProperty("text").GetString() });
                    }
                    else if (blockType == "tool_use")
                    {
                        var toolId = block.GetProperty("id").GetString() ?? "";
                        var toolName = block.GetProperty("name").GetString() ?? "";
                        var toolInput = block.GetProperty("input").GetRawText();
                        assistantContent.Add(JsonSerializer.Deserialize<object>(block.GetRawText())!);

                        var tc = new ToolCall
                        {
                            Id = toolId,
                            Function = new ToolCallFunction
                            {
                                Name = toolName,
                                Arguments = toolInput
                            }
                        };
                        toolCallCount++;
                        var result = await executeToolCall(tc);
                        toolResults.Add(new
                        {
                            type = "tool_result",
                            tool_use_id = toolId,
                            content = result
                        });
                    }
                }

                anthropicMessages.Add(new { role = "assistant", content = assistantContent });
                anthropicMessages.Add(new { role = "user", content = toolResults });
            }
            else
            {
                // Final text response
                var contentArray = doc.RootElement.GetProperty("content");
                foreach (var block in contentArray.EnumerateArray())
                {
                    if (block.GetProperty("type").GetString() == "text")
                        return block.GetProperty("text").GetString() ?? "";
                }
                return "";
            }
        }

        return "Tool call limit reached. Please review the evidence gathered.";
    }

    #endregion

    #region Routing helpers

    private async Task<LlmResponse?> TryOllamaWithMetadata(string prompt, string? systemPrompt, int? maxTokens, CancellationToken ct, bool useJsonFormat = false)
    {
        if (!_ollamaAvailable) return null;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var result = await CallOllamaWithMetadataAsync(prompt, systemPrompt, maxTokens, ct, useJsonFormat);
                if (result != null)
                {
                    _circuitBreaker.RecordOllamaSuccess();
                    return result;
                }
                break;
            }
            catch when (attempt < 2)
            {
                var delay = CalculateBackoffDelay(attempt);
                _logger?.LogWarning("Ollama attempt {Attempt} failed, retrying in {Delay}ms", attempt + 1, delay);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Ollama attempt {Attempt} failed, marking unavailable", attempt + 1);
                _ollamaAvailable = false;
                _lastOllamaRetryTime = DateTime.UtcNow;
                _circuitBreaker.RecordOllamaFailure();
            }
        }
        return null;
    }

    private async Task<LlmResponse?> TryCloudWithMetadata(string prompt, string? systemPrompt, int? maxTokens, CancellationToken ct, string? modelOverride = null)
    {
        var provider = ResolveActiveCloudProvider();
        if (provider == PaidProviderType.None) return null;

        // ChatGPT Plus via Codex CLI — no API key needed, uses OAuth (only in CodexOAuth mode)
        if (provider == PaidProviderType.ChatGptPlus
            && _settings.ChatGptPlusAuth == ChatGptPlusAuthMode.CodexOAuth)
        {
            if (_codexCli == null || !_codexCli.IsAvailable || !_codexCli.IsAuthenticated) return null;
            var fullPrompt = systemPrompt != null ? $"{systemPrompt}\n\n{prompt}" : prompt;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var result = await _codexCli.GenerateAsync(fullPrompt, maxTokens, modelOverride, ct);
                    RaiseCodexActivity();
                    if (result != null)
                    {
                        _circuitBreaker.RecordCloudSuccess();
                        return new LlmResponse(result, false, "stop", modelOverride ?? "codex-cli");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Codex CLI attempt {Attempt} failed", attempt + 1);
                    RaiseCodexActivity();
                }
                if (attempt < 2)
                {
                    var delay = CalculateBackoffDelay(attempt);
                    await Task.Delay(delay, ct);
                }
            }
            _circuitBreaker.RecordCloudFailure();
            return null;
        }

        var apiKey = ResolveApiKey();
        if (string.IsNullOrEmpty(apiKey)) return null;

        try
        {
            var result = await CallCloudWithMetadataAsync(provider, apiKey, prompt, systemPrompt, maxTokens, ct);
            _circuitBreaker.RecordCloudSuccess();
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Cloud provider {Provider} call failed", provider);
            _circuitBreaker.RecordCloudFailure();
            return null;
        }
    }

    /// <summary>Legacy string-returning helpers used by tool calling path.</summary>
    private async Task<string?> TryOllama(string prompt, string? systemPrompt, int? maxTokens, CancellationToken ct)
    {
        if (!_ollamaAvailable) return null;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var result = await CallOllamaAsync(prompt, systemPrompt, maxTokens, ct);
                if (result != null) return result;
                break;
            }
            catch when (attempt == 0)
            {
                await Task.Delay(800, ct);
            }
            catch
            {
                _ollamaAvailable = false;
                _lastOllamaRetryTime = DateTime.UtcNow;
            }
        }
        return null;
    }

    private async Task<string?> TryCloud(string prompt, string? systemPrompt, int? maxTokens, CancellationToken ct)
    {
        var provider = ResolveActiveCloudProvider();
        if (provider == PaidProviderType.None) return null;

        // ChatGPT Plus via Codex CLI — no API key needed, uses OAuth (only in CodexOAuth mode)
        if (provider == PaidProviderType.ChatGptPlus
            && _settings.ChatGptPlusAuth == ChatGptPlusAuthMode.CodexOAuth)
        {
            if (_codexCli == null || !_codexCli.IsAvailable || !_codexCli.IsAuthenticated) return null;
            try
            {
                var fullPrompt = systemPrompt != null ? $"{systemPrompt}\n\n{prompt}" : prompt;
                // Regular generation: no web search — let the pipeline handle search/evidence
                var result = await _codexCli.GenerateAsync(fullPrompt, maxTokens, ct: ct);
                RaiseCodexActivity();
                if (result != null) return result;
                // Retry once after brief pause for transient failures
                await Task.Delay(1500, ct);
                result = await _codexCli.GenerateAsync(fullPrompt, maxTokens, ct: ct);
                RaiseCodexActivity();
                return result;
            }
            catch
            {
                RaiseCodexActivity();
                return null;
            }
        }

        var apiKey = ResolveApiKey();
        if (string.IsNullOrEmpty(apiKey)) return null;

        try
        {
            return await CallCloudProviderAsync(provider, apiKey, prompt, systemPrompt, maxTokens, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Determine the currently active cloud provider (if any).
    /// </summary>
    private PaidProviderType ResolveActiveCloudProvider()
    {
        if (!_settings.UsePaidProvider) return PaidProviderType.None;
        return _settings.PaidProvider;
    }

    /// <summary>
    /// Resolve the model override string for a given tier. Returns null for Default tier (use user's model).
    /// </summary>
    private string? ResolveModelOverride(ModelTier tier)
    {
        if (tier == ModelTier.Default) return null;
        if (tier == ModelTier.Mini) return ResolveMiniModel();
        // Full tier uses the user's configured model (no override needed)
        return null;
    }

    /// <summary>
    /// Calculate exponential backoff delay with jitter.
    /// Base: 1s, Factor: 2x, Max: 8s, Jitter: ±25%.
    /// </summary>
    private static int CalculateBackoffDelay(int attempt)
    {
        var baseDelay = 1000 * Math.Pow(2, attempt); // 1s, 2s, 4s, 8s...
        var capped = Math.Min(baseDelay, 8000);
        var jitter = capped * (0.75 + _jitterRng.NextDouble() * 0.5); // ±25%
        return (int)jitter;
    }

    /// <summary>
    /// Resolve the API key/token for the active cloud provider.
    /// </summary>
    private string? ResolveApiKey()
    {
        var provider = _settings.PaidProvider;

        // ChatGPT Plus uses OAuth via Codex CLI — no API key (CodexOAuth mode only)
        if (provider == PaidProviderType.ChatGptPlus
            && _settings.ChatGptPlusAuth == ChatGptPlusAuthMode.CodexOAuth)
            return "codex-oauth"; // Sentinel value — actual auth is handled by CodexCliService

        // ChatGPT Plus with ApiKey mode — treat like OpenAI
        if (provider == PaidProviderType.ChatGptPlus
            && _settings.ChatGptPlusAuth == ChatGptPlusAuthMode.ApiKey)
        {
            if (_settings.KeySource == ApiKeySource.EnvironmentVariable && !string.IsNullOrEmpty(_settings.KeyEnvironmentVariable))
                return Environment.GetEnvironmentVariable(_settings.KeyEnvironmentVariable);
            return _settings.PaidProviderApiKey;
        }

        // GitHub Models uses the GitHub PAT
        if (provider == PaidProviderType.GitHubModels)
            return _settings.GitHubPat;

        // Environment variable source
        if (_settings.KeySource == ApiKeySource.EnvironmentVariable && !string.IsNullOrEmpty(_settings.KeyEnvironmentVariable))
            return Environment.GetEnvironmentVariable(_settings.KeyEnvironmentVariable);

        // Direct key from settings
        return _settings.PaidProviderApiKey;
    }

    #endregion

    #region Provider calls

    private async Task<string?> CallOllamaAsync(string prompt, string? systemPrompt, int? maxTokens, CancellationToken ct)
    {
        var options = new Dictionary<string, object>
        {
            ["temperature"] = 0.3,
            ["num_ctx"] = _settings.LocalContextSize
        };
        if (maxTokens.HasValue)
            options["num_predict"] = maxTokens.Value;

        var request = new
        {
            model = _settings.SynthesisModel,
            prompt = prompt,
            system = systemPrompt ?? "You are a research assistant. Provide thorough, evidence-based answers with clear citations.",
            stream = false,
            options
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"{_settings.OllamaBaseUrl}/api/generate", request, ct);

        if (!response.IsSuccessStatusCode)
        {
            _ollamaAvailable = false;
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("response", out var resp) ? resp.GetString() : null;
    }

    /// <summary>Ollama call with truncation detection via done_reason. Supports JSON format enforcement.</summary>
    private async Task<LlmResponse?> CallOllamaWithMetadataAsync(string prompt, string? systemPrompt, int? maxTokens, CancellationToken ct, bool useJsonFormat = false)
    {
        var options = new Dictionary<string, object>
        {
            ["temperature"] = 0.3,
            ["num_ctx"] = _settings.LocalContextSize
        };
        if (maxTokens.HasValue)
            options["num_predict"] = maxTokens.Value;

        // Build request body — use dictionary to conditionally add 'format' field for JSON mode
        var requestBody = new Dictionary<string, object>
        {
            ["model"] = _settings.SynthesisModel,
            ["prompt"] = prompt,
            ["system"] = systemPrompt ?? "You are a research assistant. Provide thorough, evidence-based answers with clear citations.",
            ["stream"] = false,
            ["options"] = options
        };

        // Ollama JSON format mode: ensures model output is valid JSON (helps smaller models produce reliable structured output)
        if (useJsonFormat)
            requestBody["format"] = "json";

        var response = await _httpClient.PostAsJsonAsync(
            $"{_settings.OllamaBaseUrl}/api/generate", requestBody, ct);

        if (!response.IsSuccessStatusCode)
        {
            _ollamaAvailable = false;
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var text = doc.RootElement.TryGetProperty("response", out var resp) ? resp.GetString() : null;
        if (text == null) return null;

        // Ollama returns done_reason: "stop" (complete) or "length" (truncated)
        var doneReason = doc.RootElement.TryGetProperty("done_reason", out var dr) ? dr.GetString() : "stop";
        var wasTruncated = string.Equals(doneReason, "length", StringComparison.OrdinalIgnoreCase);

        return new LlmResponse(text, wasTruncated, doneReason, _settings.SynthesisModel);
    }

    /// <summary>Cloud provider call with truncation detection via finish_reason / stop_reason.</summary>
    private async Task<LlmResponse> CallCloudWithMetadataAsync(
        PaidProviderType provider, string apiKey,
        string prompt, string? systemPrompt, int? maxTokens, CancellationToken ct)
    {
        switch (provider)
        {
            case PaidProviderType.Anthropic:
            {
                var url = (_settings.PaidProviderEndpoint ?? "https://api.anthropic.com/v1") + "/messages";
                var model = _settings.PaidProviderModel ?? "claude-sonnet-4-20250514";
                var anthropicReq = new
                {
                    model,
                    max_tokens = maxTokens ?? 4000,
                    system = systemPrompt ?? "You are a research assistant.",
                    messages = new[] { new { role = "user", content = prompt } }
                };
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("x-api-key", apiKey);
                req.Headers.Add("anthropic-version", "2023-06-01");
                req.Content = new StringContent(
                    JsonSerializer.Serialize(anthropicReq), Encoding.UTF8, "application/json");
                var resp = await _httpClient.SendAsync(req, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
                // Anthropic: stop_reason = "end_turn" (complete) or "max_tokens" (truncated)
                var stopReason = doc.RootElement.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : "end_turn";
                var truncated = string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase);
                return new LlmResponse(text, truncated, stopReason, model);
            }

            case PaidProviderType.GoogleGemini:
            {
                var model = _settings.PaidProviderModel ?? "gemini-pro";
                var url = (_settings.PaidProviderEndpoint ?? "https://generativelanguage.googleapis.com/v1beta")
                    + $"/models/{model}:generateContent";
                var geminiReq = new
                {
                    contents = new[] { new { parts = new[] { new { text = (systemPrompt != null ? systemPrompt + "\n\n" : "") + prompt } } } },
                    generationConfig = new { maxOutputTokens = maxTokens ?? 4096 }
                };
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("x-goog-api-key", apiKey);
                req.Content = new StringContent(
                    JsonSerializer.Serialize(geminiReq), Encoding.UTF8, "application/json");
                var resp = await _httpClient.SendAsync(req, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var text = doc.RootElement.GetProperty("candidates")[0]
                    .GetProperty("content").GetProperty("parts")[0]
                    .GetProperty("text").GetString() ?? "";
                // Gemini: finishReason = "STOP" (complete) or "MAX_TOKENS" (truncated)
                var finishReason = doc.RootElement.GetProperty("candidates")[0]
                    .TryGetProperty("finishReason", out var fr) ? fr.GetString() : "STOP";
                var truncated = string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase);
                return new LlmResponse(text, truncated, finishReason, model);
            }

            // OpenAI, MistralAI, OpenRouter, AzureOpenAI, GitHubModels — all OpenAI-compatible
            default:
            {
                var (url, headers, modelName) = BuildCloudEndpoint(provider, apiKey);
                var messages = new List<object>();
                if (systemPrompt != null)
                    messages.Add(new { role = "system", content = systemPrompt });
                messages.Add(new { role = "user", content = prompt });
                var chatReq = new { model = modelName, messages, temperature = 0.3, max_tokens = maxTokens ?? 4000 };
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                foreach (var (key, val) in headers)
                    req.Headers.TryAddWithoutValidation(key, val);
                req.Content = new StringContent(
                    JsonSerializer.Serialize(chatReq), Encoding.UTF8, "application/json");
                var resp = await _httpClient.SendAsync(req, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var text = doc.RootElement.GetProperty("choices")[0]
                    .GetProperty("message").GetProperty("content").GetString() ?? "";
                // OpenAI-compat: finish_reason = "stop" (complete) or "length" (truncated)
                var finishReason = doc.RootElement.GetProperty("choices")[0]
                    .TryGetProperty("finish_reason", out var fr) ? fr.GetString() : "stop";
                var truncated = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase);
                return new LlmResponse(text, truncated, finishReason, modelName);
            }
        }
    }

    private async Task<string> CallCloudProviderAsync(
        PaidProviderType provider, string apiKey,
        string prompt, string? systemPrompt, int? maxTokens, CancellationToken ct)
    {
        switch (provider)
        {
            case PaidProviderType.Anthropic:
            {
                var url = (_settings.PaidProviderEndpoint ?? "https://api.anthropic.com/v1") + "/messages";
                var model = _settings.PaidProviderModel ?? "claude-sonnet-4-20250514";
                var anthropicReq = new
                {
                    model,
                    max_tokens = maxTokens ?? 4000,
                    system = systemPrompt ?? "You are a research assistant.",
                    messages = new[] { new { role = "user", content = prompt } }
                };
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("x-api-key", apiKey);
                req.Headers.Add("anthropic-version", "2023-06-01");
                req.Content = new StringContent(
                    JsonSerializer.Serialize(anthropicReq), Encoding.UTF8, "application/json");
                var resp = await _httpClient.SendAsync(req, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
            }

            case PaidProviderType.GoogleGemini:
            {
                var model = _settings.PaidProviderModel ?? "gemini-pro";
                var url = (_settings.PaidProviderEndpoint ?? "https://generativelanguage.googleapis.com/v1beta")
                    + $"/models/{model}:generateContent";
                var geminiReq = new
                {
                    contents = new[] { new { parts = new[] { new { text = (systemPrompt != null ? systemPrompt + "\n\n" : "") + prompt } } } },
                    generationConfig = new { maxOutputTokens = maxTokens ?? 4096 }
                };
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("x-goog-api-key", apiKey);
                req.Content = new StringContent(
                    JsonSerializer.Serialize(geminiReq), Encoding.UTF8, "application/json");
                var resp = await _httpClient.SendAsync(req, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("candidates")[0]
                    .GetProperty("content").GetProperty("parts")[0]
                    .GetProperty("text").GetString() ?? "";
            }

            // OpenAI, MistralAI, OpenRouter, AzureOpenAI, GitHubModels — all OpenAI-compatible
            default:
            {
                var (url, headers, modelName) = BuildCloudEndpoint(provider, apiKey);
                var messages = new List<object>();
                if (systemPrompt != null)
                    messages.Add(new { role = "system", content = systemPrompt });
                messages.Add(new { role = "user", content = prompt });
                var chatReq = new { model = modelName, messages, temperature = 0.3, max_tokens = maxTokens ?? 4000 };
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                foreach (var (key, val) in headers)
                    req.Headers.TryAddWithoutValidation(key, val);
                req.Content = new StringContent(
                    JsonSerializer.Serialize(chatReq), Encoding.UTF8, "application/json");
                var resp = await _httpClient.SendAsync(req, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("choices")[0]
                    .GetProperty("message").GetProperty("content").GetString() ?? "";
            }
        }
    }

    /// <summary>
    /// Build the endpoint URL, auth headers, and model name for an OpenAI-compatible provider.
    /// </summary>
    private (string url, List<(string key, string val)> headers, string model) BuildCloudEndpoint(
        PaidProviderType provider, string apiKey)
    {
        var headers = new List<(string key, string val)>();

        switch (provider)
        {
            case PaidProviderType.GitHubModels:
                headers.Add(("Authorization", $"Bearer {apiKey}"));
                headers.Add(("Accept", "application/vnd.github+json"));
                headers.Add(("X-GitHub-Api-Version", "2022-11-28"));
                return (
                    "https://models.github.ai/inference/chat/completions",
                    headers,
                    _settings.PaidProviderModel ?? "openai/gpt-4o"
                );

            case PaidProviderType.MistralAI:
                headers.Add(("Authorization", $"Bearer {apiKey}"));
                return (
                    (_settings.PaidProviderEndpoint ?? "https://api.mistral.ai/v1") + "/chat/completions",
                    headers,
                    _settings.PaidProviderModel ?? "mistral-large-latest"
                );

            case PaidProviderType.OpenRouter:
                headers.Add(("Authorization", $"Bearer {apiKey}"));
                return (
                    (_settings.PaidProviderEndpoint ?? "https://openrouter.ai/api/v1") + "/chat/completions",
                    headers,
                    _settings.PaidProviderModel ?? "openai/gpt-4o"
                );

            case PaidProviderType.AzureOpenAI:
                headers.Add(("api-key", apiKey));
                return (
                    (_settings.PaidProviderEndpoint ?? "https://api.openai.com/v1") + "/chat/completions",
                    headers,
                    _settings.PaidProviderModel ?? "gpt-4o"
                );

            case PaidProviderType.ChatGptPlus: // ApiKey mode — same as OpenAI
                headers.Add(("Authorization", $"Bearer {apiKey}"));
                return (
                    (_settings.PaidProviderEndpoint ?? "https://api.openai.com/v1") + "/chat/completions",
                    headers,
                    _settings.PaidProviderModel ?? "gpt-4o"
                );

            default: // OpenAI
                headers.Add(("Authorization", $"Bearer {apiKey}"));
                return (
                    (_settings.PaidProviderEndpoint ?? "https://api.openai.com/v1") + "/chat/completions",
                    headers,
                    _settings.PaidProviderModel ?? "gpt-4o"
                );
        }
    }

    #endregion

    #region Deterministic fallback

    private static string GenerateDeterministicResponse(string prompt)
    {
        var sb = new StringBuilder();

        if (prompt.Contains("plan", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("## Research Plan");
            sb.AppendLine();
            sb.AppendLine("1. Search for primary authoritative sources on the topic");
            sb.AppendLine("2. Gather peer-reviewed and academic sources");
            sb.AppendLine("3. Identify contrarian/alternative viewpoints");
            sb.AppendLine("4. Compare and evaluate evidence quality");
            sb.AppendLine("5. Synthesize findings with proper citations");
        }
        else if (prompt.Contains("search queries", StringComparison.OrdinalIgnoreCase))
        {
            var words = prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var topic = string.Join(' ', words.Take(Math.Min(10, words.Length)));
            sb.AppendLine($"1. \"{topic}\" authoritative sources");
            sb.AppendLine($"2. \"{topic}\" academic research");
            sb.AppendLine($"3. \"{topic}\" alternative perspectives");
            sb.AppendLine($"4. \"{topic}\" recent developments");
            sb.AppendLine($"5. \"{topic}\" critical analysis");
        }
        else if (prompt.Contains("synthesize", StringComparison.OrdinalIgnoreCase) ||
                 prompt.Contains("report", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("## Most Supported View");
            sb.AppendLine();
            sb.AppendLine("Based on the evidence gathered, the most supported view is constructed from the available sources.");
            sb.AppendLine("Each claim below is supported by the referenced evidence.");
            sb.AppendLine();
            sb.AppendLine("## Credible Alternatives / Broader Views");
            sb.AppendLine();
            sb.AppendLine("Alternative interpretations exist and are worth considering:");
            sb.AppendLine("- Different methodological approaches may yield varying conclusions");
            sb.AppendLine("- The evidence base has inherent limitations");
            sb.AppendLine();
            sb.AppendLine("## Limitations");
            sb.AppendLine();
            sb.AppendLine("- Evidence quality varies across sources");
            sb.AppendLine("- Some claims remain hypotheses pending further investigation");
        }
        else
        {
            sb.AppendLine("Based on available evidence and analysis:");
            sb.AppendLine();
            sb.AppendLine("The requested information has been processed using deterministic extraction.");
            sb.AppendLine("For enhanced synthesis quality, connect an LLM provider (Ollama recommended).");
        }

        return sb.ToString();
    }

    #endregion

    #region Discovery / utility

    public async Task<bool> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_settings.OllamaBaseUrl}/api/tags", ct);
            _ollamaAvailable = response.IsSuccessStatusCode;
            return _ollamaAvailable;
        }
        catch
        {
            _ollamaAvailable = false;
            return false;
        }
    }

    /// <summary>
    /// Lists all locally-available Ollama model names by calling GET /api/tags.
    /// Returns an empty list if Ollama is unreachable.
    /// </summary>
    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var models = new List<string>();
        try
        {
            var response = await _httpClient.GetAsync($"{_settings.OllamaBaseUrl}/api/tags", ct);
            if (!response.IsSuccessStatusCode) return models;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var modelsArray))
            {
                foreach (var m in modelsArray.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var name))
                        models.Add(name.GetString() ?? "");
                }
            }
        }
        catch { /* Ollama not reachable */ }
        return models;
    }

    /// <summary>
    /// Validate that an API key works for the given provider by making a minimal test request.
    /// Returns true if the key is accepted, false otherwise.
    /// </summary>
    public async Task<(bool valid, string message)> ValidateApiKeyAsync(
        PaidProviderType provider, string apiKey, CancellationToken ct = default)
    {
        try
        {
            switch (provider)
            {
                case PaidProviderType.ChatGptPlus:
                {
                    // CodexOAuth mode — validate via Codex CLI
                    if (_settings.ChatGptPlusAuth == ChatGptPlusAuthMode.CodexOAuth)
                    {
                        if (_codexCli == null)
                            return (false, "CodexCliService not configured.");
                        return await _codexCli.ValidateAsync(ct);
                    }
                    // ApiKey mode — validate like OpenAI
                    var endpoint = _settings.PaidProviderEndpoint ?? "https://api.openai.com/v1";
                    var modelsUrlCgpt = endpoint.TrimEnd('/') + "/models";
                    using var reqCgpt = new HttpRequestMessage(HttpMethod.Get, modelsUrlCgpt);
                    reqCgpt.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
                    var respCgpt = await _httpClient.SendAsync(reqCgpt, ct);
                    return respCgpt.IsSuccessStatusCode
                        ? (true, "OpenAI API key valid!")
                        : (false, $"HTTP {(int)respCgpt.StatusCode}: Invalid API key.");
                }

                case PaidProviderType.GitHubModels:
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post,
                        "https://models.github.ai/inference/chat/completions");
                    req.Headers.Add("Authorization", $"Bearer {apiKey}");
                    req.Headers.Add("Accept", "application/vnd.github+json");
                    req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                    req.Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        model = "openai/gpt-4o-mini",
                        messages = new[] { new { role = "user", content = "Hello" } },
                        max_tokens = 5
                    }), Encoding.UTF8, "application/json");
                    var resp = await _httpClient.SendAsync(req, ct);
                    return resp.IsSuccessStatusCode
                        ? (true, "GitHub Models connection successful!")
                        : (false, $"HTTP {(int)resp.StatusCode}: Check your GitHub PAT has 'models:read' scope.");
                }

                case PaidProviderType.Anthropic:
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post,
                        (_settings.PaidProviderEndpoint ?? "https://api.anthropic.com/v1") + "/messages");
                    req.Headers.Add("x-api-key", apiKey);
                    req.Headers.Add("anthropic-version", "2023-06-01");
                    req.Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        model = "claude-haiku-4-20250514",
                        max_tokens = 5,
                        messages = new[] { new { role = "user", content = "Hi" } }
                    }), Encoding.UTF8, "application/json");
                    var resp = await _httpClient.SendAsync(req, ct);
                    return resp.IsSuccessStatusCode
                        ? (true, "Anthropic API key valid!")
                        : (false, $"HTTP {(int)resp.StatusCode}: Invalid API key.");
                }

                case PaidProviderType.GoogleGemini:
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        (_settings.PaidProviderEndpoint ?? "https://generativelanguage.googleapis.com/v1beta")
                        + "/models");
                    req.Headers.Add("x-goog-api-key", apiKey);
                    var resp = await _httpClient.SendAsync(req, ct);
                    return resp.IsSuccessStatusCode
                        ? (true, "Google Gemini API key valid!")
                        : (false, $"HTTP {(int)resp.StatusCode}: Invalid API key.");
                }

                default: // OpenAI-compatible (OpenAI, Mistral, OpenRouter, Azure)
                {
                    var (url, headers, modelName) = BuildCloudEndpoint(provider, apiKey);
                    // Use the models endpoint for validation (lighter than chat)
                    var modelsUrl = url.Replace("/chat/completions", "/models");
                    using var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                    foreach (var (key, val) in headers)
                        req.Headers.TryAddWithoutValidation(key, val);
                    var resp = await _httpClient.SendAsync(req, ct);
                    return resp.IsSuccessStatusCode
                        ? (true, $"{provider} API key valid!")
                        : (false, $"HTTP {(int)resp.StatusCode}: Invalid API key.");
                }
            }
        }
        catch (Exception ex)
        {
            return (false, $"Connection error: {ex.Message}");
        }
    }

    #endregion
}
