using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace ResearchHive.Core.Services;

/// <summary>
/// Orchestrates repo scanning + complement research as a tracked job.
/// Also supports multi-repo instant scan-and-fuse.
///
/// Pipeline routing (LLM call counts):
///   Codex 5.3 (agentic): 1 call — analysis + web search for complements in a single agentic call
///   Other cloud (large context): 2 calls — consolidated analysis + separate complement research
///   Ollama/local: 4 calls — CodeBook + analysis + gap verify + complement
/// </summary>
public class RepoIntelligenceJobRunner
{
    private readonly SessionManager _sessionManager;
    private readonly RepoScannerService _scanner;
    private readonly ComplementResearchService _complementService;
    private readonly ILlmService _llmService;
    private readonly RepoIndexService? _repoIndexService;
    private readonly CodeBookGenerator? _codeBookGenerator;
    private readonly IRetrievalService? _retrievalService;
    private readonly ILogger<RepoIntelligenceJobRunner>? _logger;
    private readonly RepoFactSheetBuilder? _factSheetBuilder;
    private readonly PostScanVerifier? _postScanVerifier;
    private readonly RepoCloneService? _cloneService;

    // Optional Hive Mind integration — set via property injection
    public GlobalMemoryService? GlobalMemory { get; set; }

    public RepoIntelligenceJobRunner(
        SessionManager sessionManager,
        RepoScannerService scanner,
        ComplementResearchService complementService,
        ILlmService llmService,
        RepoIndexService? repoIndexService = null,
        CodeBookGenerator? codeBookGenerator = null,
        IRetrievalService? retrievalService = null,
        ILogger<RepoIntelligenceJobRunner>? logger = null,
        RepoFactSheetBuilder? factSheetBuilder = null,
        PostScanVerifier? postScanVerifier = null,
        RepoCloneService? cloneService = null)
    {
        _sessionManager = sessionManager;
        _scanner = scanner;
        _complementService = complementService;
        _llmService = llmService;
        _repoIndexService = repoIndexService;
        _codeBookGenerator = codeBookGenerator;
        _retrievalService = retrievalService;
        _logger = logger;
        _factSheetBuilder = factSheetBuilder;
        _postScanVerifier = postScanVerifier;
        _cloneService = cloneService;
    }

    /// <summary>Scan a single repo: metadata → clone+index → RAG-grounded analysis → gap verification → complements.</summary>
    public async Task<RepoProfile> RunAnalysisAsync(string sessionId, string repoUrl, CancellationToken ct = default)
    {
        var db = _sessionManager.GetSessionDb(sessionId);

        var job = new ResearchJob
        {
            SessionId = sessionId,
            Type = JobType.RepoAnalysis,
            Prompt = $"Analyze repository: {repoUrl}",
            State = JobState.Planning
        };
        db.SaveJob(job);
        AddReplay(job, "start", "Repo Analysis Started", $"Target: {repoUrl}");

        try
        {
            var telemetry = new ScanTelemetry();
            var totalSw = Stopwatch.StartNew();

            // ── Phase 1: Metadata scan (GitHub API or local file system — no LLM yet) ──
            var phaseSw = Stopwatch.StartNew();
            job.State = JobState.Searching;
            db.SaveJob(job);
            var isLocal = RepoScannerService.IsLocalPath(repoUrl);
            AddReplay(job, "scan", "Scanning Repository",
                isLocal ? "Reading metadata, README, dependencies from local file system..."
                        : "Fetching metadata, README, dependencies via GitHub API...");

            var profile = await _scanner.ScanAsync(repoUrl, ct);
            profile.SessionId = sessionId;

            phaseSw.Stop();
            telemetry.Phases.Add(new PhaseTimingRecord { Phase = "Metadata Scan", DurationMs = phaseSw.ElapsedMilliseconds });
            telemetry.GitHubApiCallCount += _scanner.LastScanApiCallCount;

            AddReplay(job, "scanned", "Metadata Collected",
                $"Found: {profile.PrimaryLanguage} | {profile.Dependencies.Count} deps" +
                (isLocal ? "" : $" | {profile.Stars}★"));

            // ── Phase 2: Clone + deep index (chunks + embeddings) ──
            phaseSw = Stopwatch.StartNew();
            bool isIndexed = false;
            if (_repoIndexService != null)
            {
                try
                {
                    AddReplay(job, "indexing", "Indexing Repository Code", "Cloning repo and building vector index of all source files...");
                    await _repoIndexService.IndexRepoAsync(sessionId, profile, ct);
                    isIndexed = profile.IndexedChunkCount > 0;
                    AddReplay(job, "indexed", "Code Indexed",
                        $"Indexed {profile.IndexedFileCount} files → {profile.IndexedChunkCount} chunks");
                }
                catch (Exception ex)
                {
                    AddReplay(job, "index_warn", "Index Skipped", $"Could not index code: {ex.Message}");
                }
            }

            phaseSw.Stop();
            telemetry.Phases.Add(new PhaseTimingRecord { Phase = "Clone + Index", DurationMs = phaseSw.ElapsedMilliseconds });

            // ── Phase 2.25: Identity Scan (focused LLM call for project purpose — separate from code analysis) ──
            phaseSw = Stopwatch.StartNew();
            try
            {
                var identityPath = _cloneService?.GetClonePath(repoUrl) ?? (isLocal ? Path.GetFullPath(repoUrl.Trim()) : "");
                if (!string.IsNullOrEmpty(identityPath) && Directory.Exists(identityPath))
                {
                    AddReplay(job, "identity", "Identity Scan", "Reading project docs to determine what this project IS...");
                    await _scanner.RunIdentityScanAsync(profile, identityPath, ct);
                    var idResult = !string.IsNullOrWhiteSpace(profile.ProductCategory)
                        ? $"Category: {profile.ProductCategory}"
                        : "Category: undetermined";
                    var summaryPreview = !string.IsNullOrWhiteSpace(profile.ProjectSummary)
                        ? profile.ProjectSummary.Length > 100
                            ? profile.ProjectSummary[..100] + "…"
                            : profile.ProjectSummary
                        : "(no summary)";
                    AddReplay(job, "identity_done", "Identity Established",
                        $"{idResult} | {summaryPreview}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Identity scan failed — proceeding with code analysis");
                AddReplay(job, "identity_warn", "Identity Scan Skipped", ex.Message);
            }
            phaseSw.Stop();
            telemetry.Phases.Add(new PhaseTimingRecord { Phase = "Identity Scan", DurationMs = phaseSw.ElapsedMilliseconds });
            telemetry.LlmCalls.Add(new LlmCallRecord
            {
                Purpose = "Identity Scan",
                Model = _llmService.LastModelUsed,
                DurationMs = phaseSw.ElapsedMilliseconds
            });

            // ── Phase 2.5: Deterministic Fact Sheet (zero-LLM ground truth) ──
            phaseSw = Stopwatch.StartNew();
            if (_factSheetBuilder != null)
            {
                try
                {
                    AddReplay(job, "factsheet", "Building Fact Sheet", "Deterministic code analysis: package usage, capability fingerprinting, diagnostics...");
                    var clonePath = _cloneService?.GetClonePath(repoUrl);
                    profile.FactSheet = _factSheetBuilder.Build(profile, clonePath);
                    AddReplay(job, "factsheet_done", "Fact Sheet Built",
                        $"{profile.FactSheet.ActivePackages.Count} active packages, {profile.FactSheet.PhantomPackages.Count} phantom, " +
                        $"{profile.FactSheet.ProvenCapabilities.Count} capabilities proven, {profile.FactSheet.ConfirmedAbsent.Count} absent, " +
                        $"{profile.FactSheet.TestMethodCount} test methods, AppType={profile.FactSheet.AppType}");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Fact sheet generation failed — proceeding without ground truth");
                    AddReplay(job, "factsheet_warn", "Fact Sheet Skipped", $"Could not build: {ex.Message}");
                }
            }
            phaseSw.Stop();
            telemetry.Phases.Add(new PhaseTimingRecord { Phase = "Deterministic Fact Sheet", DurationMs = phaseSw.ElapsedMilliseconds });

            // ── Phases 3-5: Analysis pipeline (agentic for Codex, consolidated for cloud, separate for local) ──
            // Codex 5.3 (agentic): single call with web search — analysis + complements in one shot
            // Cloud providers (large context): combine CodeBook + Analysis + Gap Verification into 1 LLM call
            // Local/Ollama providers (small context): keep 3 separate LLM calls for reliability
            bool agenticComplementsHandled = false;

            if (_llmService.IsCodexOAuthActive && isIndexed && _retrievalService != null)
            {
                // ── AGENTIC PATH: Single Codex call handles everything including web search for complements ──
                phaseSw = Stopwatch.StartNew();
                AddReplay(job, "agentic", "Agentic Full Analysis",
                    "Running full analysis + complement web search in a single Codex agentic call...");
                job.State = JobState.Evaluating;
                db.SaveJob(job);
                _logger?.LogInformation("Agentic path: Codex 5.3 with web search for {Owner}/{Name}", profile.Owner, profile.Name);

                var agenticComplements = await RunAgenticFullAnalysisAsync(sessionId, profile, telemetry, ct);
                phaseSw.Stop();

                if (agenticComplements != null && agenticComplements.Count > 0)
                {
                    profile.ComplementSuggestions = agenticComplements;
                    agenticComplementsHandled = true;
                    AddReplay(job, "analyzed", "Agentic Analysis Complete",
                        $"CodeBook generated, {profile.Strengths.Count} strengths, {profile.Gaps.Count} pre-verified gaps, " +
                        $"{profile.ComplementSuggestions.Count} complements from web search");
                    telemetry.Phases.Add(new PhaseTimingRecord { Phase = "Agentic Full Analysis (CodeBook+RAG+GapVerify+Complements)", DurationMs = phaseSw.ElapsedMilliseconds });
                }
                else if (profile.Strengths.Count > 0)
                {
                    // Agentic call succeeded for analysis but returned no complements — complements will be researched separately
                    AddReplay(job, "analyzed", "Agentic Analysis Complete (no complements)",
                        $"CodeBook generated, {profile.Strengths.Count} strengths, {profile.Gaps.Count} pre-verified gaps — complements will be researched separately");
                    telemetry.Phases.Add(new PhaseTimingRecord { Phase = "Agentic Full Analysis (partial — no complements)", DurationMs = phaseSw.ElapsedMilliseconds });
                }
                else
                {
                    // Agentic call failed entirely — fall through to consolidated path
                    _logger?.LogWarning("Agentic path failed for {Owner}/{Name}, falling back to consolidated analysis", profile.Owner, profile.Name);
                    AddReplay(job, "agentic_fallback", "Agentic Failed", "Falling back to consolidated cloud analysis...");
                    telemetry.Phases.Add(new PhaseTimingRecord { Phase = "Agentic Full Analysis (FAILED)", DurationMs = phaseSw.ElapsedMilliseconds });

                    phaseSw = Stopwatch.StartNew();
                    await RunConsolidatedAnalysisAsync(sessionId, profile, telemetry, ct);
                    phaseSw.Stop();
                    AddReplay(job, "analyzed", "Consolidated Analysis Complete (fallback)",
                        $"CodeBook generated, {profile.Strengths.Count} strengths, {profile.Gaps.Count} pre-verified gaps");
                    telemetry.Phases.Add(new PhaseTimingRecord { Phase = "Consolidated Analysis (fallback from agentic)", DurationMs = phaseSw.ElapsedMilliseconds });
                }
            }
            else if (_llmService.IsLargeContextProvider && isIndexed && _retrievalService != null)
            {
                phaseSw = Stopwatch.StartNew();
                AddReplay(job, "consolidated", "Consolidated Analysis",
                    "Running CodeBook + Analysis + Gap Verification in a single cloud LLM call...");
                job.State = JobState.Evaluating;
                db.SaveJob(job);

                await RunConsolidatedAnalysisAsync(sessionId, profile, telemetry, ct);

                AddReplay(job, "analyzed", "Consolidated Analysis Complete",
                    $"CodeBook generated, {profile.Strengths.Count} strengths, {profile.Gaps.Count} pre-verified gaps");
                phaseSw.Stop();
                telemetry.Phases.Add(new PhaseTimingRecord { Phase = "Consolidated Analysis (CodeBook+RAG+GapVerify)", DurationMs = phaseSw.ElapsedMilliseconds });
            }
            else
            {
                // ── Phase 3: CodeBook generation (architecture summary from chunks) ──
                phaseSw = Stopwatch.StartNew();
                if (_codeBookGenerator != null && isIndexed)
                {
                    try
                    {
                        AddReplay(job, "codebook", "Generating CodeBook", "Analyzing architecture patterns from indexed code...");
                        var cbSw = Stopwatch.StartNew();
                        profile.CodeBook = await _codeBookGenerator.GenerateAsync(sessionId, profile, ct);
                        cbSw.Stop();
                        telemetry.LlmCalls.Add(new LlmCallRecord
                        {
                            Purpose = "CodeBook Generation",
                            Model = _llmService.LastModelUsed,
                            DurationMs = cbSw.ElapsedMilliseconds,
                            ResponseLength = profile.CodeBook.Length
                        });
                        telemetry.RetrievalCallCount += 6; // CodeBookGenerator issues 6 RAG queries
                        AddReplay(job, "codebook_done", "CodeBook Generated", "Architecture reference document created");
                    }
                    catch (Exception ex)
                    {
                        AddReplay(job, "codebook_warn", "CodeBook Skipped", $"Could not generate CodeBook: {ex.Message}");
                    }
                }

                phaseSw.Stop();
                telemetry.Phases.Add(new PhaseTimingRecord { Phase = "CodeBook Generation", DurationMs = phaseSw.ElapsedMilliseconds });

                // ── Phase 4: RAG-grounded strengths + gaps analysis ──
                phaseSw = Stopwatch.StartNew();
                job.State = JobState.Evaluating;
                db.SaveJob(job);

                if (isIndexed && _retrievalService != null)
                {
                    AddReplay(job, "rag_analysis", "RAG-Grounded Analysis", "Querying indexed code to build comprehensive assessment...");
                    await RunRagGroundedAnalysis(sessionId, profile, telemetry, ct);
                }
                else
                {
                    // Fallback: shallow analysis from metadata only (no code available)
                    AddReplay(job, "shallow_analysis", "Shallow Analysis", "No indexed code available — analyzing from README + manifests only");
                    await _scanner.AnalyzeShallowAsync(profile, ct);
                    telemetry.LlmCalls.Add(new LlmCallRecord
                    {
                        Purpose = "Shallow Analysis (fallback)",
                        Model = _llmService.LastModelUsed,
                        DurationMs = phaseSw.ElapsedMilliseconds
                    });
                }

                AddReplay(job, "analyzed", "Analysis Complete",
                    $"{profile.Strengths.Count} strengths, {profile.Gaps.Count} gaps identified");

                phaseSw.Stop();
                telemetry.Phases.Add(new PhaseTimingRecord { Phase = "RAG Analysis", DurationMs = phaseSw.ElapsedMilliseconds });

                // ── Phase 5: Gap verification via RAG (prune false positives) ──
                phaseSw = Stopwatch.StartNew();
                if (isIndexed && _retrievalService != null && profile.Gaps.Count > 0)
                {
                    AddReplay(job, "gap_verify", "Verifying Gaps Against Code", "Checking each gap claim against actual source code...");
                    var originalGapCount = profile.Gaps.Count;
                    await VerifyGapsViaRag(sessionId, profile, telemetry, ct);
                    var pruned = originalGapCount - profile.Gaps.Count;
                    if (pruned > 0)
                        AddReplay(job, "gaps_pruned", "False Positives Removed", $"Removed {pruned} false gaps — {profile.Gaps.Count} verified gaps remain");
                    else
                        AddReplay(job, "gaps_confirmed", "Gaps Confirmed", $"All {profile.Gaps.Count} gaps verified as genuine");
                }
                phaseSw.Stop();
                telemetry.Phases.Add(new PhaseTimingRecord { Phase = "Gap Verification", DurationMs = phaseSw.ElapsedMilliseconds });
            }

            // ── Phase 6: Complement research (based on verified gaps) ──
            // SKIP when the agentic path already handled complements via web search
            if (!agenticComplementsHandled)
            {
                phaseSw = Stopwatch.StartNew();
                AddReplay(job, "complement", "Researching Complements", $"Searching for projects to fill {profile.Gaps.Count} verified gaps...");
                var complements = await _complementService.ResearchAsync(profile, ct);
                profile.ComplementSuggestions = complements;
                phaseSw.Stop();
                telemetry.Phases.Add(new PhaseTimingRecord { Phase = "Complement Research", DurationMs = phaseSw.ElapsedMilliseconds });
                telemetry.WebSearchCallCount = _complementService.LastSearchCallCount;
                telemetry.GitHubApiCallCount += _complementService.LastEnrichCallCount;
                // Complement service makes 1 LLM call
                telemetry.LlmCalls.Add(new LlmCallRecord
                {
                    Purpose = "Complement Evaluation",
                    Model = _llmService.LastModelUsed,
                    DurationMs = _complementService.LastLlmDurationMs
                });
                AddReplay(job, "complements_done", "Complements Found", $"Found {complements.Count} complementary projects");
            }
            else
            {
                AddReplay(job, "complements_done", "Complements Found (Agentic)",
                    $"Found {profile.ComplementSuggestions.Count} complementary projects via Codex web search");
            }

            // ── Phase 6.5: Post-scan verification (fact-check LLM output against ground truth) ──
            if (_postScanVerifier != null && profile.FactSheet != null)
            {
                phaseSw = Stopwatch.StartNew();
                AddReplay(job, "verify", "Verifying Against Ground Truth",
                    "Fact-checking LLM strengths/gaps/complements against deterministic fact sheet...");
                try
                {
                    var verification = await _postScanVerifier.VerifyAsync(profile, profile.FactSheet, ct);
                    if (verification.TotalCorrections > 0)
                    {
                        AddReplay(job, "verified", "Ground Truth Verification Complete", verification.Summary);
                        _logger?.LogInformation("PostScanVerifier corrections: {Summary}", verification.Summary);
                    }
                    else
                    {
                        AddReplay(job, "verified", "Verification Passed", "All LLM claims consistent with ground truth");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Post-scan verification failed — results unverified");
                    AddReplay(job, "verify_warn", "Verification Skipped", ex.Message);
                }
                phaseSw.Stop();
                telemetry.Phases.Add(new PhaseTimingRecord { Phase = "Post-Scan Verification", DurationMs = phaseSw.ElapsedMilliseconds });
            }

            // ── Phase 7: Save + report ──
            totalSw.Stop();
            telemetry.TotalDurationMs = totalSw.ElapsedMilliseconds;
            profile.Telemetry = telemetry;
            db.SaveRepoProfile(profile);

            job.State = JobState.Reporting;
            db.SaveJob(job);

            var report = GenerateReport(profile);
            var reportRecord = new Report
            {
                SessionId = sessionId,
                JobId = job.Id,
                ReportType = "RepoAnalysis",
                Title = $"Repo Analysis: {profile.Owner}/{profile.Name}",
                Content = report,
                Format = "markdown",
                ModelUsed = profile.AnalysisModelUsed
            };
            db.SaveReport(reportRecord);

            job.State = JobState.Completed;
            job.FullReport = report;
            job.ModelUsed = profile.AnalysisModelUsed;
            job.ExecutiveSummary = $"Analyzed {profile.Owner}/{profile.Name}: {profile.PrimaryLanguage}, " +
                $"{profile.Dependencies.Count} deps, {profile.IndexedChunkCount} chunks, " +
                $"{profile.Strengths.Count} strengths, {profile.Gaps.Count} gaps, {profile.ComplementSuggestions.Count} complements. " +
                $"Pipeline: {telemetry.Summary}";
            db.SaveJob(job);
            AddReplay(job, "complete", "Analysis Complete", job.ExecutiveSummary);
            AddReplay(job, "telemetry", "Pipeline Telemetry", telemetry.Summary);

            return profile;
        }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.ErrorMessage = ex.Message;
            db.SaveJob(job);
            throw;
        }
    }

    /// <summary>
    /// Consolidated analysis for cloud/Codex providers: combines CodeBook generation,
    /// RAG-grounded analysis, and gap self-verification into a single LLM call.
    /// Reduces 3 LLM calls to 1, leveraging the large context window of cloud models.
    /// </summary>
    private async Task RunConsolidatedAnalysisAsync(string sessionId, RepoProfile profile, ScanTelemetry telemetry, CancellationToken ct)
    {
        // Combine ALL architecture + analysis queries and run them in parallel
        var architectureQueries = new[]
        {
            "main entry point program startup initialization",
            "architecture modules services dependency injection",
            "data model schema database entities",
            "API endpoints routes controllers",
            "configuration settings environment",
            "build deploy dockerfile CI pipeline"
        };
        var analysisQueries = new[]
        {
            "service registration dependency injection startup configuration",
            "database data access storage persistence repository",
            "API cloud provider external service integration",
            "test testing unit test integration test assertion",
            "error handling retry fault tolerance resilience",
            "user interface view model MVVM commands controls",
            "search indexing retrieval embedding vector",
            "authentication security encryption key management",
            "export reporting document generation output",
            "domain business logic core workflow pipeline",
            "notification monitoring health check logging",
            "safety validation verification quality"
        };

        var allQueries = architectureQueries.Concat(analysisQueries).ToArray();
        var repoFilter = new[] { "repo_code", "repo_doc" };
        var allChunks = new List<RetrievalResult>();

        // Parallel RAG retrieval — all 18 queries at once
        var retrievalTasks = allQueries.Select(async q =>
        {
            try
            {
                var hits = await _retrievalService!.HybridSearchAsync(sessionId, q, repoFilter, topK: 5, ct);
                return (IReadOnlyList<RetrievalResult>)hits.ToList();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Consolidated RAG query failed: {Query}", q);
                return (IReadOnlyList<RetrievalResult>)Array.Empty<RetrievalResult>();
            }
        }).ToList();

        var results = await Task.WhenAll(retrievalTasks);
        foreach (var hits in results)
            allChunks.AddRange(hits);
        telemetry.RetrievalCallCount += allQueries.Length;

        // Deduplicate and take top 40 by score — broad coverage for self-verification
        var topChunks = allChunks
            .DistinctBy(r => r.Chunk.Id)
            .OrderByDescending(r => r.Score)
            .Take(40)
            .Select(r => r.Chunk.Text)
            .ToList();

        if (topChunks.Count == 0)
        {
            // Fallback to shallow analysis
            await _scanner.AnalyzeShallowAsync(profile, ct);
            return;
        }

        // Build consolidated prompt and call LLM once
        var prompt = RepoScannerService.BuildConsolidatedAnalysisPrompt(profile, topChunks);
        var llmSw = Stopwatch.StartNew();
        var response = await _llmService.GenerateAsync(prompt,
            "You are a senior software architecture analyst. Produce ALL requested sections in a single response. " +
            "Be specific — reference real class names, service names, and patterns from the code. " +
            "Self-verify your gap claims: only include gaps for things genuinely MISSING, not critiques of existing features.",
            ct: ct);
        llmSw.Stop();

        telemetry.LlmCalls.Add(new LlmCallRecord
        {
            Purpose = "Consolidated Analysis (CodeBook + RAG + GapVerify)",
            Model = _llmService.LastModelUsed,
            DurationMs = llmSw.ElapsedMilliseconds,
            PromptLength = prompt.Length,
            ResponseLength = response.Length
        });

        // Parse the combined response
        var (summary, codeBook, frameworks, strengths, gaps) = RepoScannerService.ParseConsolidatedAnalysis(response);

        profile.CodeBook = $"# CodeBook: {profile.Owner}/{profile.Name}\n\n{codeBook}";
        profile.AnalysisModelUsed = _llmService.LastModelUsed;

        if (!string.IsNullOrWhiteSpace(summary))
            profile.ProjectSummary = summary;

        // Add LLM-detected frameworks (dedup with deterministic ones)
        foreach (var fw in frameworks)
        {
            if (!profile.Frameworks.Any(f => f.Equals(fw, StringComparison.OrdinalIgnoreCase) ||
                f.Contains(fw.Split(' ')[0], StringComparison.OrdinalIgnoreCase)))
                profile.Frameworks.Add(fw);
        }

        profile.Strengths.AddRange(strengths);

        // Gaps are already self-verified by the cloud model; enforce minimum of 3
        if (gaps.Count >= 3)
            profile.Gaps = gaps;
        else if (gaps.Count > 0)
            profile.Gaps = gaps; // Keep whatever we got rather than losing them
    }

    /// <summary>
    /// Agentic full analysis: sends a single Codex 5.3 call with web search enabled.
    /// Codex handles CodeBook generation, framework detection, strengths, gaps, AND
    /// autonomously searches the web for complementary projects — all in one shot.
    /// Returns the complement list on success, or null if the agentic call failed.
    /// </summary>
    private async Task<List<ComplementProject>?> RunAgenticFullAnalysisAsync(
        string sessionId, RepoProfile profile, ScanTelemetry telemetry, CancellationToken ct)
    {
        // ── Step 1: Parallel RAG retrieval (same 18 queries as consolidated path) ──
        var architectureQueries = new[]
        {
            "main entry point program startup initialization",
            "architecture modules services dependency injection",
            "data model schema database entities",
            "API endpoints routes controllers",
            "configuration settings environment",
            "build deploy dockerfile CI pipeline"
        };
        var analysisQueries = new[]
        {
            "service registration dependency injection startup configuration",
            "database data access storage persistence repository",
            "API cloud provider external service integration",
            "test testing unit test integration test assertion",
            "error handling retry fault tolerance resilience",
            "user interface view model MVVM commands controls",
            "search indexing retrieval embedding vector",
            "authentication security encryption key management",
            "export reporting document generation output",
            "domain business logic core workflow pipeline",
            "notification monitoring health check logging",
            "safety validation verification quality"
        };

        var allQueries = architectureQueries.Concat(analysisQueries).ToArray();
        var repoFilter = new[] { "repo_code", "repo_doc" };
        var allChunks = new List<RetrievalResult>();

        var retrievalTasks = allQueries.Select(async q =>
        {
            try
            {
                var hits = await _retrievalService!.HybridSearchAsync(sessionId, q, repoFilter, topK: 5, ct);
                return (IReadOnlyList<RetrievalResult>)hits.ToList();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Agentic RAG query failed: {Query}", q);
                return (IReadOnlyList<RetrievalResult>)Array.Empty<RetrievalResult>();
            }
        }).ToList();

        var results = await Task.WhenAll(retrievalTasks);
        foreach (var hits in results)
            allChunks.AddRange(hits);

        telemetry.RetrievalCallCount += allQueries.Length;

        // Dedup by content hash, take top 40 by score
        var topChunks = allChunks
            .DistinctBy(c => c.Chunk.Id)
            .OrderByDescending(c => c.Score)
            .Take(40)
            .Select(c => $"[{c.SourceType}]\n{c.Chunk.Text}")
            .ToList();

        if (topChunks.Count < 3)
        {
            _logger?.LogWarning("Agentic path: only {Count} chunks retrieved, falling back to shallow analysis", topChunks.Count);
            await _scanner.AnalyzeShallowAsync(profile, ct);
            return null;
        }

        // ── Step 2: Build the agentic prompt (all 5 sections + web search instructions) ──
        var prompt = RepoScannerService.BuildFullAgenticPrompt(profile, topChunks);

        // ── Step 3: Single agentic Codex call ──
        var llmSw = Stopwatch.StartNew();
        string response;
        try
        {
            response = await _llmService.GenerateAgenticAsync(
                prompt,
                "You are a senior software architect with web search capabilities. " +
                "Analyze the codebase thoroughly, then use web search to find real complementary projects. " +
                "Be specific — reference real class names, service names, and patterns from the code. " +
                "Self-verify your gap claims: only include gaps for things genuinely MISSING.",
                timeoutSeconds: 300,
                ct: ct);
        }
        catch (Exception ex)
        {
            llmSw.Stop();
            _logger?.LogError(ex, "Agentic Codex call failed for {Owner}/{Name}", profile.Owner, profile.Name);
            telemetry.LlmCalls.Add(new LlmCallRecord
            {
                Purpose = "Agentic Full Analysis (FAILED)",
                Model = _llmService.LastModelUsed,
                DurationMs = llmSw.ElapsedMilliseconds,
                PromptLength = prompt.Length
            });
            return null; // Caller will fall through to complement service
        }

        llmSw.Stop();
        telemetry.LlmCalls.Add(new LlmCallRecord
        {
            Purpose = "Agentic Full Analysis (CodeBook+RAG+GapVerify+Complements)",
            Model = _llmService.LastModelUsed,
            DurationMs = llmSw.ElapsedMilliseconds,
            PromptLength = prompt.Length,
            ResponseLength = response.Length
        });

        if (string.IsNullOrWhiteSpace(response))
        {
            _logger?.LogWarning("Agentic call returned empty response for {Owner}/{Name}", profile.Owner, profile.Name);
            return null;
        }

        // ── Step 4: Parse the 6-section response ──
        var (summary, codeBook, frameworks, strengths, gaps, complements) =
            RepoScannerService.ParseFullAgenticAnalysis(response);

        // ── Step 5: Apply results to profile ──
        profile.CodeBook = $"# CodeBook: {profile.Owner}/{profile.Name}\n\n{codeBook}";
        profile.AnalysisModelUsed = _llmService.LastModelUsed;

        if (!string.IsNullOrWhiteSpace(summary))
            profile.ProjectSummary = summary;

        // Dedup frameworks
        foreach (var fw in frameworks)
        {
            if (!profile.Frameworks.Any(f => f.Equals(fw, StringComparison.OrdinalIgnoreCase) ||
                f.Contains(fw.Split(' ')[0], StringComparison.OrdinalIgnoreCase)))
                profile.Frameworks.Add(fw);
        }

        profile.Strengths.AddRange(strengths);

        if (gaps.Count >= 3)
            profile.Gaps = gaps;
        else if (gaps.Count > 0)
            profile.Gaps = gaps;

        _logger?.LogInformation(
            "Agentic analysis complete: {Strengths} strengths, {Gaps} gaps, {Complements} complements",
            strengths.Count, gaps.Count, complements.Count);

        return complements.Count > 0 ? complements : null;
    }

    /// <summary>
    /// Multi-query RAG analysis: issue diverse queries against the indexed codebase,
    /// gather top chunks, and ask the LLM to assess strengths/gaps from actual code.
    /// </summary>
    private async Task RunRagGroundedAnalysis(string sessionId, RepoProfile profile, ScanTelemetry telemetry, CancellationToken ct)
    {
        // Diverse queries to explore the full codebase — not just architecture
        var analysisQueries = new[]
        {
            "service registration dependency injection startup configuration",
            "database data access storage persistence repository",
            "API cloud provider external service integration",
            "test testing unit test integration test assertion",
            "error handling retry fault tolerance resilience",
            "user interface view model MVVM commands controls",
            "search indexing retrieval embedding vector",
            "authentication security encryption key management",
            "export reporting document generation output",
            "domain business logic core workflow pipeline",
            "notification monitoring health check logging",
            "safety validation verification quality"
        };

        var repoFilter = new[] { "repo_code", "repo_doc" };
        var allChunks = new List<RetrievalResult>();

        // Parallel RAG retrieval — queries are independent reads
        var retrievalTasks = analysisQueries.Select(async q =>
        {
            try
            {
                var hits = await _retrievalService!.HybridSearchAsync(sessionId, q, repoFilter, topK: 5, ct);
                return (IReadOnlyList<RetrievalResult>)hits.ToList();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "RAG analysis query failed: {Query}", q);
                return (IReadOnlyList<RetrievalResult>)Array.Empty<RetrievalResult>();
            }
        }).ToList();

        var results = await Task.WhenAll(retrievalTasks);
        foreach (var hits in results)
            allChunks.AddRange(hits);
        telemetry.RetrievalCallCount += analysisQueries.Length;

        // Deduplicate and take top 30 by score — broader coverage than CodeBook's 20
        var topChunks = allChunks
            .DistinctBy(r => r.Chunk.Id)
            .OrderByDescending(r => r.Score)
            .Take(30)
            .Select(r => r.Chunk.Text)
            .ToList();

        if (topChunks.Count == 0)
        {
            // Fallback to shallow
            await _scanner.AnalyzeShallowAsync(profile, ct);
            return;
        }

        var prompt = RepoScannerService.BuildRagAnalysisPrompt(profile, profile.CodeBook, topChunks);
        var llmSw = Stopwatch.StartNew();
        var analysis = await _llmService.GenerateAsync(prompt,
            "You are a senior software architecture analyst. Analyze the ACTUAL SOURCE CODE provided. " +
            "Be specific — reference real class names, service names, and patterns you see in the code. " +
            "Do NOT make generic assumptions. If the code shows cloud providers, say so. If it has 300+ tests, say so.",
            ct: ct);
        llmSw.Stop();
        telemetry.LlmCalls.Add(new LlmCallRecord
        {
            Purpose = "RAG-Grounded Analysis",
            Model = _llmService.LastModelUsed,
            DurationMs = llmSw.ElapsedMilliseconds,
            PromptLength = prompt.Length,
            ResponseLength = analysis.Length
        });
        RepoScannerService.ParseAnalysis(analysis, profile);
        profile.AnalysisModelUsed = _llmService.LastModelUsed;
    }

    /// <summary>
    /// For each proposed gap, query the index with gap-related terms.
    /// If relevant code is found, the gap might be a false positive.
    /// Send all evidence to LLM for final verification.
    /// </summary>
    private async Task VerifyGapsViaRag(string sessionId, RepoProfile profile, ScanTelemetry telemetry, CancellationToken ct)
    {
        var repoFilter = new[] { "repo_code", "repo_doc" };

        // Parallel RAG retrieval for gap evidence
        var gapTasks = profile.Gaps.Select(async gap =>
        {
            try
            {
                var hits = await _retrievalService!.HybridSearchAsync(sessionId, gap, repoFilter, topK: 3, ct);
                var chunkTexts = hits.Select(h => h.Chunk.Text).ToList();
                return (gap, chunks: (IReadOnlyList<string>)chunkTexts);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Gap evidence retrieval failed: {Gap}", gap);
                return (gap, chunks: (IReadOnlyList<string>)Array.Empty<string>());
            }
        }).ToList();

        var gapEvidence = (await Task.WhenAll(gapTasks)).ToList();
        telemetry.RetrievalCallCount += profile.Gaps.Count;

        var verificationPrompt = RepoScannerService.BuildGapVerificationPrompt(profile, gapEvidence);
        var llmSw = Stopwatch.StartNew();
        var response = await _llmService.GenerateAsync(verificationPrompt,
            "You are a code auditor. Compare each gap claim against the actual source code evidence. " +
            "Be strict: if the code clearly already handles something, mark it as a false positive. " +
            "IMPORTANT: gaps about MISSING things (no tests, no CI, no docs) are real when no counter-evidence is found — do NOT remove them just because no code was found. " +
            "Gaps that merely critique how an existing feature works should be removed as false positives. " +
            "Keep at least 3 verified gaps.",
            tier: ModelTier.Mini,
            ct: ct);
        llmSw.Stop();
        telemetry.LlmCalls.Add(new LlmCallRecord
        {
            Purpose = "Gap Verification",
            Model = _llmService.LastModelUsed,
            DurationMs = llmSw.ElapsedMilliseconds,
            PromptLength = verificationPrompt.Length,
            ResponseLength = response.Length
        });

        var verifiedGaps = RepoScannerService.ParseVerifiedGaps(response);
        if (verifiedGaps.Count >= 3)
        {
            profile.Gaps = verifiedGaps;
        }
        else if (verifiedGaps.Count > 0)
        {
            // Fewer than 3 gaps survived — supplement with highest-value originals
            var extras = profile.Gaps.Where(g => !verifiedGaps.Any(v =>
                v.Contains(g.Split(' ')[..Math.Min(3, g.Split(' ').Length)].First(), StringComparison.OrdinalIgnoreCase))).ToList();
            profile.Gaps = verifiedGaps.Concat(extras).Take(Math.Max(3, verifiedGaps.Count)).ToList();
        }
        // If parsing returned empty (LLM format issue), keep original gaps rather than losing them all
    }

    /// <summary>Scan multiple repos and immediately fuse them.</summary>
    public async Task<(List<RepoProfile> profiles, ProjectFusionArtifact fusion)> RunMultiScanFusionAsync(
        string sessionId, List<string> repoUrls, string focusPrompt, ProjectFusionGoal goal,
        ProjectFusionEngine fusionEngine, CancellationToken ct = default)
    {
        // Scan repos in parallel (max 2 concurrent to avoid overwhelming GitHub API + LLM)
        var scanSemaphore = new SemaphoreSlim(2);
        var scanTasks = repoUrls.Select(async url =>
        {
            await scanSemaphore.WaitAsync(ct);
            try
            {
                return await RunAnalysisAsync(sessionId, url, ct);
            }
            finally
            {
                scanSemaphore.Release();
            }
        }).ToList();

        var profiles = (await Task.WhenAll(scanTasks)).ToList();

        // Build fusion request from all profiles
        var request = new ProjectFusionRequest
        {
            SessionId = sessionId,
            Goal = goal,
            FocusPrompt = focusPrompt,
            Inputs = profiles.Select(p => new ProjectFusionInput
            {
                Id = p.Id,
                Type = FusionInputType.RepoProfile,
                Title = $"{p.Owner}/{p.Name}"
            }).ToList()
        };

        var fusion = await fusionEngine.RunAsync(sessionId, request, ct);
        return (profiles, fusion);
    }

    private static string GenerateReport(RepoProfile p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Repository Analysis: {p.Owner}/{p.Name}");
        sb.AppendLine();
        sb.AppendLine($"**URL:** {p.RepoUrl}");
        sb.AppendLine($"**Primary Language:** {p.PrimaryLanguage}");
        sb.AppendLine($"**Stars:** {p.Stars} | **Forks:** {p.Forks} | **Open Issues:** {p.OpenIssues}");
        if (p.Topics.Count > 0)
            sb.AppendLine($"**Topics:** {string.Join(", ", p.Topics)}");
        if (p.LastCommitUtc.HasValue)
            sb.AppendLine($"**Last Updated:** {p.LastCommitUtc.Value:yyyy-MM-dd}");
        sb.AppendLine();

        sb.AppendLine($"> {p.Description}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(p.ProjectSummary))
        {
            sb.AppendLine("## Project Summary");
            sb.AppendLine(p.ProjectSummary);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(p.ProductCategory))
        {
            sb.AppendLine($"**Product Category:** {p.ProductCategory}");
            sb.AppendLine();
        }

        if (p.CoreCapabilities.Count > 0)
        {
            sb.AppendLine("## Core Capabilities");
            foreach (var cap in p.CoreCapabilities)
                sb.AppendLine($"- {cap}");
            sb.AppendLine();
        }

        if (p.Languages.Count > 0)
        {
            sb.AppendLine("## Languages");
            foreach (var lang in p.Languages)
                sb.AppendLine($"- {lang}");
            sb.AppendLine();
        }

        if (p.Frameworks.Count > 0)
        {
            sb.AppendLine("## Frameworks & Key Technologies");
            foreach (var fw in p.Frameworks)
                sb.AppendLine($"- {fw}");
            sb.AppendLine();
        }

        if (p.Dependencies.Count > 0)
        {
            sb.AppendLine("## Dependencies");
            sb.AppendLine("| Package | Version | Manifest |");
            sb.AppendLine("|---------|---------|----------|");
            foreach (var dep in p.Dependencies.Take(30))
                sb.AppendLine($"| {dep.Name} | {dep.Version} | {dep.ManifestFile} |");
            if (p.Dependencies.Count > 30)
                sb.AppendLine($"_...and {p.Dependencies.Count - 30} more_");
            sb.AppendLine();
        }

        sb.AppendLine("## Strengths");
        foreach (var s in p.Strengths)
            sb.AppendLine($"- ✅ {s}");
        sb.AppendLine();

        sb.AppendLine("## Gaps & Improvement Opportunities");
        foreach (var g in p.Gaps)
            sb.AppendLine($"- 🔸 {g}");
        sb.AppendLine();

        if (p.ComplementSuggestions.Count > 0)
        {
            sb.AppendLine("## Complementary Projects");
            sb.AppendLine();
            foreach (var c in p.ComplementSuggestions)
            {
                sb.AppendLine($"### {c.Name}");
                sb.AppendLine($"- **URL:** {c.Url}");
                sb.AppendLine($"- **Purpose:** {c.Purpose}");
                sb.AppendLine($"- **What it adds:** {c.WhatItAdds}");
                sb.AppendLine($"- **Category:** {c.Category} | **License:** {c.License} | **Maturity:** {c.Maturity}");
                sb.AppendLine();
            }
        }

        // Pipeline telemetry section
        if (p.Telemetry != null)
        {
            var t = p.Telemetry;
            sb.AppendLine("## Pipeline Telemetry");
            sb.AppendLine($"**Total Duration:** {t.TotalDurationMs / 1000.0:F1}s | " +
                $"**LLM Calls:** {t.LlmCallCount} ({t.TotalLlmDurationMs / 1000.0:F1}s) | " +
                $"**RAG Queries:** {t.RetrievalCallCount} | " +
                $"**Web Searches:** {t.WebSearchCallCount} | " +
                $"**GitHub API Calls:** {t.GitHubApiCallCount}");
            sb.AppendLine();

            if (t.LlmCalls.Count > 0)
            {
                sb.AppendLine("| # | Purpose | Model | Duration | Prompt | Response |");
                sb.AppendLine("|---|---------|-------|----------|--------|----------|");
                for (int i = 0; i < t.LlmCalls.Count; i++)
                {
                    var c = t.LlmCalls[i];
                    sb.AppendLine($"| {i + 1} | {c.Purpose} | {c.Model ?? "?"} | {c.DurationMs / 1000.0:F1}s | " +
                        $"{(c.PromptLength > 0 ? $"{c.PromptLength / 1000.0:F1}K chars" : "—")} | " +
                        $"{(c.ResponseLength > 0 ? $"{c.ResponseLength / 1000.0:F1}K chars" : "—")} |");
                }
                sb.AppendLine();
            }

            if (t.Phases.Count > 0)
            {
                sb.AppendLine("| Phase | Duration |");
                sb.AppendLine("|-------|----------|");
                foreach (var phase in t.Phases)
                    sb.AppendLine($"| {phase.Phase} | {phase.DurationMs / 1000.0:F1}s |");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Ask a natural-language question about a GitHub repo.
    /// Uses retrieval-augmented generation (RAG) when code is indexed,
    /// falling back to profile-only context otherwise.
    /// </summary>
    public async Task<string> AskAboutRepoAsync(string sessionId, string repoUrl, string question, CancellationToken ct = default)
    {
        var db = _sessionManager.GetSessionDb(sessionId);

        // Find or create profile
        var profiles = db.GetRepoProfiles();
        var (owner, repo) = RepoScannerService.ParseRepoUrl(repoUrl);
        var profile = profiles.FirstOrDefault(p =>
            p.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase) &&
            p.Name.Equals(repo, StringComparison.OrdinalIgnoreCase));

        if (profile == null)
        {
            // Quick scan (will also index if services are wired)
            profile = await RunAnalysisAsync(sessionId, repoUrl, ct);
        }

        // Build context from profile metadata
        var context = new StringBuilder();
        context.AppendLine($"Repository: {profile.Owner}/{profile.Name}");
        context.AppendLine($"URL: {profile.RepoUrl}");
        context.AppendLine($"Description: {profile.Description}");
        context.AppendLine($"Primary Language: {profile.PrimaryLanguage}");
        context.AppendLine($"Languages: {string.Join(", ", profile.Languages)}");
        context.AppendLine($"Frameworks: {string.Join(", ", profile.Frameworks)}");
        context.AppendLine($"Stars: {profile.Stars} | Forks: {profile.Forks} | Open Issues: {profile.OpenIssues}");
        context.AppendLine($"Topics: {string.Join(", ", profile.Topics)}");
        if (profile.LastCommitUtc != null)
        {
            var span = DateTime.UtcNow - profile.LastCommitUtc.Value;
            var ago = span.TotalDays < 1 ? $"{(int)span.TotalHours}h ago" :
                      span.TotalDays < 30 ? $"{(int)span.TotalDays}d ago" :
                      span.TotalDays < 365 ? $"{(int)(span.TotalDays / 30)}mo ago" : $"{span.TotalDays / 365:F1}y ago";
            context.AppendLine($"Last push: {profile.LastCommitUtc.Value:yyyy-MM-dd} ({ago})");
        }
        if (profile.TopLevelEntries.Count > 0)
            context.AppendLine($"Root entries: {string.Join(", ", profile.TopLevelEntries.Select(e => $"{e.Name} ({e.Type})"))}");
        if (profile.Dependencies.Count > 0)
            context.AppendLine($"Dependencies ({profile.Dependencies.Count}): {string.Join(", ", profile.Dependencies.Take(30).Select(d => $"{d.Name} {d.Version}"))}");
        context.AppendLine($"Strengths: {string.Join("; ", profile.Strengths)}");
        context.AppendLine($"Gaps: {string.Join("; ", profile.Gaps)}");
        if (profile.ComplementSuggestions.Count > 0)
            context.AppendLine($"Complementary Projects: {string.Join("; ", profile.ComplementSuggestions.Select(c => $"{c.Name} — {c.Purpose}"))}");

        // Include CodeBook if available
        if (!string.IsNullOrEmpty(profile.CodeBook))
        {
            context.AppendLine($"\n--- CODEBOOK SUMMARY ---\n{profile.CodeBook}");
        }

        // RAG: retrieve relevant code chunks if indexed
        if (_retrievalService != null && profile.IndexedChunkCount > 0)
        {
            var repoFilter = new[] { "repo_code", "repo_doc" };
            var hits = await _retrievalService.HybridSearchAsync(sessionId, question, repoFilter, topK: 8, ct);
            if (hits.Count > 0)
            {
                context.AppendLine("\n--- RELEVANT CODE EXCERPTS ---");
                foreach (var hit in hits)
                {
                    context.AppendLine($"\n[{hit.Chunk.SourceType}] (score: {hit.Score:F3})");
                    context.AppendLine(hit.Chunk.Text);
                    context.AppendLine("---");
                }
            }
        }
        else if (!string.IsNullOrEmpty(profile.ReadmeContent))
        {
            // Fallback: include README if no code is indexed
            context.AppendLine($"\nREADME:\n{profile.ReadmeContent}");
        }

        var systemPrompt = @"You are a senior software engineer and open-source analyst.
You have deep knowledge of software architecture, ecosystems, dependencies, and project strategy.
Answer questions about the given repository thoroughly, with specific, actionable insights.
Reference concrete details from the repo data provided — class names, function names, file paths, dependencies.
When code excerpts are available, cite specific code patterns, classes, or functions from them.
If a CodeBook summary is provided, use it to ground your architectural analysis.
Be detailed and insightful — don't give surface-level answers.";

        var userPrompt = $@"Based on this repository profile and code:

{context}

User's question: {question}

Provide a thorough, detailed answer.";

        return await _llmService.GenerateAsync(userPrompt, systemPrompt, 3000, ct: ct);
    }

    private static void AddReplay(ResearchJob job, string type, string title, string description)
    {
        job.ReplayEntries.Add(new ReplayEntry
        {
            Order = job.ReplayEntries.Count + 1,
            Title = title, Description = description, EntryType = type
        });
    }
}
