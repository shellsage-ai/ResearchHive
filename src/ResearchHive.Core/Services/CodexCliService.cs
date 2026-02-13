using ResearchHive.Core.Configuration;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace ResearchHive.Core.Services;

/// <summary>
/// Wraps the OpenAI Codex CLI (v0.98+ Rust binary) to provide LLM generation
/// with full agent capabilities via ChatGPT Plus OAuth. 
///
/// The Codex CLI is a full coding agent — it natively performs:
///   • Web search (via -c web_search="live")
///   • Shell command execution (via --full-auto / --sandbox workspace-write)
///   • File reading and editing
///   • Structured JSON output (via --output-schema)
///   • Reasoning traces
///   • MCP tool integration
///
/// Authentication is via ChatGPT Plus OAuth — no API key required.
/// Binary path: discovered via codex.exe in the npm vendor directory.
/// JSONL event types: thread.started, turn.started, item.started, item.completed,
///                    turn.completed, turn.failed, error
/// Item types: agent_message, reasoning, web_search, command_execution, file_change
/// </summary>
public class CodexCliService
{
    private readonly AppSettings _settings;
    private bool? _isAvailable;
    private bool? _isAuthenticated;
    private string? _codexBinaryPath;

    // Track web search results and commands from the last call for activity logging
    private readonly List<CodexEvent> _lastCallEvents = new();

    public CodexCliService(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>Events captured from the most recent Codex CLI call.</summary>
    public IReadOnlyList<CodexEvent> LastCallEvents => _lastCallEvents;

    /// <summary>Resolve the path to the codex.exe binary (Rust).</summary>
    private string? ResolveBinaryPath()
    {
        if (_codexBinaryPath != null) return _codexBinaryPath;

        // 1. Try the npm vendor path (installed via npm i -g @openai/codex)
        var npmRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm", "node_modules", "@openai", "codex", "vendor",
            "x86_64-pc-windows-msvc", "codex", "codex.exe");
        if (File.Exists(npmRoot)) { _codexBinaryPath = npmRoot; return npmRoot; }

        // 2. Try ARM64 variant
        var npmArm = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm", "node_modules", "@openai", "codex", "vendor",
            "aarch64-pc-windows-msvc", "codex", "codex.exe");
        if (File.Exists(npmArm)) { _codexBinaryPath = npmArm; return npmArm; }

        // 3. Check if codex.exe is on PATH
        try
        {
            var psi = new ProcessStartInfo("codex", "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                p.WaitForExit(5000);
                if (p.ExitCode == 0)
                {
                    _codexBinaryPath = "codex";
                    return "codex";
                }
            }
        }
        catch { /* not on PATH */ }

        // 4. Legacy: try node.exe + codex.js (TypeScript version)
        if (File.Exists(_settings.CodexNodePath) && File.Exists(_settings.CodexScriptPath))
        {
            _codexBinaryPath = "node-legacy";
            return "node-legacy";
        }

        return null;
    }

    /// <summary>Check if the Codex CLI binary is present on this machine.</summary>
    public bool IsAvailable
    {
        get
        {
            _isAvailable ??= ResolveBinaryPath() != null;
            return _isAvailable.Value;
        }
    }

    /// <summary>Check if the user is authenticated (ChatGPT OAuth or API key).</summary>
    public bool IsAuthenticated
    {
        get
        {
            if (_isAuthenticated.HasValue) return _isAuthenticated.Value;
            try
            {
                // Check for ChatGPT OAuth tokens
                var codexDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".codex");
                
                // auth.json contains the OAuth tokens
                var authPath = Path.Combine(codexDir, "auth.json");
                if (File.Exists(authPath))
                {
                    var json = File.ReadAllText(authPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("tokens", out var tokens)
                        && tokens.TryGetProperty("refresh_token", out var rt)
                        && !string.IsNullOrEmpty(rt.GetString()))
                    {
                        _isAuthenticated = true;
                        return true;
                    }
                }

                // Also check for API key in environment (CODEX_API_KEY or OPENAI_API_KEY)
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEX_API_KEY"))
                    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
                {
                    _isAuthenticated = true;
                    return true;
                }

                _isAuthenticated = false;
                return false;
            }
            catch { _isAuthenticated = false; return false; }
        }
    }

    /// <summary>
    /// Generate a response using Codex CLI (read-only sandbox, no web search).
    /// Best for: synthesis, summarization, analysis prompts where no external tools needed.
    /// </summary>
    /// <param name="modelOverride">If set, override the user's model selection for this call (e.g. mini model for routine tasks).</param>
    public async Task<string?> GenerateAsync(string prompt, int? maxTokens = null, string? modelOverride = null, CancellationToken ct = default)
    {
        return await RunCodexExecAsync(prompt, maxTokens: maxTokens,
            enableSearch: false, sandbox: "read-only", modelOverride: modelOverride, ct: ct);
    }

    /// <summary>
    /// Generate a response with full agent capabilities: web search + command execution.
    /// The Codex model natively decides when to search the web and execute code.
    /// Best for: research tasks that need live web data, grounding, fact-checking.
    /// </summary>
    /// <param name="modelOverride">If set, override the user's model selection for this call.</param>
    public async Task<string?> GenerateWithToolsAsync(string prompt, int? maxTokens = null,
        bool enableSearch = true, string sandbox = "read-only", string? modelOverride = null, CancellationToken ct = default)
    {
        return await RunCodexExecAsync(prompt, maxTokens: maxTokens,
            enableSearch: enableSearch, sandbox: sandbox, modelOverride: modelOverride, ct: ct);
    }

    /// <summary>
    /// Generate a structured JSON response conforming to a schema.
    /// The Codex CLI validates the output against the schema.
    /// </summary>
    /// <param name="modelOverride">If set, override the user's model selection for this call.</param>
    public async Task<string?> GenerateStructuredAsync(string prompt, string jsonSchema,
        bool enableSearch = true, string? modelOverride = null, CancellationToken ct = default)
    {
        // Write schema to a temp file (Codex CLI reads it from disk)
        var schemaPath = Path.Combine(Path.GetTempPath(), $"codex_schema_{Guid.NewGuid():N}.json");
        try
        {
            // Write without BOM — Codex requires clean UTF-8
            await File.WriteAllTextAsync(schemaPath, jsonSchema, new UTF8Encoding(false), ct);
            return await RunCodexExecAsync(prompt, enableSearch: enableSearch,
                sandbox: "read-only", outputSchemaPath: schemaPath, modelOverride: modelOverride, ct: ct);
        }
        finally
        {
            try { File.Delete(schemaPath); } catch { /* cleanup */ }
        }
    }

    /// <summary>
    /// Core execution method. Invokes `codex exec --json` and parses the JSONL event stream.
    ///
    /// JSONL event types emitted by Codex CLI:
    ///   thread.started  — { thread_id }
    ///   turn.started    — (no payload)
    ///   item.started    — { item: { id, type, ... } }  (for in-progress items like web_search)
    ///   item.completed  — { item: { id, type, text|query|command|... } }
    ///   turn.completed  — { usage: { input_tokens, cached_input_tokens, output_tokens } }
    ///   turn.failed     — { error: { message } }
    ///   error           — { message }
    ///
    /// Item types: agent_message, reasoning, web_search, command_execution, file_change
    /// </summary>
    private async Task<string?> RunCodexExecAsync(
        string prompt,
        int? maxTokens = null,
        bool enableSearch = false,
        string sandbox = "read-only",
        string? outputSchemaPath = null,
        string? modelOverride = null,
        int timeoutSeconds = 180,
        CancellationToken ct = default)
    {
        if (!IsAvailable || !IsAuthenticated) return null;

        _lastCallEvents.Clear();

        var binaryPath = ResolveBinaryPath()!;
        var isLegacy = binaryPath == "node-legacy";

        // If maxTokens is specified, nudge the prompt
        var effectivePrompt = prompt;
        if (maxTokens.HasValue && maxTokens.Value < 2000)
            effectivePrompt = $"[Keep your response under ~{maxTokens.Value} tokens.]\n\n{prompt}";

        // Build arguments
        var args = new StringBuilder();

        if (isLegacy)
        {
            args.Append($"\"{_settings.CodexScriptPath}\" ");
        }

        args.Append("exec --json");
        args.Append($" --sandbox {sandbox}");
        args.Append(" --skip-git-repo-check");

        // Model override: explicit per-call override > user's PaidProviderModel > Codex CLI default
        var model = modelOverride ?? _settings.PaidProviderModel;
        if (!string.IsNullOrEmpty(model))
            args.Append($" --model {model}");

        // Enable full-auto if sandbox allows writes (combines -a on-request + --sandbox workspace-write)
        if (sandbox == "workspace-write")
            args.Append(" --full-auto");

        // Web search: live or cached
        if (enableSearch)
            args.Append(" -c web_search=\"live\"");

        // Structured output schema
        if (!string.IsNullOrEmpty(outputSchemaPath))
            args.Append($" --output-schema \"{outputSchemaPath}\"");

        // Pipe prompt through stdin instead of CLI arguments.
        // This avoids Windows command-line escaping issues (broken backslash-quote sequences,
        // literal \n text instead of actual newlines, 32K arg limit) that cause Codex CLI
        // to receive garbled prompts — especially for long synthesis prompts with evidence text.
        // Codex CLI's resolve_prompt() reads from stdin when no positional prompt arg is given.
        var psi = new ProcessStartInfo
        {
            FileName = isLegacy ? _settings.CodexNodePath : binaryPath,
            Arguments = args.ToString(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = _settings.DataRootPath
        };
        // Force stdin to UTF-8 (no BOM) — Windows defaults to OEM code page which
        // garbles non-ASCII characters and can confuse Codex CLI's decode_prompt_bytes.
        psi.Environment["DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION"] = "1";

        // Guard: if prompt is null/empty, Codex CLI will exit(1) with "No prompt provided via stdin."
        if (string.IsNullOrWhiteSpace(effectivePrompt))
        {
            _lastCallEvents.Add(new CodexEvent("error", "Empty prompt — skipping Codex call"));
            System.Diagnostics.Debug.WriteLine("[CodexCli] Empty prompt, aborting call");
            return null;
        }

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return null;

            // Override stdin encoding to UTF-8 without BOM
            process.StandardInput.AutoFlush = false;
            var stdinStream = process.StandardInput.BaseStream;

            // Start reading stderr concurrently to avoid deadlock —
            // if Codex writes to stderr while we're blocked reading stdout,
            // the stderr pipe buffer can fill up and deadlock the process.
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            // Pipe the prompt through stdin as raw UTF-8 bytes (no BOM),
            // then close so Codex knows input is complete.
            var promptBytes = new UTF8Encoding(false).GetBytes(effectivePrompt);
            await stdinStream.WriteAsync(promptBytes, ct);
            await stdinStream.FlushAsync(ct);
            process.StandardInput.Close();

            var agentMessages = new List<string>();
            string? threadId = null;
            int inputTokens = 0, outputTokens = 0;

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            while (!linkedCts.Token.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(linkedCts.Token);
                if (line == null) break; // EOF

                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("type", out var typeProp)) continue;
                    var eventType = typeProp.GetString();

                    switch (eventType)
                    {
                        case "thread.started":
                            if (root.TryGetProperty("thread_id", out var tid))
                                threadId = tid.GetString();
                            break;

                        case "item.completed":
                            if (root.TryGetProperty("item", out var item))
                                ProcessCompletedItem(item, agentMessages);
                            break;

                        case "item.started":
                            if (root.TryGetProperty("item", out var startItem))
                                ProcessStartedItem(startItem);
                            break;

                        case "turn.completed":
                            if (root.TryGetProperty("usage", out var usage))
                            {
                                if (usage.TryGetProperty("input_tokens", out var inp))
                                    inputTokens = inp.GetInt32();
                                if (usage.TryGetProperty("output_tokens", out var outp))
                                    outputTokens = outp.GetInt32();
                            }
                            _lastCallEvents.Add(new CodexEvent("turn.completed",
                                $"Tokens: {inputTokens} in / {outputTokens} out"));
                            break;

                        case "turn.failed":
                        {
                            var errMsg = "Turn failed";
                            if (root.TryGetProperty("error", out var err)
                                && err.TryGetProperty("message", out var m))
                                errMsg = m.GetString() ?? errMsg;
                            _lastCallEvents.Add(new CodexEvent("error", errMsg));
                            System.Diagnostics.Debug.WriteLine($"[CodexCli] Turn failed: {errMsg}");
                            break;
                        }

                        case "error":
                        {
                            var errMsg = "Unknown error";
                            if (root.TryGetProperty("message", out var m))
                                errMsg = m.GetString() ?? errMsg;
                            _lastCallEvents.Add(new CodexEvent("error", errMsg));
                            System.Diagnostics.Debug.WriteLine($"[CodexCli] Error: {errMsg}");
                            break;
                        }
                    }
                }
                catch (JsonException)
                {
                    // Non-JSON line — debug/progress output, ignore
                }
            }

            await process.WaitForExitAsync(ct);

            if (agentMessages.Count > 0)
            {
                // Drain stderr even on success (don't block — already read concurrently)
                var stderrSuccess = await stderrTask;
                if (!string.IsNullOrEmpty(stderrSuccess))
                    System.Diagnostics.Debug.WriteLine($"[CodexCli] stderr (success path): {stderrSuccess}");
                return string.Join("\n\n", agentMessages);
            }

            // Check stderr for error info if no messages were captured — capture ALL lines
            var stderr = await stderrTask;
            if (!string.IsNullOrEmpty(stderr))
            {
                System.Diagnostics.Debug.WriteLine($"[CodexCli] stderr: {stderr}");
                // Filter out informational "Reading prompt from stdin..." and capture real errors
                var stderrLines = stderr.Trim().Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrEmpty(l))
                    .ToList();
                var errorLines = stderrLines
                    .Where(l => !l.StartsWith("Reading prompt from stdin"))
                    .ToList();
                var errorDetail = errorLines.Count > 0
                    ? string.Join(" | ", errorLines.Take(3))
                    : string.Join(" | ", stderrLines.Take(3));
                _lastCallEvents.Add(new CodexEvent("error", $"stderr: {errorDetail}"));
            }

            // If no output at all, record the failure with exit code
            if (_lastCallEvents.Count == 0 || _lastCallEvents.All(e => e.Type.StartsWith("error") || e.Type.Contains("start")))
            {
                var exitCode = process.HasExited ? process.ExitCode : -1;
                _lastCallEvents.Add(new CodexEvent("error", $"Codex returned no response (exit code: {exitCode})"));
            }

            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CodexCli] Error: {ex.Message}");
            _lastCallEvents.Add(new CodexEvent("error", $"Exception: {ex.Message}"));
            return null;
        }
    }

    /// <summary>Process a completed item from the JSONL stream.</summary>
    private void ProcessCompletedItem(JsonElement item, List<string> agentMessages)
    {
        if (!item.TryGetProperty("type", out var typeProp)) return;
        var itemType = typeProp.GetString();

        switch (itemType)
        {
            case "agent_message":
                if (item.TryGetProperty("text", out var text))
                {
                    var msg = text.GetString();
                    if (!string.IsNullOrWhiteSpace(msg))
                        agentMessages.Add(msg);
                }
                _lastCallEvents.Add(new CodexEvent("agent_message", "Response received"));
                break;

            case "web_search":
            {
                var query = "";
                if (item.TryGetProperty("query", out var q))
                    query = q.GetString() ?? "";
                // Also try to get action.query for the actual search query
                if (item.TryGetProperty("action", out var action)
                    && action.TryGetProperty("query", out var aq))
                    query = aq.GetString() ?? query;
                _lastCallEvents.Add(new CodexEvent("web_search", $"Search: {query}"));
                break;
            }

            case "command_execution":
            {
                var cmd = "";
                if (item.TryGetProperty("command", out var c))
                    cmd = c.GetString() ?? "";
                var exitCode = -1;
                if (item.TryGetProperty("exit_code", out var ec))
                    exitCode = ec.GetInt32();
                _lastCallEvents.Add(new CodexEvent("command_execution",
                    $"Command: {cmd} (exit: {exitCode})"));
                break;
            }

            case "reasoning":
                if (item.TryGetProperty("text", out var reason))
                    _lastCallEvents.Add(new CodexEvent("reasoning", reason.GetString() ?? ""));
                break;

            case "file_change":
                _lastCallEvents.Add(new CodexEvent("file_change", "File modified"));
                break;
        }
    }

    /// <summary>Process a started item (tracks in-progress operations).</summary>
    private void ProcessStartedItem(JsonElement item)
    {
        if (!item.TryGetProperty("type", out var typeProp)) return;
        var itemType = typeProp.GetString();

        switch (itemType)
        {
            case "web_search":
                _lastCallEvents.Add(new CodexEvent("web_search_start", "Searching the web..."));
                break;
            case "command_execution":
                var cmd = "";
                if (item.TryGetProperty("command", out var c))
                    cmd = c.GetString() ?? "";
                _lastCallEvents.Add(new CodexEvent("command_start", $"Running: {cmd}"));
                break;
        }
    }

    /// <summary>
    /// Validate that the Codex CLI is installed, authenticated, and can reach the model.
    /// Returns (success, message) for display in the settings UI.
    /// </summary>
    public async Task<(bool valid, string message)> ValidateAsync(CancellationToken ct = default)
    {
        if (!IsAvailable)
            return (false, "Codex CLI not found. Install with: npm i -g @openai/codex");

        // Reset cached auth check
        _isAuthenticated = null;
        if (!IsAuthenticated)
            return (false, "Not authenticated. Run 'codex login' in a terminal to sign in with your ChatGPT Plus account.");

        // Quick ping — ask for a one-word response
        try
        {
            var result = await GenerateAsync("Reply with exactly one word: hello", maxTokens: 10, ct: ct);
            if (!string.IsNullOrWhiteSpace(result))
                return (true, $"Connected via Codex CLI! Model responded: \"{result.Trim().Split('\n')[0]}\"");
            return (false, "Model returned empty response. Check your ChatGPT Plus subscription status.");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>Reset cached availability/auth checks (e.g. after re-authenticating).</summary>
    public void ResetCache()
    {
        _isAvailable = null;
        _isAuthenticated = null;
        _codexBinaryPath = null;
    }
}

/// <summary>Represents an event captured from the Codex CLI JSONL stream.</summary>
public record CodexEvent(string Type, string Detail)
{
    public DateTime Timestamp { get; } = DateTime.UtcNow;
}
