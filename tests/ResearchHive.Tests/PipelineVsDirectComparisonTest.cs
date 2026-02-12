using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Services;

namespace ResearchHive.Tests;

/// <summary>
/// A/B comparison test: ResearchHive pipeline vs. a single direct Codex CLI call.
/// 
/// PURPOSE: Determine whether our multi-step pipeline (10 Codex calls, web scraping, 
/// indexing, dedup, grounding) produces measurably better research than just asking 
/// Codex the same question in a single call with web search enabled.
/// 
/// HOW TO RUN:
///   dotnet test tests/ResearchHive.Tests --filter "PipelineVsDirectComparison" --no-build -v n
///   
/// REQUIRES: Codex CLI authenticated (codex auth) and available on PATH or npm global.
/// This test makes REAL API calls — it is [Explicit] and skipped in CI.
/// </summary>
public class PipelineVsDirectComparisonTest
{
    private const string TestQuestion = 
        "What are the most effective green synthesis methods for producing silver nanoparticles? " +
        "Focus on plant-extract-mediated synthesis, comparing different plant sources in terms of " +
        "reduction efficiency, particle size distribution, and stability. What are the safety " +
        "considerations for handling silver nanoparticles at bench scale? Include information on " +
        "required PPE, disposal protocols, and any known health hazards. How do green-synthesized " +
        "nanoparticles compare to chemically reduced ones in antibacterial efficacy?";

    /// <summary>
    /// APPROACH B: Single Codex CLI call with web search enabled.
    /// This is the "just ask Codex directly" baseline.
    /// </summary>
    [Fact(Skip = "Manual comparison test — makes real API calls")]
    public async Task DirectCodexCall_SingleShot()
    {
        var codexPath = ResolveBinaryPath();
        Assert.NotNull(codexPath);

        var systemPrompt = 
            "You are a thorough research assistant. Produce a comprehensive, well-structured research report. " +
            "Use web search to find authoritative sources. Include specific data points, cite your sources " +
            "with URLs, and organize the report with clear sections: Overview, Key Findings (with subsections), " +
            "Safety Considerations, Comparison Analysis, and Conclusion. " +
            "Target 1500-2500 words. Every factual claim must reference a source.";

        var fullPrompt = $"{systemPrompt}\n\nResearch Question:\n{TestQuestion}";

        var sw = Stopwatch.StartNew();
        var (output, events, exitCode) = await RunCodexDirect(codexPath, fullPrompt, enableWebSearch: true);
        sw.Stop();

        // Write results
        var resultsPath = Path.Combine(Path.GetTempPath(), "codex_direct_result.md");
        var metricsPath = Path.Combine(Path.GetTempPath(), "codex_direct_metrics.txt");

        await File.WriteAllTextAsync(resultsPath, output ?? "[NO OUTPUT]");

        var metrics = new StringBuilder();
        metrics.AppendLine("=== APPROACH B: Direct Codex CLI (Single Call) ===");
        metrics.AppendLine($"Question: {TestQuestion[..80]}...");
        metrics.AppendLine($"Exit Code: {exitCode}");
        metrics.AppendLine($"Wall Clock Time: {sw.Elapsed.TotalSeconds:F1}s");
        metrics.AppendLine($"Output Length: {output?.Length ?? 0} chars");
        metrics.AppendLine($"Output Words: {output?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0}");
        metrics.AppendLine($"Codex Process Spawns: 1");
        metrics.AppendLine();
        metrics.AppendLine("--- Events ---");
        
        int webSearches = 0, reasoningEvents = 0, responses = 0;
        int totalInputTokens = 0, totalOutputTokens = 0;
        foreach (var evt in events)
        {
            metrics.AppendLine($"  [{evt.Type}] {evt.Detail}");
            if (evt.Type == "web_search") webSearches++;
            if (evt.Type == "reasoning") reasoningEvents++;
            if (evt.Type == "agent_message") responses++;
            if (evt.Type == "turn.completed" && evt.Detail.Contains("Tokens:"))
            {
                // Parse "Tokens: 1234 in / 5678 out"
                var parts = evt.Detail.Split('/');
                if (parts.Length == 2)
                {
                    var inStr = new string(parts[0].Where(char.IsDigit).ToArray());
                    var outStr = new string(parts[1].Where(char.IsDigit).ToArray());
                    if (int.TryParse(inStr, out var inp)) totalInputTokens += inp;
                    if (int.TryParse(outStr, out var outp)) totalOutputTokens += outp;
                }
            }
        }

        metrics.AppendLine();
        metrics.AppendLine("--- Summary ---");
        metrics.AppendLine($"Web Searches Performed: {webSearches}");
        metrics.AppendLine($"Reasoning Steps: {reasoningEvents}");
        metrics.AppendLine($"Response Messages: {responses}");
        metrics.AppendLine($"Total Input Tokens: {totalInputTokens}");
        metrics.AppendLine($"Total Output Tokens: {totalOutputTokens}");
        metrics.AppendLine($"Total Tokens: {totalInputTokens + totalOutputTokens}");
        metrics.AppendLine();

        // Quality metrics
        if (output != null)
        {
            var citationCount = CountCitations(output);
            var urlCount = CountUrls(output);
            var sectionCount = CountSections(output);
            var hasConclusion = output.Contains("Conclusion", StringComparison.OrdinalIgnoreCase) ||
                                output.Contains("Summary", StringComparison.OrdinalIgnoreCase);
            var hasSafety = output.Contains("safety", StringComparison.OrdinalIgnoreCase) ||
                            output.Contains("PPE", StringComparison.OrdinalIgnoreCase);

            metrics.AppendLine("--- Quality Metrics ---");
            metrics.AppendLine($"Citation References: {citationCount}");
            metrics.AppendLine($"Unique URLs: {urlCount}");
            metrics.AppendLine($"Sections (## headers): {sectionCount}");
            metrics.AppendLine($"Has Conclusion: {hasConclusion}");
            metrics.AppendLine($"Covers Safety: {hasSafety}");
            metrics.AppendLine($"Unique Domains Cited: {CountUniqueDomains(output)}");
        }

        metrics.AppendLine();
        metrics.AppendLine($"Full output: {resultsPath}");
        metrics.AppendLine($"Metrics: {metricsPath}");

        await File.WriteAllTextAsync(metricsPath, metrics.ToString());

        // Print to test output
        Console.WriteLine(metrics.ToString());
        Console.WriteLine("\n--- BEGIN DIRECT OUTPUT ---");
        Console.WriteLine(output?[..Math.Min(output.Length, 3000)] ?? "[EMPTY]");
        if ((output?.Length ?? 0) > 3000)
            Console.WriteLine($"\n... [{output!.Length - 3000} more chars] ...");
        Console.WriteLine("--- END DIRECT OUTPUT ---");

        Assert.NotNull(output);
        Assert.True(output.Length > 100, "Output should be substantial");
    }

    /// <summary>
    /// APPROACH A: Our full pipeline. Run this by actually running a research session
    /// through the app, then inspect the report. This test just documents what it does.
    /// 
    /// For a fair comparison, you can also run this programmatically — but it requires
    /// Chrome, SQLite, and the full service stack. Easier to run via the WPF UI and
    /// compare the exported report.
    /// 
    /// This test instead prints the EXPECTED pipeline behavior for reference.
    /// </summary>
    [Fact(Skip = "Manual comparison test — run explicitly with --filter")]
    public void PipelineApproach_DocumentedBehavior()
    {
        var doc = new StringBuilder();
        doc.AppendLine("=== APPROACH A: ResearchHive Pipeline ===");
        doc.AppendLine();
        doc.AppendLine("CODEX CLI CALLS (in order):");
        doc.AppendLine("  1. GenerateAsync — Generate 5 search queries (~200 tokens, retry: yes)");
        doc.AppendLine("  2. GenerateAsync — Decompose into 3-4 sub-questions (~200 tokens, retry: yes)");
        doc.AppendLine("     [calls 1-2 run in parallel on cloud]");
        doc.AppendLine("  3. GenerateAsync — Coverage eval iteration 1 (~2000 tokens, retry: yes)");
        doc.AppendLine("  4. GenerateAsync — Deep search query generation (~2000 tokens, retry: yes)");
        doc.AppendLine("  5. GenerateAsync — Coverage eval iteration 2 (~2500 tokens, retry: yes)");
        doc.AppendLine("  6. GenerateAsync — Coverage eval iteration 3 (~2500 tokens, retry: yes)");
        doc.AppendLine("  7. GenerateAsync — SYNTHESIS: full report from evidence (~6000 tokens, retry: yes)");
        doc.AppendLine("  8. GenerateAsync — Sufficiency check (~3500 tokens, retry: yes, SKIPPED if grounding >= 60%)");
        doc.AppendLine("  9. GenerateAsync — Remediation re-draft (~6000 tokens, retry: yes, SKIPPED if sufficient)");
        doc.AppendLine(" 10. GenerateAsync — Executive summary (~4500 tokens, retry: yes)");
        doc.AppendLine();
        doc.AppendLine("ADDITIONAL INFRASTRUCTURE:");
        doc.AppendLine("  - Google Chrome (headless-ish) search via UndetectedChromeDriver");
        doc.AppendLine("  - 4 additional search engines (Bing, DuckDuckGo, SearXNG, Brave)");
        doc.AppendLine("  - HTTP page fetching + HTML cleaning for each URL");
        doc.AppendLine("  - Text chunking + embedding generation (local, free)");
        doc.AppendLine("  - SQLite FTS5 indexing for BM25 keyword search");
        doc.AppendLine("  - Cosine similarity semantic search on embeddings");
        doc.AppendLine("  - Reciprocal Rank Fusion (RRF) to merge search lanes");
        doc.AppendLine("  - Domain-based evidence deduplication (max 3 chunks/domain)");
        doc.AppendLine("  - Grounding score computation (% of claims with [N] citations)");
        doc.AppendLine("  - Full citation chain with source URLs and excerpts");
        doc.AppendLine();
        doc.AppendLine("TOTAL CODEX PROCESS SPAWNS:");
        doc.AppendLine("  Best case (all succeed):  8-10");
        doc.AppendLine("  Typical (some retries):   12-14");
        doc.AppendLine("  Worst case (all retry):   20-24");
        doc.AppendLine();
        doc.AppendLine("TOTAL INPUT TOKENS (approximate):");
        doc.AppendLine("  Best case (skip sufficiency + remediation): ~15,000");
        doc.AppendLine("  Full path (all steps):                      ~29,000");
        doc.AppendLine();
        doc.AppendLine("WALL CLOCK TIME: 3-10 minutes (dominated by Chrome + page fetching)");
        doc.AppendLine();
        doc.AppendLine("KEY LIMITATION: Codex synthesis call has web search DISABLED");
        doc.AppendLine("  (we pass enableSearch:false to force use of OUR evidence via [N] citations)");
        doc.AppendLine("  BUT Codex o4-mini still performs web searches via built-in capability");
        doc.AppendLine("  (observed: 30+ searches in user's log during synthesis).");
        doc.AppendLine("  This means the model IGNORES our evidence context and researches independently!");

        Console.WriteLine(doc.ToString());
    }

    // ---- Helpers ----

    private static async Task<(string? output, List<CodexEvt> events, int exitCode)> RunCodexDirect(
        string binaryPath, string prompt, bool enableWebSearch)
    {
        var args = new StringBuilder("exec --json --sandbox read-only --skip-git-repo-check");
        if (enableWebSearch)
            args.Append(" -c web_search=\"live\"");

        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = args.ToString(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath()
        };

        using var process = Process.Start(psi);
        if (process == null) return (null, new(), -1);

        // Start stderr reader concurrently
        var stderrTask = process.StandardError.ReadToEndAsync();

        // Write prompt as UTF-8
        var promptBytes = new UTF8Encoding(false).GetBytes(prompt);
        await process.StandardInput.BaseStream.WriteAsync(promptBytes);
        await process.StandardInput.BaseStream.FlushAsync();
        process.StandardInput.Close();

        var agentMessages = new List<string>();
        var events = new List<CodexEvt>();

        using var linkedCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        while (!linkedCts.Token.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(linkedCts.Token);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) continue;
                var eventType = typeProp.GetString();

                switch (eventType)
                {
                    case "item.completed":
                        if (root.TryGetProperty("item", out var item))
                        {
                            if (item.TryGetProperty("type", out var itemType))
                            {
                                var t = itemType.GetString();
                                switch (t)
                                {
                                    case "agent_message":
                                        if (item.TryGetProperty("text", out var text))
                                        {
                                            var msg = text.GetString();
                                            if (!string.IsNullOrWhiteSpace(msg))
                                                agentMessages.Add(msg);
                                        }
                                        events.Add(new("agent_message", "Response received"));
                                        break;
                                    case "web_search":
                                        var query = "";
                                        if (item.TryGetProperty("query", out var q))
                                            query = q.GetString() ?? "";
                                        events.Add(new("web_search", $"Search: {query}"));
                                        break;
                                    case "reasoning":
                                        var reason = "";
                                        if (item.TryGetProperty("text", out var r))
                                            reason = r.GetString() ?? "";
                                        events.Add(new("reasoning", reason.Length > 80 ? reason[..80] : reason));
                                        break;
                                }
                            }
                        }
                        break;

                    case "item.started":
                        if (root.TryGetProperty("item", out var startItem)
                            && startItem.TryGetProperty("type", out var sit))
                        {
                            if (sit.GetString() == "web_search")
                                events.Add(new("web_search_start", "Searching..."));
                        }
                        break;

                    case "turn.completed":
                        var detail = "Turn done";
                        if (root.TryGetProperty("usage", out var usage))
                        {
                            var inp = usage.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0;
                            var outp = usage.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0;
                            detail = $"Tokens: {inp} in / {outp} out";
                        }
                        events.Add(new("turn.completed", detail));
                        break;

                    case "turn.failed":
                        var errMsg = "Turn failed";
                        if (root.TryGetProperty("error", out var err)
                            && err.TryGetProperty("message", out var m))
                            errMsg = m.GetString() ?? errMsg;
                        events.Add(new("error", errMsg));
                        break;
                }
            }
            catch (JsonException) { }
        }

        await process.WaitForExitAsync();
        var stderr = await stderrTask;
        if (!string.IsNullOrEmpty(stderr))
            events.Add(new("stderr", stderr.Trim()));

        var output = agentMessages.Count > 0 ? string.Join("\n\n", agentMessages) : null;
        return (output, events, process.HasExited ? process.ExitCode : -1);
    }

    private static string? ResolveBinaryPath()
    {
        var npmRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm", "node_modules", "@openai", "codex", "vendor",
            "x86_64-pc-windows-msvc", "codex", "codex.exe");
        if (File.Exists(npmRoot)) return npmRoot;

        var npmArm = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm", "node_modules", "@openai", "codex", "vendor",
            "aarch64-pc-windows-msvc", "codex", "codex.exe");
        if (File.Exists(npmArm)) return npmArm;

        return null;
    }

    private static int CountCitations(string text)
    {
        int count = 0;
        for (int i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == '[' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                int j = i + 1;
                while (j < text.Length && char.IsDigit(text[j])) j++;
                if (j < text.Length && text[j] == ']') count++;
            }
        }
        return count;
    }

    private static int CountUrls(string text)
    {
        var urls = new HashSet<string>();
        var idx = 0;
        while (idx < text.Length)
        {
            var httpIdx = text.IndexOf("http", idx, StringComparison.OrdinalIgnoreCase);
            if (httpIdx < 0) break;
            var end = httpIdx;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != ')' && text[end] != ']' && text[end] != '>') end++;
            var url = text[httpIdx..end].TrimEnd('.', ',', ';');
            if (url.Length > 10) urls.Add(url);
            idx = end;
        }
        return urls.Count;
    }

    private static int CountSections(string text)
    {
        return text.Split('\n').Count(l => l.TrimStart().StartsWith("## "));
    }

    private static int CountUniqueDomains(string text)
    {
        var domains = new HashSet<string>();
        var idx = 0;
        while (idx < text.Length)
        {
            var httpIdx = text.IndexOf("http", idx, StringComparison.OrdinalIgnoreCase);
            if (httpIdx < 0) break;
            var end = httpIdx;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != ')' && text[end] != ']' && text[end] != '>') end++;
            var url = text[httpIdx..end];
            try
            {
                var uri = new Uri(url);
                var host = uri.Host.Replace("www.", "");
                domains.Add(host);
            }
            catch { }
            idx = end;
        }
        return domains.Count;
    }

    record CodexEvt(string Type, string Detail);
}
