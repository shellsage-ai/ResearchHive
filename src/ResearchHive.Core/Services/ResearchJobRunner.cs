using ResearchHive.Core.Configuration;
using ResearchHive.Core.Data;
using ResearchHive.Core.Models;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace ResearchHive.Core.Services;

/// <summary>
/// Implements the agentic research loop per spec/RESEARCH_AGENT_LOOP.md:
/// Plan → Search (multi-lane) → Acquire & Snapshot → Extract & Index → 
/// Evaluate Coverage → Draft Answer → Validate → Produce Reports + Replay
/// </summary>
public class ResearchJobRunner
{
    private readonly SessionManager _sessionManager;
    private readonly SnapshotService _snapshotService;
    private readonly IndexService _indexService;
    private readonly RetrievalService _retrievalService;
    private readonly LlmService _llmService;
    private readonly EmbeddingService _embeddingService;
    private readonly AppSettings _settings;
    private readonly BrowserSearchService _browserSearch;
    private readonly GoogleSearchService _googleSearch;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCts = new();

    // Cached prompt embedding for IsContentRelevantAsync — avoids re-embedding the same prompt
    // for every snapshot check (~200-500ms per embedding call on local Ollama)
    private float[]? _cachedPromptEmbedding;
    private string? _cachedPromptText;

    /// <summary>
    /// Fired at each state transition during a research job, providing real-time progress data.
    /// </summary>
    public event EventHandler<JobProgressEventArgs>? ProgressChanged;

    public ResearchJobRunner(
        SessionManager sessionManager,
        SnapshotService snapshotService,
        IndexService indexService,
        RetrievalService retrievalService,
        LlmService llmService,
        EmbeddingService embeddingService,
        AppSettings settings,
        BrowserSearchService browserSearch,
        GoogleSearchService googleSearch)
    {
        _sessionManager = sessionManager;
        _snapshotService = snapshotService;
        _indexService = indexService;
        _retrievalService = retrievalService;
        _llmService = llmService;
        _embeddingService = embeddingService;
        _settings = settings;
        _browserSearch = browserSearch;
        _googleSearch = googleSearch;
    }

    public async Task<ResearchJob> RunAsync(string sessionId, string prompt, JobType jobType = JobType.Research,
        int targetSources = 5, CancellationToken ct = default)
    {
        var db = _sessionManager.GetSessionDb(sessionId);
        var session = _sessionManager.GetSession(sessionId)!;
        var sourceHealth = new List<SourceHealthEntry>();

        var job = new ResearchJob
        {
            SessionId = sessionId,
            Type = jobType,
            Prompt = prompt,
            TargetSourceCount = targetSources,
            State = JobState.Pending,
            SearchLanes = GetSearchLanes(session.Pack)
        };

        db.SaveJob(job);
        AddStep(db, job, "Created", $"Job created for prompt: {prompt}", JobState.Pending);
        AddReplay(job, "start", "Job Started", $"Research job created for: {prompt}");

        // Track CTS for cancel support
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeCts[job.Id] = cts;
        var token = cts.Token;

        void EmitProgress(string desc, double coverage = 0)
        {
            var poolStats = _browserSearch.PoolStats;
            ProgressChanged?.Invoke(this, new JobProgressEventArgs
            {
                JobId = job.Id,
                State = job.State,
                StepDescription = desc,
                SourcesFound = job.AcquiredSourceIds.Count,
                SourcesFailed = sourceHealth.Count(s => s.Status != SourceFetchStatus.Success),
                SourcesBlocked = sourceHealth.Count(s => s.Status == SourceFetchStatus.Blocked),
                TargetSources = targetSources,
                CoverageScore = coverage,
                CurrentIteration = job.CurrentIteration,
                MaxIterations = job.MaxIterations,
                SourceHealth = sourceHealth.ToList(),
                LogMessage = desc,
                SubQuestionsTotal = job.SubQuestions.Count,
                SubQuestionsAnswered = job.SubQuestionCoverage.Count(kv => kv.Value == "answered"),
                GroundingScore = job.GroundingScore,
                BrowserPoolAvailable = poolStats.Total,
                BrowserPoolTotal = poolStats.Max,
            });
        }

        // Surface all Codex CLI events (web searches, reasoning, commands, etc.) in the activity log
        void OnCodexActivity(IReadOnlyList<CodexEvent> events)
        {
            foreach (var e in events)
            {
                var label = e.Type switch
                {
                    "web_search" => "[Codex Search]",
                    "web_search_start" => "[Codex Search]",
                    "reasoning" => "[Codex Reasoning]",
                    "agent_message" => "[Codex Response]",
                    "command_execution" => "[Codex Command]",
                    "command_start" => "[Codex Command]",
                    "file_change" => "[Codex File]",
                    "turn.completed" => "[Codex Done]",
                    "error" => "[Codex Error]",
                    _ => "[Codex]"
                };
                EmitProgress($"{label} {e.Detail}");
            }
        }
        _llmService.CodexActivityOccurred += OnCodexActivity;

        try
        {
            // ═══════════════════════════════════════════════════════════════════
            // STREAMLINED CODEX PATH — single synthesis call with native web search
            // ═══════════════════════════════════════════════════════════════════
            // When StreamlinedCodexMode is on and Codex CLI OAuth is active, skip the
            // multi-call orchestration (plan, decompose, coverage loop, deep search).
            // Instead: minimal Chrome search → single Codex synthesis (web search ON)
            // → grounding → reports. Saves 5-8 Codex calls and 2-5 min wall clock.
            // Ollama and other API providers always take the full pipeline below.
            if (_settings.StreamlinedCodexMode && _llmService.IsCodexOAuthActive)
            {
                return await RunStreamlinedCodexPathAsync(
                    sessionId, db, session, job, prompt, targetSources,
                    sourceHealth, EmitProgress, token);
            }

            // ═══════════════════════════════════════════════════════════════════
            // FULL PIPELINE — used by Ollama, cloud APIs, and standard Codex mode
            // ═══════════════════════════════════════════════════════════════════

            // 1) Plan
            job.State = JobState.Planning;
            db.SaveJob(job);
            AddStep(db, job, "Planning", "Generating research plan and queries", JobState.Planning);
            EmitProgress("Generating research plan and search queries...");

            var planPrompt = $@"Generate 5 diverse web search queries that would find high-quality sources to answer this research question. Cover different angles and sub-topics.

Research question: {prompt}
Domain: {session.Pack}

Return ONLY numbered queries, no explanation:
1. [query]
2. [query]
3. [query]
4. [query]
5. [query]";

            // Sequential for local Ollama (GPU contention makes parallel slower),
            // parallel for cloud providers
            string planResponse;
            List<string> subQuestions;
            var isLocalOnly = _settings.Routing == RoutingStrategy.LocalOnly 
                || _settings.Routing == RoutingStrategy.LocalWithCloudFallback;

            if (isLocalOnly)
            {
                // Sequential: fast queries first (~5-8s with slim prompt), then decompose
                planResponse = await _llmService.GenerateAsync(planPrompt, maxTokens: 300, ct: token);
                subQuestions = await DecomposeQuestionAsync(prompt, session.Pack, token);
            }
            else
            {
                // Cloud: parallel is fine — no GPU contention
                var planTask = _llmService.GenerateAsync(planPrompt, maxTokens: 300, ct: token);
                var decomposeTask = DecomposeQuestionAsync(prompt, session.Pack, token);
                await Task.WhenAll(planTask, decomposeTask);
                planResponse = planTask.Result;
                subQuestions = decomposeTask.Result;
            }

            job.Plan = planResponse;
            job.SearchQueries = ExtractQueries(planResponse, prompt);
            job.SubQuestions = subQuestions;
            db.SaveJob(job);
            AddReplay(job, "plan", "Research Plan Created", job.Plan ?? "");
            AddStep(db, job, "Decomposed", $"Identified {job.SubQuestions.Count} sub-questions", JobState.Planning);
            AddReplay(job, "decompose", "Question Decomposition",
                $"Sub-questions:\n{string.Join("\n", job.SubQuestions.Select((q, i) => $"  {i + 1}. {q}"))}");
            EmitProgress($"Plan created with {job.SearchQueries.Count} queries + {job.SubQuestions.Count} sub-questions");

            // Enhance search queries with sub-questions (add top unrepresented sub-Qs)
            foreach (var sq in job.SubQuestions.Take(3))
            {
                if (!job.SearchQueries.Any(q => q.Contains(sq, StringComparison.OrdinalIgnoreCase)))
                    job.SearchQueries.Add(sq);
            }

            // Extra iterations add an LLM coverage-eval call each but yield diminishing
            // evidence: iteration 0 already runs multi-lane search + deep search drill-down.
            // Iterations 1-2 just re-search with slightly refined queries (no deep search),
            // costing 10-30s each on Ollama for marginal gain. Cap at 1 for all providers.
            if (job.MaxIterations > 1)
                job.MaxIterations = 1;

            // Iterative loop
            for (int iteration = 0; iteration < job.MaxIterations && !token.IsCancellationRequested; iteration++)
            {
                job.CurrentIteration = iteration + 1;
                
                // Check for pause/cancel
                var latestJob = db.GetJob(job.Id);
                if (latestJob?.State == JobState.Paused)
                {
                    job.State = JobState.Paused;
                    db.SaveJob(job);
                    AddStep(db, job, "Paused", "Job paused by user", JobState.Paused);
                    EmitProgress("Job paused by user");
                    return job;
                }
                if (latestJob?.State == JobState.Cancelled)
                {
                    job.State = JobState.Cancelled;
                    db.SaveJob(job);
                    AddStep(db, job, "Cancelled", "Job cancelled by user", JobState.Cancelled);
                    EmitProgress("Job cancelled");
                    return job;
                }

                // 2) Search (multi-lane)
                job.State = JobState.Searching;
                db.SaveJobState(job.Id, JobState.Searching);
                AddStep(db, job, "Searching", $"Iteration {iteration + 1}: searching {job.SearchQueries.Count} queries", JobState.Searching);
                EmitProgress($"Iteration {iteration + 1}/{job.MaxIterations}: Searching across {BrowserSearchService.SearchEngines.Length + 1} engines (incl. Google)...");

                var searchUrls = await SearchMultiLaneAsync(job.SearchQueries, session.Pack, token);

                // Pre-filter: score URLs by keyword relevance to the research prompt
                // Removes obviously irrelevant URLs before spending time capturing them
                var relevantUrls = ScoreAndFilterUrls(searchUrls, prompt, job.SearchQueries, _settings.SourceQualityRanking);
                AddReplay(job, "search", $"Search Iteration {iteration + 1}",
                    $"Found {searchUrls.Count} candidate URLs, {relevantUrls.Count} passed relevance filter");
                EmitProgress($"Found {searchUrls.Count} URLs, {relevantUrls.Count} relevant");

                // 3) Acquire & Snapshot — parallel with auto-tuned concurrency (B3)
                job.State = JobState.Acquiring;
                db.SaveJobState(job.Id, JobState.Acquiring);

                var remaining = targetSources - job.AcquiredSourceIds.Count;
                var urlsToAcquire = relevantUrls.Take(Math.Min(remaining * 2, 20)).ToList(); // Dynamic overshoot, capped
                var fetchConcurrency = Math.Min(10, Math.Max(4, urlsToAcquire.Count / 2)); // Auto-tune concurrency
                var acquireSemaphore = new SemaphoreSlim(fetchConcurrency);
                var acquireResults = new ConcurrentBag<(string url, Snapshot? snapshot, Exception? error)>();

                var acquireTasks = urlsToAcquire.Select(async url =>
                {
                    if (token.IsCancellationRequested || job.AcquiredSourceIds.Count >= targetSources) return;
                    await acquireSemaphore.WaitAsync(token);
                    try
                    {
                        EmitProgress($"Acquiring: {url}");
                        var snapshot = await _snapshotService.CaptureUrlAsync(sessionId, url, ct: token);
                        acquireResults.Add((url, snapshot, null));
                    }
                    catch (Exception ex)
                    {
                        acquireResults.Add((url, null, ex));
                    }
                    finally
                    {
                        acquireSemaphore.Release();
                    }
                }).ToList();

                await Task.WhenAll(acquireTasks);

                // Process results sequentially for consistent state
                var snapshotsToIndex = new List<Snapshot>();
                foreach (var (url, snapshot, error) in acquireResults)
                {
                    if (job.AcquiredSourceIds.Count >= targetSources) break;

                    if (error != null)
                    {
                        if (error is TaskCanceledException)
                        {
                            sourceHealth.Add(new SourceHealthEntry { Url = url, Status = SourceFetchStatus.Timeout, Reason = "Request timed out" });
                            AddStep(db, job, "Timeout", $"Timeout capturing {url}", JobState.Acquiring, false);
                        }
                        else
                        {
                            sourceHealth.Add(new SourceHealthEntry { Url = url, Status = SourceFetchStatus.Error, Reason = error.Message });
                            AddStep(db, job, "AcquireError", $"Error capturing {url}: {error.Message}", JobState.Acquiring, false);
                        }
                        continue;
                    }

                    if (snapshot != null && !snapshot.IsBlocked)
                    {
                        // Post-capture content relevance gate: check if page content
                        // is topically relevant before counting as an acquired source
                        if (!await IsContentRelevantAsync(snapshot, prompt, token))
                        {
                            AddStep(db, job, "Filtered", $"Content irrelevant, skipping: {snapshot.Title} ({url})", JobState.Acquiring, false);
                            sourceHealth.Add(new SourceHealthEntry
                            {
                                Url = url, Title = snapshot.Title ?? url,
                                Status = SourceFetchStatus.Error, Reason = "Content not relevant to research question"
                            });
                            continue;
                        }

                        job.AcquiredSourceIds.Add(snapshot.Id);
                        AddStep(db, job, "Acquired", $"Captured: {snapshot.Title} ({url})", JobState.Acquiring);
                        AddReplay(job, "snapshot", $"Captured: {snapshot.Title}",
                            $"URL: {url}\nTitle: {snapshot.Title}\nSnapshot ID: {snapshot.Id}",
                            snapshot.Id);

                        sourceHealth.Add(new SourceHealthEntry
                        {
                            Url = url, Title = snapshot.Title ?? url,
                            Status = SourceFetchStatus.Success, HttpStatus = snapshot.HttpStatus
                        });

                        snapshotsToIndex.Add(snapshot);
                    }
                    else if (snapshot != null)
                    {
                        AddStep(db, job, "Blocked", $"Blocked: {url} - {snapshot.BlockReason}", JobState.Acquiring, false);
                        AddReplay(job, "blocked", $"Source Blocked", $"URL: {url}\nReason: {snapshot.BlockReason}");
                        sourceHealth.Add(new SourceHealthEntry
                        {
                            Url = url, Title = url,
                            Status = snapshot.BlockReason?.Contains("paywall", StringComparison.OrdinalIgnoreCase) == true
                                ? SourceFetchStatus.Paywall : SourceFetchStatus.Blocked,
                            HttpStatus = snapshot.HttpStatus,
                            Reason = snapshot.BlockReason
                        });
                    }
                }

                EmitProgress($"Acquired {job.AcquiredSourceIds.Count}/{targetSources} sources");

                // 4) Extract & Index — parallel indexing of all acquired snapshots
                if (snapshotsToIndex.Count > 0)
                {
                    job.State = JobState.Extracting;
                    EmitProgress($"Indexing {snapshotsToIndex.Count} sources in parallel...");
                    var indexTasks = snapshotsToIndex.Select(async snap =>
                    {
                        await _indexService.IndexSnapshotAsync(sessionId, snap, token);
                        AddReplay(job, "extract", $"Indexed: {snap.Title}",
                            $"Text extracted and indexed for retrieval");
                    }).ToList();
                    await Task.WhenAll(indexTasks);
                    db.SaveJob(job);
                }

                // 5) Evaluate Coverage (semantic via LLM when sub-questions available)
                job.State = JobState.Evaluating;
                db.SaveJobState(job.Id, JobState.Evaluating);

                var results = await _retrievalService.HybridSearchAsync(sessionId, prompt, ct: token);
                var coverage = await EvaluateCoverageWithLlmAsync(prompt, job.SubQuestions, results, job, token);
                AddStep(db, job, "Evaluated", $"Coverage: {coverage.Score:P0}, gaps: {coverage.Gaps.Count}", JobState.Evaluating);
                AddReplay(job, "evaluate", "Coverage Evaluation",
                    $"Score: {coverage.Score:P0}\nSources: {job.AcquiredSourceIds.Count}/{targetSources}\n" +
                    $"Answered: {coverage.AnsweredSubQuestions.Count} | Unanswered: {coverage.UnansweredSubQuestions.Count}\n" +
                    $"Gaps: {string.Join(", ", coverage.Gaps)}");
                EmitProgress($"Coverage: {coverage.Score:P0} ({job.AcquiredSourceIds.Count} sources, {coverage.AnsweredSubQuestions.Count}/{job.SubQuestions.Count} sub-Qs answered)", coverage.Score);

                // Exit when quality is sufficient, OR when we've hit max sources AND have some coverage
                if (coverage.Score >= 0.7)
                    break;
                if (job.AcquiredSourceIds.Count >= targetSources && coverage.Score >= 0.4)
                    break;

                // Pivot mechanism: if coverage is very low after first iteration,
                // it means the captured sources are probably off-topic. Clear bad sources
                // and try refined queries from the gaps.
                if (iteration > 0 && coverage.Score < 0.25 && coverage.Gaps.Any())
                {
                    EmitProgress($"Low coverage ({coverage.Score:P0}) — pivoting to gap-focused queries...");
                    AddReplay(job, "pivot", "Low Coverage Pivot",
                        $"Coverage only {coverage.Score:P0} after {iteration + 1} iterations. Focusing on: {string.Join(", ", coverage.Gaps.Take(3))}");
                    // Use gaps directly as new search queries instead of appending to prompt
                    job.SearchQueries = coverage.Gaps.Take(5).Select(g => CleanSearchQuery(g)).ToList();
                }

                // 6) Multi-level deep search — agent analyzes acquired content for drill-down topics
                if (iteration == 0 && job.AcquiredSourceIds.Count >= 2 && !token.IsCancellationRequested)
                {
                    EmitProgress("Agent analyzing findings for deeper search opportunities...");
                    var deepSearchQueries = await GenerateDeepSearchQueriesAsync(sessionId, prompt, session.Pack, token);
                    if (deepSearchQueries.Count > 0)
                    {
                        AddStep(db, job, "DeepSearch", $"Agent identified {deepSearchQueries.Count} drill-down topics", JobState.Searching);
                        AddReplay(job, "deepsearch", "Multi-Level Search",
                            $"Agent drill-down queries: {string.Join(", ", deepSearchQueries)}");
                        EmitProgress($"Launching {deepSearchQueries.Count} targeted deep searches...");

                        job.State = JobState.Searching;
                        var deepUrls = await SearchMultiLaneAsync(deepSearchQueries, session.Pack, token);
                        EmitProgress($"Deep search found {deepUrls.Count} additional URLs");

                        // Acquire deep search results (parallel, auto-tuned)
                        if (deepUrls.Count > 0 && job.AcquiredSourceIds.Count < targetSources)
                        {
                            job.State = JobState.Acquiring;
                            var deepRemaining = targetSources - job.AcquiredSourceIds.Count;
                            var deepFetchCount = Math.Min(8, Math.Max(3, deepUrls.Count / 2));
                            var deepAcquire = new SemaphoreSlim(deepFetchCount);
                            var deepResults = new ConcurrentBag<(string url, Snapshot? snapshot, Exception? error)>();

                            var deepTasks = deepUrls.Take(Math.Min(deepRemaining * 2, 8)).Select(async url =>
                            {
                                if (token.IsCancellationRequested || job.AcquiredSourceIds.Count >= targetSources) return;
                                await deepAcquire.WaitAsync(token);
                                try
                                {
                                    var snap = await _snapshotService.CaptureUrlAsync(sessionId, url, ct: token);
                                    deepResults.Add((url, snap, null));
                                }
                                catch (Exception ex) { deepResults.Add((url, null, ex)); }
                                finally { deepAcquire.Release(); }
                            }).ToList();

                            await Task.WhenAll(deepTasks);

                            // Collect valid snapshots, then index in parallel
                            var deepIndexTasks = new List<Task>();
                            foreach (var (url, snap, err) in deepResults)
                            {
                                if (job.AcquiredSourceIds.Count >= targetSources) break;
                                if (err != null || snap == null || snap.IsBlocked)
                                {
                                    sourceHealth.Add(new SourceHealthEntry { Url = url, Status = err is TaskCanceledException ? SourceFetchStatus.Timeout : SourceFetchStatus.Error, Reason = err?.Message ?? snap?.BlockReason ?? "Unknown" });
                                    continue;
                                }
                                job.AcquiredSourceIds.Add(snap.Id);
                                sourceHealth.Add(new SourceHealthEntry { Url = url, Title = snap.Title ?? url, Status = SourceFetchStatus.Success, HttpStatus = snap.HttpStatus });
                                deepIndexTasks.Add(_indexService.IndexSnapshotAsync(sessionId, snap, token));
                                AddStep(db, job, "DeepAcquired", $"Deep: {snap.Title} ({url})", JobState.Acquiring);
                            }
                            if (deepIndexTasks.Count > 0)
                                await Task.WhenAll(deepIndexTasks);
                            db.SaveJob(job);
                            EmitProgress($"Deep search acquired {job.AcquiredSourceIds.Count}/{targetSources} total sources");
                        }
                    }
                }

                // 7) Refine queries for next iteration (already handled by pivot or gap logic above)
                if (coverage.Gaps.Any() && iteration == 0)
                {
                    var refinedQueries = coverage.Gaps.Select(g => CleanSearchQuery($"{prompt} {g}")).ToList();
                    job.SearchQueries = refinedQueries;
                    AddReplay(job, "refine", "Queries Refined", $"Refined queries: {string.Join(", ", refinedQueries)}");
                    EmitProgress($"Refining queries based on {coverage.Gaps.Count} gap(s)");
                }
            }

            // 7) Evidence Pack
            EmitProgress("Building evidence pack from indexed sources...");
            var allEvidence = await _retrievalService.HybridSearchAsync(sessionId, prompt, 30, token);

            // CRITICAL: Filter to only chunks from THIS job's acquired sources.
            // Without this filter, chunks from prior research jobs in the same session
            // leak into the evidence pack, producing off-topic citations.
            var jobSourceSet = new HashSet<string>(job.AcquiredSourceIds);
            var evidenceResults = allEvidence.Where(er => jobSourceSet.Contains(er.SourceId)).ToList();
            if (allEvidence.Count != evidenceResults.Count)
                EmitProgress($"Filtered evidence: {evidenceResults.Count}/{allEvidence.Count} chunks from {jobSourceSet.Count} job sources");

            // Build source URL lookup: SourceId → URL (batch query, not N+1)
            var sourceIds = evidenceResults.Select(er => er.SourceId).Distinct();
            var snapshotMap = db.GetSnapshotsByIds(sourceIds);
            var sourceUrlMap = snapshotMap
                .Where(kv => !string.IsNullOrEmpty(kv.Value.Url))
                .ToDictionary(kv => kv.Key, kv => kv.Value.Url);

            // Deduplicate evidence by source URL: take only the best-scoring chunk per source.
            // This prevents 8 citations from the same page dominating the report.
            var deduplicatedResults = DeduplicateEvidenceBySource(evidenceResults, sourceUrlMap);

            // Create citations from deduplicated evidence (up to 20 diverse sources)
            int citationNum = 1;
            var citations = new List<Citation>();
            foreach (var er in deduplicatedResults.Take(20))
            {
                var citation = new Citation
                {
                    SessionId = sessionId,
                    JobId = job.Id,
                    Type = er.SourceType == "snapshot" ? CitationType.WebSnapshot :
                           er.SourceType == "capture" ? CitationType.OcrImage : CitationType.File,
                    SourceId = er.SourceId,
                    ChunkId = er.Chunk.Id,
                    StartOffset = er.Chunk.StartOffset,
                    EndOffset = er.Chunk.EndOffset,
                    Excerpt = er.Chunk.Text.Length > 400 ? er.Chunk.Text[..400] + "..." : er.Chunk.Text,
                    Label = $"[{citationNum++}]"
                };
                citations.Add(citation);
                db.SaveCitation(citation);
            }

            // 8) Draft Answer
            job.State = JobState.Drafting;
            db.SaveJobState(job.Id, JobState.Drafting);
            AddStep(db, job, "Drafting", "Generating evidence-based report", JobState.Drafting);
            EmitProgress("Synthesizing evidence into report...");

            var evidenceContext = BuildEvidenceContext(deduplicatedResults, citations, sourceUrlMap);

            // Build sub-question context for the synthesis prompt
            var subQContext = job.SubQuestions.Count > 1
                ? "\nSub-Questions to Address:\n" + string.Join("\n", job.SubQuestions.Select((q, i) => $"{i + 1}. {q}"))
                : "";

            // Choose synthesis approach
            string draftResponse;

            if (_settings.SectionalReports)
            {
                // Section-by-section generation: each section gets targeted evidence
                // and a focused prompt. Produces longer, more detailed reports.
                draftResponse = await GenerateSectionalReportAsync(
                    sessionId, prompt, job.SubQuestions, deduplicatedResults, citations,
                    sourceUrlMap, evidenceContext, session.Pack, job,
                    (desc, _) => EmitProgress(desc),
                    enableWebSearch: false, ct: token);
            }
            else
            {
                // Legacy single-pass synthesis (fallback)
                var draftPrompt = BuildSynthesisPrompt(prompt, subQContext, evidenceContext, citations.Count);
                draftResponse = await GenerateSynthesisAsync(
                    sessionId, prompt, draftPrompt, deduplicatedResults, citations, sourceUrlMap, job, token);
            }

            // Detect LLM failure — don't process error responses as valid synthesis
            var trimmedDraft = draftResponse.TrimStart();
            if (trimmedDraft.StartsWith("[LLM_UNAVAILABLE]") || trimmedDraft.StartsWith("[CLOUD_ERROR]"))
            {
                var errorDetail = draftResponse.Contains(']') ? draftResponse[(draftResponse.IndexOf(']') + 1)..].Trim() : draftResponse;
                EmitProgress($"AI synthesis failed: {errorDetail}");
                AddStep(db, job, "SynthesisFailed", draftResponse, JobState.Reporting, false, "AI provider unavailable");
                // Use a clear error report instead of garbage deterministic output
                draftResponse = $"## Synthesis Failed\n\nThe AI provider did not return a response for this research. " +
                    $"This can happen when:\n- The provider is rate-limited\n- Authentication has expired\n- Ollama is not running (for local routing)\n- The service is temporarily unavailable\n\n" +
                    $"**Routing:** {_settings.Routing}\n**Provider:** {_settings.PaidProvider}\n**Auth Mode:** {_settings.ChatGptPlusAuth}\n\n" +
                    $"**Error:** {errorDetail}\n\n" +
                    $"Try running the research again, or check your settings.";
            }

            // Parse sections
            job.MostSupportedView = ExtractSection(draftResponse, "Most Supported View");
            job.CredibleAlternatives = ExtractSection(draftResponse, "Credible Alternatives");

            // Compute grounding early — if already high, skip the LLM sufficiency call entirely
            var preGrounding = ComputeGroundingScore(ExtractClaims(draftResponse));

            SufficiencyResult sufficiency;
            if (preGrounding >= 0.6)
            {
                // Good grounding means well-cited draft — skip the ~15-30s sufficiency LLM call
                sufficiency = new SufficiencyResult { Sufficient = true, Score = preGrounding, MissingTopics = new(), WeakClaims = new() };
                AddStep(db, job, "SufficiencyCheck", $"Skipped (grounding {preGrounding:P0} ≥ 60%)", JobState.Validating);
                AddReplay(job, "sufficiency", "Sufficiency Check (Skipped)",
                    $"Grounding: {preGrounding:P0} — high enough to skip sufficiency LLM call");
                EmitProgress($"Grounding {preGrounding:P0} — skipping sufficiency check");
            }
            else
            {
                // Low grounding — need LLM to identify what's missing
                EmitProgress("Verifying answer sufficiency...");
                sufficiency = await VerifyAnswerSufficiencyAsync(prompt, job.SubQuestions, draftResponse, token);
                AddStep(db, job, "SufficiencyCheck", $"Sufficient: {sufficiency.Sufficient}, Score: {sufficiency.Score:F2}", JobState.Validating);
                AddReplay(job, "sufficiency", "Sufficiency Check",
                    $"Score: {sufficiency.Score:F2}\nSufficient: {sufficiency.Sufficient}\n" +
                    $"Missing: {string.Join(", ", sufficiency.MissingTopics)}\n" +
                    $"Weak Claims: {sufficiency.WeakClaims.Count}");
            }

            // One remediation cycle if insufficient AND grounding isn't already high
            // (skip remediation when claims are well-cited — saves ~40-55s)
            if (!sufficiency.Sufficient && sufficiency.MissingTopics.Count > 0 
                && preGrounding < 0.7 && !token.IsCancellationRequested)
            {
                EmitProgress($"Draft insufficient — searching for {sufficiency.MissingTopics.Count} missing topic(s)...");
                var remediationQueries = sufficiency.MissingTopics
                    .Take(3)  // Cap to 3 queries — more is diminishing returns vs time cost
                    .Select(t => $"{prompt} {t}").ToList();
                var remediationUrls = await SearchMultiLaneAsync(remediationQueries, session.Pack, token);
                if (remediationUrls.Count > 0)
                {
                    var remSemaphore = new SemaphoreSlim(_settings.MaxConcurrentFetches);
                    var remResults = new System.Collections.Concurrent.ConcurrentBag<(string url, Snapshot? snapshot, Exception? error)>();
                    var remTasks = remediationUrls.Take(5).Select(async url =>
                    {
                        if (token.IsCancellationRequested) return;
                        await remSemaphore.WaitAsync(token);
                        try
                        {
                            var snap = await _snapshotService.CaptureUrlAsync(sessionId, url, ct: token);
                            remResults.Add((url, snap, null));
                        }
                        catch (Exception ex) { remResults.Add((url, null, ex)); }
                        finally { remSemaphore.Release(); }
                    }).ToList();
                    await Task.WhenAll(remTasks);

                    // Index remediation results in parallel
                    var remIndexTasks = new List<Task>();
                    foreach (var (url, snap, err) in remResults)
                    {
                        if (err != null || snap == null || snap.IsBlocked) continue;
                        job.AcquiredSourceIds.Add(snap.Id);
                        remIndexTasks.Add(_indexService.IndexSnapshotAsync(sessionId, snap, token));
                    }
                    if (remIndexTasks.Count > 0)
                        await Task.WhenAll(remIndexTasks);
                    db.SaveJob(job);

                    // Re-draft with enhanced evidence
                    EmitProgress("Re-drafting with additional evidence...");
                    evidenceResults = await _retrievalService.HybridSearchAsync(sessionId, prompt, 30, token);
                    var remDeduped = DeduplicateEvidenceBySource(evidenceResults, sourceUrlMap);
                    // Refresh source URL map with batch query
                    var remSourceIds = evidenceResults.Select(er => er.SourceId).Distinct();
                    var remSnapMap = db.GetSnapshotsByIds(remSourceIds);
                    foreach (var kv in remSnapMap)
                    {
                        if (!string.IsNullOrEmpty(kv.Value.Url))
                            sourceUrlMap[kv.Key] = kv.Value.Url;
                    }
                    evidenceContext = BuildEvidenceContext(remDeduped, citations, sourceUrlMap);
                    var remDraftPrompt = BuildSynthesisPrompt(prompt, subQContext, evidenceContext, citations.Count);
                    draftResponse = await GenerateSynthesisAsync(
                        sessionId, prompt, remDraftPrompt, remDeduped, citations, sourceUrlMap, job, token);
                    job.MostSupportedView = ExtractSection(draftResponse, "Most Supported View");
                    job.CredibleAlternatives = ExtractSection(draftResponse, "Credible Alternatives");
                    AddStep(db, job, "Remediated", "Re-drafted with additional evidence", JobState.Drafting);
                    AddReplay(job, "remediation", "Remediation Cycle", $"Added {remResults.Count(r => r.snapshot != null && !r.snapshot.IsBlocked)} sources and re-drafted");
                }
            }

            // 9) Validate - check for unsupported claims
            job.State = JobState.Validating;
            db.SaveJobState(job.Id, JobState.Validating);
            AddStep(db, job, "Validating", "Checking claims against evidence", JobState.Validating);
            EmitProgress("Validating claims against evidence...");

            var claims = ExtractClaims(draftResponse);

            // A4: Grounding Score
            job.GroundingScore = ComputeGroundingScore(claims);
            var citedCount = claims.Count(c => CitationRefRegex.IsMatch(c));
            AddStep(db, job, "GroundingScore", $"Grounding: {job.GroundingScore:P0} ({citedCount}/{claims.Count} claims cited)", JobState.Validating);
            AddReplay(job, "grounding", "Grounding Score",
                $"Score: {job.GroundingScore:P0}\nCited claims: {citedCount}\nTotal claims: {claims.Count}");
            EmitProgress($"Grounding: {job.GroundingScore:P0}");

            foreach (var claim in claims)
            {
                var cl = new ClaimLedger
                {
                    JobId = job.Id,
                    Claim = claim,
                    Support = claim.Contains("[") ? "cited" : "hypothesis",
                    Explanation = claim.Contains("[") ? "Backed by referenced citation" : "No direct citation found; labeled as hypothesis"
                };
                db.SaveClaim(cl);
            }

            // 10) Produce Reports
            job.State = JobState.Reporting;
            db.SaveJobState(job.Id, JobState.Reporting);
            EmitProgress("Generating reports...");

            // Generate proper LLM-based executive summary + key findings
            var execSummaryText = await GenerateExecutiveSummaryAsync(prompt, draftResponse, job, token);
            job.ExecutiveSummary = execSummaryText;

            // Full report
            var fullReportBuilder = new StringBuilder();
            fullReportBuilder.AppendLine($"# Research Report");
            fullReportBuilder.AppendLine();
            fullReportBuilder.AppendLine($"*Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC*");
            fullReportBuilder.AppendLine($"*Sources: {job.AcquiredSourceIds.Count} | Citations: {citations.Count} | Iterations: {job.CurrentIteration} | Grounding: {job.GroundingScore:P0}*");
            fullReportBuilder.AppendLine();
            fullReportBuilder.AppendLine("---");
            fullReportBuilder.AppendLine();
            fullReportBuilder.AppendLine(draftResponse);
            fullReportBuilder.AppendLine();
            fullReportBuilder.AppendLine("---");
            fullReportBuilder.AppendLine();
            fullReportBuilder.AppendLine("## Source Index");
            fullReportBuilder.AppendLine();
            foreach (var c in citations)
            {
                var url = sourceUrlMap.TryGetValue(c.SourceId, out var u) ? u : "(no URL)";
                fullReportBuilder.AppendLine($"- {c.Label} {url}");
                fullReportBuilder.AppendLine($"  > {c.Excerpt}");
                fullReportBuilder.AppendLine();
            }
            job.FullReport = fullReportBuilder.ToString();

            // Activity report
            var activityBuilder = new StringBuilder();
            activityBuilder.AppendLine($"# Activity Report");
            activityBuilder.AppendLine();
            foreach (var step in job.Steps)
            {
                activityBuilder.AppendLine($"- [{step.TimestampUtc:HH:mm:ss}] **{step.Action}**: {step.Detail}");
            }
            job.ActivityReport = activityBuilder.ToString();

            AddReplay(job, "report", "Reports Generated",
                $"Generated executive summary, full report, and activity report");

            // Save reports as files (parallel writes for speed)
            var session2 = _sessionManager.GetSession(sessionId)!;
            var exportsDir = Path.Combine(session2.WorkspacePath, "Exports");

            var shortTitle = ShortTitle(prompt);
            var execReport = new Report
            {
                SessionId = sessionId, JobId = job.Id, ReportType = "executive",
                Title = $"Executive Summary - {shortTitle}", Content = job.ExecutiveSummary,
                FilePath = Path.Combine(exportsDir, $"{job.Id}_executive.md")
            };
            var fullReport = new Report
            {
                SessionId = sessionId, JobId = job.Id, ReportType = "full",
                Title = $"Full Report - {shortTitle}", Content = job.FullReport,
                FilePath = Path.Combine(exportsDir, $"{job.Id}_full.md")
            };
            var actReport = new Report
            {
                SessionId = sessionId, JobId = job.Id, ReportType = "activity",
                Title = $"Activity Report - {shortTitle}", Content = job.ActivityReport,
                FilePath = Path.Combine(exportsDir, $"{job.Id}_activity.md")
            };
            var replayReport = new Report
            {
                SessionId = sessionId, JobId = job.Id, ReportType = "replay",
                Title = $"Replay Timeline - {shortTitle}",
                Content = JsonSerializer.Serialize(job.ReplayEntries, new JsonSerializerOptions { WriteIndented = true }),
                FilePath = Path.Combine(exportsDir, $"{job.Id}_replay.json")
            };

            // Write all 4 report files in parallel
            await Task.WhenAll(
                File.WriteAllTextAsync(execReport.FilePath, execReport.Content, token),
                File.WriteAllTextAsync(fullReport.FilePath, fullReport.Content, token),
                File.WriteAllTextAsync(actReport.FilePath, actReport.Content, token),
                File.WriteAllTextAsync(replayReport.FilePath, replayReport.Content, token)
            );

            db.SaveReport(execReport);
            db.SaveReport(fullReport);
            db.SaveReport(actReport);
            db.SaveReport(replayReport);

            // Update session with last report
            session2.LastReportSummary = job.ExecutiveSummary?.Length > 200
                ? job.ExecutiveSummary[..200] + "..."
                : job.ExecutiveSummary;
            session2.LastReportPath = fullReport.FilePath;
            _sessionManager.UpdateSession(session2);

            job.State = JobState.Completed;
            job.CompletedUtc = DateTime.UtcNow;
            AddStep(db, job, "Completed", "Job completed successfully", JobState.Completed);
            AddReplay(job, "complete", "Research Complete", "All reports generated and saved");
            db.SaveJob(job); // Single final save with all data
            EmitProgress("Research complete! All reports generated.");

            return job;
        }
        catch (OperationCanceledException)
        {
            // Check if this was a cancel vs pause
            var latestState = db.GetJob(job.Id)?.State;
            job.State = latestState == JobState.Cancelled ? JobState.Cancelled : JobState.Paused;
            db.SaveJob(job);
            AddStep(db, job, job.State.ToString(), $"Job {job.State.ToString().ToLower()}", job.State);
            EmitProgress($"Job {job.State.ToString().ToLower()}");
            return job;
        }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.ErrorMessage = ex.Message;
            db.SaveJob(job);
            AddStep(db, job, "Failed", ex.Message, JobState.Failed, false, ex.Message);
            EmitProgress($"Job failed: {ex.Message}");
            return job;
        }
        finally
        {
            _activeCts.TryRemove(job.Id, out _);
            // Release Chrome driver so the Chrome window closes after research
            _googleSearch.ReleaseDriver();
            _llmService.CodexActivityOccurred -= OnCodexActivity;
        }
    }

    /// <summary>
    /// Streamlined Codex path: skips orchestration calls (plan, decompose, coverage, deep search)
    /// and does a single synthesis call with Codex's native web search enabled.
    /// 
    /// Flow: minimal Chrome search (for evidence DB) → single Codex synthesis (web search ON) 
    /// → grounding → exec summary → reports.
    /// 
    /// Saves 5-8 Codex process invocations and 2-5 minutes wall clock compared to the full pipeline.
    /// Only used when StreamlinedCodexMode is on AND the active provider is Codex CLI OAuth.
    /// </summary>
    private async Task<ResearchJob> RunStreamlinedCodexPathAsync(
        string sessionId, SessionDb db, Session session, ResearchJob job, string prompt,
        int targetSources, List<SourceHealthEntry> sourceHealth,
        Action<string, double> emitProgress, CancellationToken ct)
    {
        emitProgress("Streamlined mode: Codex will research autonomously with web search", 0);
        AddStep(db, job, "Streamlined", "Using streamlined Codex path — single synthesis with native web search", JobState.Planning);
        AddReplay(job, "streamlined", "Streamlined Mode",
            "Skipping orchestration (plan/decompose/coverage). Codex will handle search + synthesis in one call.");

        // ── 1) Minimal Chrome search: extract keywords from prompt, run 2-3 quick searches ──
        // This populates the evidence DB so the Evidence tab has content, even though
        // Codex will do its own independent web research during synthesis.
        job.State = JobState.Searching;
        db.SaveJobState(job.Id, JobState.Searching);
        emitProgress("Running minimal search to seed evidence database...", 0);

        var minimalQueries = ExtractMinimalQueries(prompt, session.Pack);
        var searchUrls = await SearchMultiLaneAsync(minimalQueries, session.Pack, ct);
        var relevantUrls = ScoreAndFilterUrls(searchUrls, prompt, minimalQueries, _settings.SourceQualityRanking);

        emitProgress($"Found {relevantUrls.Count} relevant URLs from {searchUrls.Count} candidates", 0);
        AddReplay(job, "search", "Minimal Search",
            $"Seeded evidence DB with {minimalQueries.Count} queries → {relevantUrls.Count} relevant URLs");

        // ── 2) Acquire & snapshot top results ──
        if (relevantUrls.Count > 0)
        {
            job.State = JobState.Acquiring;
            db.SaveJobState(job.Id, JobState.Acquiring);

            var urlsToFetch = relevantUrls.Take(Math.Min(targetSources, 10)).ToList();
            var fetchSemaphore = new SemaphoreSlim(Math.Min(6, urlsToFetch.Count));
            var fetchResults = new ConcurrentBag<(string url, Snapshot? snapshot, Exception? error)>();

            var fetchTasks = urlsToFetch.Select(async url =>
            {
                if (ct.IsCancellationRequested) return;
                await fetchSemaphore.WaitAsync(ct);
                try
                {
                    var snap = await _snapshotService.CaptureUrlAsync(sessionId, url, ct: ct);
                    fetchResults.Add((url, snap, null));
                }
                catch (Exception ex) { fetchResults.Add((url, null, ex)); }
                finally { fetchSemaphore.Release(); }
            }).ToList();

            await Task.WhenAll(fetchTasks);

            // Index acquired snapshots
            var indexTasks = new List<Task>();
            foreach (var (url, snap, err) in fetchResults)
            {
                if (err != null || snap == null || snap.IsBlocked)
                {
                    sourceHealth.Add(new SourceHealthEntry
                    {
                        Url = url,
                        Status = err is TaskCanceledException ? SourceFetchStatus.Timeout : SourceFetchStatus.Error,
                        Reason = err?.Message ?? snap?.BlockReason ?? "Unknown"
                    });
                    continue;
                }

                if (!await IsContentRelevantAsync(snap, prompt, ct))
                {
                    sourceHealth.Add(new SourceHealthEntry
                    {
                        Url = url, Title = snap.Title ?? url,
                        Status = SourceFetchStatus.Error, Reason = "Content not relevant"
                    });
                    continue;
                }

                job.AcquiredSourceIds.Add(snap.Id);
                sourceHealth.Add(new SourceHealthEntry
                {
                    Url = url, Title = snap.Title ?? url,
                    Status = SourceFetchStatus.Success, HttpStatus = snap.HttpStatus
                });
                indexTasks.Add(_indexService.IndexSnapshotAsync(sessionId, snap, ct));
                AddStep(db, job, "Acquired", $"Captured: {snap.Title} ({url})", JobState.Acquiring);
            }

            if (indexTasks.Count > 0)
                await Task.WhenAll(indexTasks);
            db.SaveJob(job);
        }

        emitProgress($"Evidence DB seeded with {job.AcquiredSourceIds.Count} sources", 0);

        // ── 3) Build evidence context from indexed sources ──
        var allEvidence = await _retrievalService.HybridSearchAsync(sessionId, prompt, 30, ct);
        var jobSourceSet = new HashSet<string>(job.AcquiredSourceIds);
        var evidenceResults = allEvidence.Where(er => jobSourceSet.Contains(er.SourceId)).ToList();

        var sourceIds = evidenceResults.Select(er => er.SourceId).Distinct();
        var snapshotMap = db.GetSnapshotsByIds(sourceIds);
        var sourceUrlMap = snapshotMap
            .Where(kv => !string.IsNullOrEmpty(kv.Value.Url))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Url);

        var deduplicatedResults = DeduplicateEvidenceBySource(evidenceResults, sourceUrlMap);

        int citationNum = 1;
        var citations = new List<Citation>();
        foreach (var er in deduplicatedResults.Take(20))
        {
            var citation = new Citation
            {
                SessionId = sessionId, JobId = job.Id,
                Type = er.SourceType == "snapshot" ? CitationType.WebSnapshot :
                       er.SourceType == "capture" ? CitationType.OcrImage : CitationType.File,
                SourceId = er.SourceId, ChunkId = er.Chunk.Id,
                StartOffset = er.Chunk.StartOffset, EndOffset = er.Chunk.EndOffset,
                Excerpt = er.Chunk.Text.Length > 400 ? er.Chunk.Text[..400] + "..." : er.Chunk.Text,
                Label = $"[{citationNum++}]"
            };
            citations.Add(citation);
            db.SaveCitation(citation);
        }

        var evidenceContext = BuildEvidenceContext(deduplicatedResults, citations, sourceUrlMap);

        // ── 4) Single Codex synthesis with web search enabled ──
        job.State = JobState.Drafting;
        db.SaveJobState(job.Id, JobState.Drafting);
        emitProgress("Codex synthesizing with native web search (this is the main research call)...", 0);
        AddStep(db, job, "Drafting", "Streamlined synthesis: Codex researches + writes in one call", JobState.Drafting);

        // Choose synthesis approach — sectional or legacy single-call
        string draftResponse;

        if (_settings.SectionalReports)
        {
            // Section-by-section with web search enabled per-section
            draftResponse = await GenerateSectionalReportAsync(
                sessionId, prompt, new List<string>(), deduplicatedResults, citations,
                sourceUrlMap, evidenceContext, session.Pack, job, emitProgress,
                enableWebSearch: true, ct: ct);
        }
        else
        {
            // Legacy single-call synthesis
            var hasEvidence = citations.Count > 0;
            var evidenceSection = hasEvidence
                ? $"\n\nPRE-GATHERED EVIDENCE ({citations.Count} sources from our search):\n" +
                  "Use these as a starting point, but also search the web for additional authoritative sources.\n" +
                  "Cite these with [N] labels AND cite any additional sources you find with inline URLs.\n\n" +
                  evidenceContext
                : "\n\nNo pre-gathered evidence available. Search the web thoroughly for authoritative sources.\n";

            var streamlinedPrompt = $@"You are a research synthesis engine. Produce a comprehensive, well-structured research report.

RESEARCH QUESTION: {prompt}

INSTRUCTIONS:
- Write a thorough, in-depth research report (target 1500-2500 words).
- Search the web for authoritative primary sources (peer-reviewed papers, official documents, reputable databases).
- Every substantive claim MUST have a citation — either [N] referencing pre-gathered evidence or an inline URL.
- Include specific data points, statistics, comparisons, and concrete examples.
- If something is unsubstantiated, explicitly label it as hypothesis.

REQUIRED SECTIONS (use these exact headings):
## Key Findings - 5-8 major findings with citations
## Most Supported View - Primary evidence-weighted analysis (3-5 paragraphs)
## Detailed Analysis - Topic-by-topic deep dive with data and citations
## Credible Alternatives / Broader Views - Alternative interpretations
## Limitations - Evidence gaps, methodological caveats
## Sources - All cited sources: [N] Title — URL
{evidenceSection}";

            draftResponse = await GenerateSynthesisAsync(
                sessionId, prompt, streamlinedPrompt, deduplicatedResults, citations, sourceUrlMap, job, ct);
        }

        // Detect LLM failure
        var trimmedDraft = draftResponse.TrimStart();
        if (trimmedDraft.StartsWith("[LLM_UNAVAILABLE]") || trimmedDraft.StartsWith("[CLOUD_ERROR]"))
        {
            var errorDetail = draftResponse.Contains(']') ? draftResponse[(draftResponse.IndexOf(']') + 1)..].Trim() : draftResponse;
            emitProgress($"AI synthesis failed: {errorDetail}", 0);
            AddStep(db, job, "SynthesisFailed", draftResponse, JobState.Reporting, false, "AI provider unavailable");
            draftResponse = $"## Synthesis Failed\n\nThe AI provider did not return a response.\n\n" +
                $"**Error:** {errorDetail}\n\nTry running again or disable Streamlined mode.";
        }

        job.MostSupportedView = ExtractSection(draftResponse, "Most Supported View");
        job.CredibleAlternatives = ExtractSection(draftResponse, "Credible Alternatives");

        // ── 5) Grounding validation ──
        job.State = JobState.Validating;
        db.SaveJobState(job.Id, JobState.Validating);
        emitProgress("Validating claims against evidence...", 0);

        var claims = ExtractClaims(draftResponse);
        job.GroundingScore = ComputeGroundingScore(claims);
        var citedCount = claims.Count(c => CitationRefRegex.IsMatch(c));
        AddStep(db, job, "GroundingScore",
            $"Grounding: {job.GroundingScore:P0} ({citedCount}/{claims.Count} claims cited)", JobState.Validating);
        emitProgress($"Grounding: {job.GroundingScore:P0}", job.GroundingScore);

        foreach (var claim in claims)
        {
            db.SaveClaim(new ClaimLedger
            {
                JobId = job.Id, Claim = claim,
                Support = claim.Contains("[") ? "cited" : "hypothesis",
                Explanation = claim.Contains("[") ? "Backed by citation" : "No direct citation; labeled as hypothesis"
            });
        }

        // ── 6) Executive summary + reports ──
        job.State = JobState.Reporting;
        db.SaveJobState(job.Id, JobState.Reporting);
        emitProgress("Generating reports...", job.GroundingScore);

        var execSummaryText = await GenerateExecutiveSummaryAsync(prompt, draftResponse, job, ct);
        job.ExecutiveSummary = execSummaryText;

        var fullReportBuilder = new StringBuilder();
        fullReportBuilder.AppendLine("# Research Report");
        fullReportBuilder.AppendLine();
        fullReportBuilder.AppendLine($"*Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC — Streamlined Codex Mode*");
        fullReportBuilder.AppendLine($"*Sources: {job.AcquiredSourceIds.Count} (DB) + Codex web search | Citations: {citations.Count} | Grounding: {job.GroundingScore:P0}*");
        fullReportBuilder.AppendLine();
        fullReportBuilder.AppendLine("---");
        fullReportBuilder.AppendLine();
        fullReportBuilder.AppendLine(draftResponse);
        fullReportBuilder.AppendLine();
        fullReportBuilder.AppendLine("---");
        fullReportBuilder.AppendLine();
        fullReportBuilder.AppendLine("## Source Index");
        fullReportBuilder.AppendLine();
        foreach (var c in citations)
        {
            var url = sourceUrlMap.TryGetValue(c.SourceId, out var u) ? u : "(no URL)";
            fullReportBuilder.AppendLine($"- {c.Label} {url}");
            fullReportBuilder.AppendLine($"  > {c.Excerpt}");
            fullReportBuilder.AppendLine();
        }
        job.FullReport = fullReportBuilder.ToString();

        var activityBuilder = new StringBuilder();
        activityBuilder.AppendLine("# Activity Report (Streamlined Mode)");
        activityBuilder.AppendLine();
        foreach (var step in job.Steps)
            activityBuilder.AppendLine($"- [{step.TimestampUtc:HH:mm:ss}] **{step.Action}**: {step.Detail}");
        job.ActivityReport = activityBuilder.ToString();

        AddReplay(job, "report", "Reports Generated", "Generated executive summary, full report, and activity report");

        var session2 = _sessionManager.GetSession(sessionId)!;
        var exportsDir = Path.Combine(session2.WorkspacePath, "Exports");
        var shortTitle = ShortTitle(prompt);

        var execReport = new Report
        {
            SessionId = sessionId, JobId = job.Id, ReportType = "executive",
            Title = $"Executive Summary - {shortTitle}", Content = job.ExecutiveSummary,
            FilePath = Path.Combine(exportsDir, $"{job.Id}_executive.md")
        };
        var fullReport = new Report
        {
            SessionId = sessionId, JobId = job.Id, ReportType = "full",
            Title = $"Full Report - {shortTitle}", Content = job.FullReport,
            FilePath = Path.Combine(exportsDir, $"{job.Id}_full.md")
        };
        var actReport = new Report
        {
            SessionId = sessionId, JobId = job.Id, ReportType = "activity",
            Title = $"Activity Report - {shortTitle}", Content = job.ActivityReport,
            FilePath = Path.Combine(exportsDir, $"{job.Id}_activity.md")
        };
        var replayReport = new Report
        {
            SessionId = sessionId, JobId = job.Id, ReportType = "replay",
            Title = $"Replay Timeline - {shortTitle}",
            Content = JsonSerializer.Serialize(job.ReplayEntries, new JsonSerializerOptions { WriteIndented = true }),
            FilePath = Path.Combine(exportsDir, $"{job.Id}_replay.json")
        };

        await Task.WhenAll(
            File.WriteAllTextAsync(execReport.FilePath, execReport.Content, ct),
            File.WriteAllTextAsync(fullReport.FilePath, fullReport.Content, ct),
            File.WriteAllTextAsync(actReport.FilePath, actReport.Content, ct),
            File.WriteAllTextAsync(replayReport.FilePath, replayReport.Content, ct)
        );

        db.SaveReport(execReport);
        db.SaveReport(fullReport);
        db.SaveReport(actReport);
        db.SaveReport(replayReport);

        session2.LastReportSummary = job.ExecutiveSummary?.Length > 200
            ? job.ExecutiveSummary[..200] + "..." : job.ExecutiveSummary;
        session2.LastReportPath = fullReport.FilePath;
        _sessionManager.UpdateSession(session2);

        job.State = JobState.Completed;
        job.CompletedUtc = DateTime.UtcNow;
        AddStep(db, job, "Completed", "Streamlined research complete", JobState.Completed);
        AddReplay(job, "complete", "Research Complete", "Streamlined Codex research finished");
        db.SaveJob(job);
        emitProgress("Research complete! (Streamlined Codex mode — single synthesis call)", job.GroundingScore);

        return job;
    }

    /// <summary>
    /// Extracts 2-3 minimal search queries directly from the research prompt without an LLM call.
    /// Uses simple keyword extraction: takes the core noun-phrases and domain context.
    /// Good enough to seed the evidence DB while Codex handles the real research.
    /// </summary>
    internal static List<string> ExtractMinimalQueries(string prompt, DomainPack pack)
    {
        var queries = new List<string>();

        // Query 1: Use the first sentence/question as-is (up to 120 chars)
        var firstSentenceEnd = prompt.IndexOfAny(new[] { '.', '?', '!' });
        var firstQuery = firstSentenceEnd > 0 && firstSentenceEnd < 120
            ? prompt[..(firstSentenceEnd + 1)]
            : (prompt.Length > 120 ? prompt[..120] : prompt);
        queries.Add(firstQuery.Trim());

        // Query 2: Extract a "safety" or "comparison" sub-query if the prompt mentions it
        // Use the FIRST matching keyword so the window captures the keyword itself
        var lowerPrompt = prompt.ToLowerInvariant();
        var safetyKeywords = new[] { "safety", "hazard", "ppe" };
        var compareKeywords = new[] { "compare", "versus", " vs " };

        var safetyHit = safetyKeywords
            .Select(k => (keyword: k, idx: lowerPrompt.IndexOf(k)))
            .Where(x => x.idx >= 0)
            .OrderBy(x => x.idx)
            .FirstOrDefault();

        var compareHit = compareKeywords
            .Select(k => (keyword: k, idx: lowerPrompt.IndexOf(k)))
            .Where(x => x.idx >= 0)
            .OrderBy(x => x.idx)
            .FirstOrDefault();

        if (safetyHit.idx >= 0)
        {
            // Window: 10 chars before keyword start → 100 chars after keyword start
            var start = Math.Max(0, safetyHit.idx - 10);
            var end = Math.Min(prompt.Length, safetyHit.idx + 100);
            queries.Add(prompt[start..end].Trim());
        }
        else if (compareHit.idx >= 0)
        {
            var start = Math.Max(0, compareHit.idx - 20);
            var end = Math.Min(prompt.Length, compareHit.idx + 80);
            queries.Add(prompt[start..end].Trim());
        }

        // Query 3: Domain-scoped version of the core topic
        if (pack != DomainPack.GeneralResearch)
        {
            var domainLabel = pack.ToString().Replace("_", " ");
            var shortPrompt = prompt.Length > 80 ? prompt[..80] : prompt;
            queries.Add($"{domainLabel}: {shortPrompt}");
        }

        // Ensure at least 2 queries — add a variant of the prompt if needed
        if (queries.Distinct().Count() < 2)
        {
            // Use the second half of the prompt as a different search angle
            var halfLen = prompt.Length / 2;
            if (halfLen > 20)
                queries.Add(prompt[halfLen..Math.Min(prompt.Length, halfLen + 100)].Trim());
            else
                queries.Add($"research overview: {prompt}");
        }

        return queries.Distinct().Take(3).ToList();
    }

    public async Task<ResearchJob?> ResumeAsync(string sessionId, string jobId, CancellationToken ct = default)
    {
        var db = _sessionManager.GetSessionDb(sessionId);
        var job = db.GetJob(jobId);
        if (job == null) return null;

        // Mark resumed
        job.State = JobState.Searching;
        db.SaveJobState(job.Id, JobState.Searching);
        AddStep(db, job, "Resumed", "Job resumed from checkpoint", JobState.Searching);
        AddReplay(job, "resume", "Job Resumed", $"Resuming from iteration {job.CurrentIteration} with {job.AcquiredSourceIds.Count} sources acquired");

        ProgressChanged?.Invoke(this, new JobProgressEventArgs
        {
            JobId = job.Id, State = JobState.Searching,
            StepDescription = "Resuming research...",
            SourcesFound = job.AcquiredSourceIds.Count,
            TargetSources = job.TargetSourceCount,
            CurrentIteration = job.CurrentIteration,
            MaxIterations = job.MaxIterations
        });

        // Continue the EXISTING job instead of creating a new one
        return await ContinueJobAsync(sessionId, job, ct);
    }

    /// <summary>
    /// Continue an existing job from its last checkpoint without creating a duplicate.
    /// Re-enters the iterative search→acquire→evaluate loop at the saved iteration.
    /// </summary>
    private async Task<ResearchJob> ContinueJobAsync(string sessionId, ResearchJob job, CancellationToken ct)
    {
        var db = _sessionManager.GetSessionDb(sessionId);
        var session = _sessionManager.GetSession(sessionId)!;
        var sourceHealth = new List<SourceHealthEntry>();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeCts[job.Id] = cts;
        var token = cts.Token;
        var targetSources = job.TargetSourceCount;

        void EmitProgress(string desc, double coverage = 0)
        {
            var poolStats = _browserSearch.PoolStats;
            ProgressChanged?.Invoke(this, new JobProgressEventArgs
            {
                JobId = job.Id, State = job.State, StepDescription = desc,
                SourcesFound = job.AcquiredSourceIds.Count,
                SourcesFailed = sourceHealth.Count(s => s.Status != SourceFetchStatus.Success),
                SourcesBlocked = sourceHealth.Count(s => s.Status == SourceFetchStatus.Blocked),
                TargetSources = targetSources, CoverageScore = coverage,
                CurrentIteration = job.CurrentIteration, MaxIterations = job.MaxIterations,
                SourceHealth = sourceHealth.ToList(), LogMessage = desc,
                SubQuestionsTotal = job.SubQuestions.Count,
                SubQuestionsAnswered = job.SubQuestionCoverage.Count(kv => kv.Value == "answered"),
                GroundingScore = job.GroundingScore,
                BrowserPoolAvailable = poolStats.Total,
                BrowserPoolTotal = poolStats.Max,
            });
        }

        // Surface Codex CLI events in the activity log for resumed jobs
        void OnCodexActivity(IReadOnlyList<CodexEvent> events)
        {
            foreach (var e in events)
            {
                var label = e.Type switch
                {
                    "web_search" or "web_search_start" => "[Codex Search]",
                    "reasoning" => "[Codex Reasoning]",
                    "agent_message" => "[Codex Response]",
                    "command_execution" or "command_start" => "[Codex Command]",
                    "file_change" => "[Codex File]",
                    "turn.completed" => "[Codex Done]",
                    "error" => "[Codex Error]",
                    _ => "[Codex]"
                };
                EmitProgress($"{label} {e.Detail}");
            }
        }
        _llmService.CodexActivityOccurred += OnCodexActivity;

        try
        {
            // Regenerate queries if empty
            if (job.SearchQueries.Count == 0)
            {
                var planPrompt = $@"You are a research planner. Generate 5 diverse search queries for: {job.Prompt}
Domain: {session.Pack}
Format: 1. [query]  2. [query]  etc.";
                var planResponse = await _llmService.GenerateAsync(planPrompt, ct: token);
                job.SearchQueries = ExtractQueries(planResponse, job.Prompt);
            }

            // Resume from the saved iteration
            for (int iteration = job.CurrentIteration; iteration < job.MaxIterations && !token.IsCancellationRequested; iteration++)
            {
                job.CurrentIteration = iteration + 1;

                // Check for pause/cancel
                var latestJob = db.GetJob(job.Id);
                if (latestJob?.State == JobState.Paused) { job.State = JobState.Paused; db.SaveJob(job); EmitProgress("Job paused"); return job; }
                if (latestJob?.State == JobState.Cancelled) { job.State = JobState.Cancelled; db.SaveJob(job); EmitProgress("Job cancelled"); return job; }

                // Search
                job.State = JobState.Searching; db.SaveJob(job);
                EmitProgress($"Iteration {iteration + 1}/{job.MaxIterations}: Searching...");
                var searchUrls = await SearchMultiLaneAsync(job.SearchQueries, session.Pack, token);
                EmitProgress($"Found {searchUrls.Count} candidate URLs");

                // Acquire — parallel with auto-tuned concurrency (B3)
                job.State = JobState.Acquiring; db.SaveJob(job);
                var cRemaining = targetSources - job.AcquiredSourceIds.Count;
                var cUrlsToAcquire = searchUrls.Take(cRemaining * 4).ToList();
                var cFetchConcurrency = Math.Min(15, Math.Max(4, cUrlsToAcquire.Count / 2));
                var cAcquireSem = new SemaphoreSlim(cFetchConcurrency);
                var cAcquireResults = new ConcurrentBag<(string url, Snapshot? snapshot, Exception? error)>();

                var cAcquireTasks = cUrlsToAcquire.Select(async url =>
                {
                    if (token.IsCancellationRequested || job.AcquiredSourceIds.Count >= targetSources) return;
                    await cAcquireSem.WaitAsync(token);
                    try
                    {
                        EmitProgress($"Acquiring: {url}");
                        var snap = await _snapshotService.CaptureUrlAsync(sessionId, url, ct: token);
                        cAcquireResults.Add((url, snap, null));
                    }
                    catch (Exception ex) { cAcquireResults.Add((url, null, ex)); }
                    finally { cAcquireSem.Release(); }
                }).ToList();

                await Task.WhenAll(cAcquireTasks);

                foreach (var (url, snapshot, err) in cAcquireResults)
                {
                    if (job.AcquiredSourceIds.Count >= targetSources) break;
                    if (err != null || snapshot == null)
                    {
                        sourceHealth.Add(new SourceHealthEntry { Url = url, Status = err is TaskCanceledException ? SourceFetchStatus.Timeout : SourceFetchStatus.Error, Reason = err?.Message ?? "Unknown" });
                        continue;
                    }
                    if (!snapshot.IsBlocked)
                    {
                        job.AcquiredSourceIds.Add(snapshot.Id);
                        sourceHealth.Add(new SourceHealthEntry { Url = url, Title = snapshot.Title ?? url, Status = SourceFetchStatus.Success, HttpStatus = snapshot.HttpStatus });
                        job.State = JobState.Extracting;
                        await _indexService.IndexSnapshotAsync(sessionId, snapshot, token);
                        db.SaveJob(job);
                    }
                    else
                    {
                        sourceHealth.Add(new SourceHealthEntry { Url = url, Title = url, Status = SourceFetchStatus.Blocked, HttpStatus = snapshot.HttpStatus, Reason = snapshot.BlockReason });
                    }
                }

                EmitProgress($"Acquired {job.AcquiredSourceIds.Count}/{targetSources} sources");

                // Evaluate coverage (semantic via LLM)
                job.State = JobState.Evaluating; db.SaveJob(job);
                var results = await _retrievalService.HybridSearchAsync(sessionId, job.Prompt, ct: token);
                var coverage = await EvaluateCoverageWithLlmAsync(job.Prompt, job.SubQuestions, results, job, token);
                EmitProgress($"Coverage: {coverage.Score:P0}", coverage.Score);

                // Exit when quality is sufficient, OR when we've hit max sources AND have some coverage
                if (coverage.Score >= 0.7) break;
                if (job.AcquiredSourceIds.Count >= targetSources && coverage.Score >= 0.4) break;

                if (coverage.Gaps.Any())
                {
                    job.SearchQueries = coverage.Gaps.Select(g => CleanSearchQuery($"{job.Prompt} {g}")).ToList();
                    EmitProgress($"Refining queries based on {coverage.Gaps.Count} gap(s)");
                }
            }

            // Synthesis (same as RunAsync)
            EmitProgress("Building evidence pack...");
            var evidenceResults = await _retrievalService.HybridSearchAsync(sessionId, job.Prompt, 20, token);

            // Filter to only chunks from THIS job's acquired sources (prevent cross-job contamination)
            var jobSourceSet = new HashSet<string>(job.AcquiredSourceIds);
            evidenceResults = evidenceResults.Where(er => jobSourceSet.Contains(er.SourceId)).ToList();

            // Build source URL lookup: SourceId → URL (batch query, not N+1)
            var sourceIds2 = evidenceResults.Select(er => er.SourceId).Distinct();
            var snapshotMap2 = db.GetSnapshotsByIds(sourceIds2);
            var sourceUrlMap = snapshotMap2
                .Where(kv => !string.IsNullOrEmpty(kv.Value.Url))
                .ToDictionary(kv => kv.Key, kv => kv.Value.Url);

            int citationNum = 1;
            var citations = new List<Citation>();
            foreach (var er in evidenceResults.Take(15))
            {
                var citation = new Citation
                {
                    SessionId = sessionId, JobId = job.Id,
                    Type = er.SourceType == "snapshot" ? CitationType.WebSnapshot : er.SourceType == "capture" ? CitationType.OcrImage : CitationType.File,
                    SourceId = er.SourceId, ChunkId = er.Chunk.Id,
                    StartOffset = er.Chunk.StartOffset, EndOffset = er.Chunk.EndOffset,
                    Excerpt = er.Chunk.Text.Length > 200 ? er.Chunk.Text[..200] + "..." : er.Chunk.Text,
                    Label = $"[{citationNum++}]"
                };
                citations.Add(citation);
                db.SaveCitation(citation);
            }

            job.State = JobState.Drafting; db.SaveJob(job);
            EmitProgress("Synthesizing report...");

            var evidenceContext = BuildEvidenceContext(evidenceResults, citations, sourceUrlMap);
            var draftPrompt = $@"You are a research synthesis engine. Based on the evidence below, produce a report with these sections:
## Most Supported View
## Credible Alternatives / Broader Views
## Limitations
## Sources
(List every source cited above with its citation label, title if available, and full URL. Format: [N] Title — URL)

Research Question: {job.Prompt}

Evidence:
{evidenceContext}

Every claim must reference a citation label like [1], [2].
IMPORTANT: In the Sources section, EVERY source must include its full URL. Use the URLs provided alongside each evidence chunk.";

            var draftResponse = await GenerateSynthesisAsync(
                sessionId, job.Prompt, draftPrompt, evidenceResults, citations, sourceUrlMap, job, token);

            // Detect LLM failure on continue path
            var trimmedDraft = draftResponse.TrimStart();
            if (trimmedDraft.StartsWith("[LLM_UNAVAILABLE]") || trimmedDraft.StartsWith("[CLOUD_ERROR]"))
            {
                var errorDetail = draftResponse.Contains(']') ? draftResponse[(draftResponse.IndexOf(']') + 1)..].Trim() : draftResponse;
                EmitProgress($"AI synthesis failed: {errorDetail}");
                draftResponse = $"## Synthesis Failed\n\nThe AI provider did not return a response. " +
                    $"Try running the research again, or check your settings.\n\n" +
                    $"**Routing:** {_settings.Routing}\n**Provider:** {_settings.PaidProvider}\n\n" +
                    $"**Error:** {errorDetail}";
            }

            job.MostSupportedView = ExtractSection(draftResponse, "Most Supported View");
            job.CredibleAlternatives = ExtractSection(draftResponse, "Credible Alternatives");

            // A3: Sufficiency check on continue path
            if (job.SubQuestions.Count > 1)
            {
                var sufficiency = await VerifyAnswerSufficiencyAsync(job.Prompt, job.SubQuestions, draftResponse, token);
                EmitProgress($"Sufficiency: {sufficiency.Score:F2}, Sufficient: {sufficiency.Sufficient}");
            }

            // Validate + produce reports (same as RunAsync)
            job.State = JobState.Validating; db.SaveJob(job);
            EmitProgress("Validating claims...");
            var continueClaimList = ExtractClaims(draftResponse);

            // A4: Grounding score
            job.GroundingScore = ComputeGroundingScore(continueClaimList);
            EmitProgress($"Grounding: {job.GroundingScore:P0}");

            foreach (var claim in continueClaimList)
            {
                db.SaveClaim(new ClaimLedger
                {
                    JobId = job.Id, Claim = claim,
                    Support = claim.Contains("[") ? "cited" : "hypothesis",
                    Explanation = claim.Contains("[") ? "Backed by citation" : "No direct citation"
                });
            }

            job.State = JobState.Reporting; db.SaveJob(job);
            EmitProgress("Generating reports...");

            job.ExecutiveSummary = $"# Executive Summary\n\n**Sources:** {job.AcquiredSourceIds.Count}\n**Citations:** {citations.Count}\n\n" +
                (job.MostSupportedView ?? "See full report.");

            var fullReportBuilder = new StringBuilder();
            fullReportBuilder.AppendLine($"# Research Report");
            fullReportBuilder.AppendLine($"*Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC*");
            fullReportBuilder.AppendLine($"*Sources: {job.AcquiredSourceIds.Count} | Citations: {citations.Count}*\n");
            fullReportBuilder.AppendLine(draftResponse);
            fullReportBuilder.AppendLine("\n## Source Index\n");
            foreach (var c in citations)
            {
                var url = sourceUrlMap.TryGetValue(c.SourceId, out var u) ? u : "(no URL)";
                fullReportBuilder.AppendLine($"- {c.Label} <{url}>");
                fullReportBuilder.AppendLine($"  {c.Excerpt}");
                fullReportBuilder.AppendLine();
            }
            job.FullReport = fullReportBuilder.ToString();

            var activityBuilder = new StringBuilder();
            activityBuilder.AppendLine($"# Activity Report\n");
            foreach (var step in job.Steps) activityBuilder.AppendLine($"- [{step.TimestampUtc:HH:mm:ss}] **{step.Action}**: {step.Detail}");
            job.ActivityReport = activityBuilder.ToString();

            var session2 = _sessionManager.GetSession(sessionId)!;
            var exportsDir = Path.Combine(session2.WorkspacePath, "Exports");

            var shortTitle2 = ShortTitle(job.Prompt);
            foreach (var (type, title, content) in new[]
            {
                ("executive", $"Executive Summary - {shortTitle2}", job.ExecutiveSummary),
                ("full", $"Full Report - {shortTitle2}", job.FullReport),
                ("activity", $"Activity Report - {shortTitle2}", job.ActivityReport),
            })
            {
                var rpt = new Report
                {
                    SessionId = sessionId, JobId = job.Id, ReportType = type,
                    Title = title, Content = content!,
                    FilePath = Path.Combine(exportsDir, $"{job.Id}_{type}.md")
                };
                await File.WriteAllTextAsync(rpt.FilePath, rpt.Content, token);
                db.SaveReport(rpt);
            }

            session2.LastReportSummary = job.ExecutiveSummary?.Length > 200
                ? job.ExecutiveSummary[..200] + "..." : job.ExecutiveSummary;
            session2.LastReportPath = Path.Combine(exportsDir, $"{job.Id}_full.md");
            _sessionManager.UpdateSession(session2);

            job.State = JobState.Completed; job.CompletedUtc = DateTime.UtcNow;
            db.SaveJob(job);
            EmitProgress("Research complete! All reports generated.");
            return job;
        }
        catch (OperationCanceledException)
        {
            var latestState = db.GetJob(job.Id)?.State;
            job.State = latestState == JobState.Cancelled ? JobState.Cancelled : JobState.Paused;
            db.SaveJob(job); return job;
        }
        catch (Exception ex)
        {
            job.State = JobState.Failed; job.ErrorMessage = ex.Message;
            db.SaveJob(job); return job;
        }
        finally { _activeCts.TryRemove(job.Id, out _); _googleSearch.ReleaseDriver(); _llmService.CodexActivityOccurred -= OnCodexActivity; }
    }

    public void PauseJob(string sessionId, string jobId)
    {
        var db = _sessionManager.GetSessionDb(sessionId);
        var job = db.GetJob(jobId);
        if (job != null)
        {
            job.State = JobState.Paused;
            db.SaveJob(job);
            AddStep(db, job, "Paused", "Job paused by user", JobState.Paused);
        }
        // Also cancel the CTS so the running task stops
        if (_activeCts.TryGetValue(jobId, out var cts))
            cts.Cancel();
    }

    public void CancelJob(string sessionId, string jobId)
    {
        var db = _sessionManager.GetSessionDb(sessionId);
        var job = db.GetJob(jobId);
        if (job != null)
        {
            job.State = JobState.Cancelled;
            db.SaveJob(job);
            AddStep(db, job, "Cancelled", "Job cancelled by user", JobState.Cancelled);
        }
        if (_activeCts.TryGetValue(jobId, out var cts))
            cts.Cancel();
    }

    /// <summary>
    /// Continue/increment an existing research job. Searches for additional sources
    /// with a refined query (optionally provided), adds them to the evidence DB,
    /// and re-synthesizes the report incorporating all old + new evidence.
    /// This is the "incremental research" feature — build knowledge over time.
    /// </summary>
    public async Task<ResearchJob> ContinueResearchAsync(
        string sessionId, string? existingJobId, string? additionalPrompt = null,
        int additionalSources = 5, CancellationToken ct = default)
    {
        var db = _sessionManager.GetSessionDb(sessionId);
        var session = _sessionManager.GetSession(sessionId)!;

        // Load the existing job (if any) for context
        ResearchJob? previousJob = null;
        if (!string.IsNullOrEmpty(existingJobId))
            previousJob = db.GetJob(existingJobId);

        var basePrompt = previousJob?.Prompt ?? additionalPrompt ?? "";
        var refinedPrompt = !string.IsNullOrEmpty(additionalPrompt)
            ? $"{basePrompt}\n\nADDITIONAL FOCUS: {additionalPrompt}"
            : basePrompt;

        // Get already-acquired source URLs to avoid re-fetching
        var existingSnapshots = db.GetSnapshots();
        var existingUrls = new HashSet<string>(
            existingSnapshots.Select(s => s.CanonicalUrl ?? s.Url ?? "").Where(u => !string.IsNullOrEmpty(u)),
            StringComparer.OrdinalIgnoreCase);

        // Create a new continuation job
        var job = new ResearchJob
        {
            SessionId = sessionId,
            Type = JobType.Research,
            Prompt = refinedPrompt,
            TargetSourceCount = additionalSources,
            State = JobState.Pending,
            SearchLanes = GetSearchLanes(session.Pack)
        };

        db.SaveJob(job);
        AddStep(db, job, "ContinueResearch",
            $"Incremental research — building on {existingSnapshots.Count} existing sources, adding {additionalSources} more",
            JobState.Pending);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeCts[job.Id] = cts;
        var token = cts.Token;
        var sourceHealth = new List<SourceHealthEntry>();

        void EmitProgress(string desc, double cov = 0) =>
            ProgressChanged?.Invoke(this, new JobProgressEventArgs
            {
                JobId = job.Id, State = job.State, StepDescription = desc,
                SourcesFound = job.AcquiredSourceIds.Count,
                TargetSources = additionalSources
            });

        try
        {
            // ── Search for NEW sources ──
            job.State = JobState.Searching;
            db.SaveJobState(job.Id, JobState.Searching);
            EmitProgress("Searching for additional sources...");

            var queries = ExtractMinimalQueries(refinedPrompt, session.Pack);
            var searchUrls = await SearchMultiLaneAsync(queries, session.Pack, token);
            var relevant = ScoreAndFilterUrls(searchUrls, refinedPrompt, queries, _settings.SourceQualityRanking);

            // Filter out already-acquired URLs
            var newUrls = relevant.Where(u => !existingUrls.Contains(u)).Take(additionalSources * 2).ToList();
            EmitProgress($"Found {newUrls.Count} new URLs (filtered {relevant.Count - newUrls.Count} duplicates)");

            // ── Acquire new sources ──
            if (newUrls.Count > 0)
            {
                job.State = JobState.Acquiring;
                db.SaveJobState(job.Id, JobState.Acquiring);

                var fetchSemaphore = new SemaphoreSlim(Math.Min(6, newUrls.Count));
                var fetchResults = new ConcurrentBag<(string url, Snapshot? snap, Exception? err)>();
                var fetchTasks = newUrls.Take(additionalSources).Select(async url =>
                {
                    if (token.IsCancellationRequested) return;
                    await fetchSemaphore.WaitAsync(token);
                    try
                    {
                        var snap = await _snapshotService.CaptureUrlAsync(sessionId, url, ct: token);
                        fetchResults.Add((url, snap, null));
                    }
                    catch (Exception ex) { fetchResults.Add((url, null, ex)); }
                    finally { fetchSemaphore.Release(); }
                }).ToList();

                await Task.WhenAll(fetchTasks);

                var indexTasks = new List<Task>();
                foreach (var (url, snap, err) in fetchResults)
                {
                    if (err != null || snap == null || snap.IsBlocked) continue;
                    if (!await IsContentRelevantAsync(snap, refinedPrompt, token)) continue;

                    job.AcquiredSourceIds.Add(snap.Id);
                    indexTasks.Add(_indexService.IndexSnapshotAsync(sessionId, snap, token));
                    AddStep(db, job, "Acquired", $"New source: {snap.Title} ({url})", JobState.Acquiring);
                }

                if (indexTasks.Count > 0) await Task.WhenAll(indexTasks);
                db.SaveJob(job);
            }

            EmitProgress($"Added {job.AcquiredSourceIds.Count} new sources to evidence DB");

            // ── Re-synthesize with ALL evidence (old + new) ──
            job.State = JobState.Drafting;
            db.SaveJobState(job.Id, JobState.Drafting);
            EmitProgress("Re-synthesizing report with combined evidence...");

            // Gather ALL evidence from the session (old + newly added)
            var allEvidence = await _retrievalService.HybridSearchAsync(sessionId, refinedPrompt, 30, token);
            var allSnapshots = db.GetSnapshots();
            var sourceUrlMap = allSnapshots
                .Where(s => !string.IsNullOrEmpty(s.Url))
                .GroupBy(s => s.Id)
                .ToDictionary(g => g.Key, g => g.First().Url!);

            var deduped = DeduplicateEvidenceBySource(allEvidence, sourceUrlMap);

            int citNum = 1;
            var citations = new List<Citation>();
            foreach (var er in deduped.Take(25))
            {
                var cit = new Citation
                {
                    SessionId = sessionId, JobId = job.Id,
                    Type = er.SourceType == "snapshot" ? CitationType.WebSnapshot : CitationType.File,
                    SourceId = er.SourceId, ChunkId = er.Chunk.Id,
                    StartOffset = er.Chunk.StartOffset, EndOffset = er.Chunk.EndOffset,
                    Excerpt = er.Chunk.Text.Length > 400 ? er.Chunk.Text[..400] + "..." : er.Chunk.Text,
                    Label = $"[{citNum++}]"
                };
                citations.Add(cit);
                db.SaveCitation(cit);
            }

            var evidenceContext = BuildEvidenceContext(deduped, citations, sourceUrlMap);

            // Generate report using sectional approach
            string reportContent;
            if (_settings.SectionalReports)
            {
                reportContent = await GenerateSectionalReportAsync(
                    sessionId, refinedPrompt, new List<string>(), deduped, citations,
                    sourceUrlMap, evidenceContext, session.Pack, job, EmitProgress,
                    enableWebSearch: _settings.StreamlinedCodexMode && _llmService.IsCodexOAuthActive,
                    ct: token);
            }
            else
            {
                reportContent = await GenerateSynthesisAsync(
                    sessionId, refinedPrompt,
                    $"Research continuation report for: {refinedPrompt}\n\nEVIDENCE ({citations.Count} sources, including newly added):\n{evidenceContext}",
                    deduped, citations, sourceUrlMap, job, token);
            }

            job.MostSupportedView = ExtractSection(reportContent, "Most Supported View");
            job.CredibleAlternatives = ExtractSection(reportContent, "Credible Alternatives");

            // ── Grounding ──
            job.State = JobState.Validating;
            db.SaveJobState(job.Id, JobState.Validating);
            var claims = ExtractClaims(reportContent);
            job.GroundingScore = ComputeGroundingScore(claims);
            EmitProgress($"Grounding: {job.GroundingScore:P0}");

            // ── Report ──
            job.State = JobState.Reporting;
            db.SaveJobState(job.Id, JobState.Reporting);
            var execSummary = await GenerateExecutiveSummaryAsync(refinedPrompt, reportContent, job, token);
            job.ExecutiveSummary = execSummary;

            var fullReport = new StringBuilder();
            fullReport.AppendLine("# Research Report (Incremental)");
            fullReport.AppendLine($"*Built on {existingSnapshots.Count} existing + {job.AcquiredSourceIds.Count} new sources*\n");
            fullReport.AppendLine("---\n");
            fullReport.AppendLine(reportContent);

            job.FullReport = fullReport.ToString();
            session.LastReportSummary = execSummary;
            _sessionManager.UpdateSession(session);

            db.SaveReport(new Report
            {
                SessionId = sessionId, JobId = job.Id,
                ReportType = "incremental", Title = "Incremental Research Report",
                Content = job.FullReport, Format = "markdown",
                CreatedUtc = DateTime.UtcNow
            });

            job.State = JobState.Completed;
            job.CompletedUtc = DateTime.UtcNow;
            db.SaveJob(job);
            EmitProgress("Incremental research complete!", job.GroundingScore);
        }
        catch (OperationCanceledException)
        {
            job.State = JobState.Cancelled;
            db.SaveJob(job);
        }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.ErrorMessage = ex.Message;
            db.SaveJob(job);
        }
        finally
        {
            _activeCts.TryRemove(job.Id, out _);
        }

        return job;
    }

    // ──────────────────────────────────────────────────────────
    // URL & Content Relevance Filtering
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Pre-filter: score URLs by keyword overlap between URL path/domain and
    /// the research query + search terms. Removes obviously irrelevant URLs
    /// (e.g. "SQL Syntax" for an AI research query) before spending time capturing.
    /// </summary>
    internal static List<string> ScoreAndFilterUrls(List<string> urls, string prompt, List<string> searchQueries, bool useQualityRanking = false)
    {
        // Build keyword set from prompt + queries
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in TokenizeForRelevance(prompt))
            keywords.Add(word);
        foreach (var q in searchQueries)
            foreach (var word in TokenizeForRelevance(q))
                keywords.Add(word);

        // Remove very common words that don't help with relevance
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could",
            "should", "may", "might", "shall", "can", "need", "must", "what",
            "how", "why", "when", "where", "which", "who", "whom", "this", "that",
            "these", "those", "for", "from", "with", "about", "into", "through",
            "and", "but", "or", "not", "nor", "so", "yet", "both", "either",
            "each", "every", "all", "any", "few", "more", "most", "other",
            "some", "such", "than", "too", "very", "just", "also", "only",
            "of", "in", "on", "at", "to", "by", "as", "it", "its"
        };
        keywords.ExceptWith(stopWords);

        if (keywords.Count == 0)
            return urls; // Can't filter without keywords

        // Score each URL by keyword relevance + optional domain authority
        var scored = urls.Select(url =>
        {
            try
            {
                var uri = new Uri(url);
                // Score by matching keywords in host + path segments
                var urlText = $"{uri.Host} {uri.AbsolutePath.Replace('/', ' ').Replace('-', ' ').Replace('_', ' ')}";
                var urlWords = TokenizeForRelevance(urlText);
                int matchCount = urlWords.Count(w => keywords.Any(k =>
                    w.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                    k.Contains(w, StringComparison.OrdinalIgnoreCase)));
                double keywordScore = urlWords.Length > 0 ? (double)matchCount / Math.Max(3, keywords.Count) : 0;

                // Blend with domain authority when quality ranking is enabled
                double sc = useQualityRanking
                    ? keywordScore * 0.5 + SourceQualityScorer.ScoreUri(uri) * 0.5
                    : keywordScore;

                return (Url: url, Score: sc);
            }
            catch { return (Url: url, Score: 0.0); }
        })
        .OrderByDescending(x => x.Score)
        .ToList();

        // Keep URLs with score > 0 (at least one keyword match), plus up to 5 
        // unscored URLs as exploration candidates
        var relevant = scored.Where(x => x.Score > 0).Select(x => x.Url).ToList();
        var exploratory = scored.Where(x => x.Score == 0).Select(x => x.Url).Take(5).ToList();
        relevant.AddRange(exploratory);

        // If filtering removed too many, relax threshold
        if (relevant.Count < 10 && urls.Count >= 10)
            return urls.Take(Math.Max(relevant.Count, 15)).ToList();

        return relevant;
    }

    internal static string[] TokenizeForRelevance(string text)
    {
        return System.Text.RegularExpressions.Regex
            .Split(text.ToLowerInvariant(), @"[\s\-_/\.\,\;\:\!\?\(\)\[\]\""\'\+\=\&\#]+")
            .Where(w => w.Length >= 3)
            .ToArray();
    }

    /// <summary>
    /// Post-capture content relevance gate: checks if the captured page text
    /// is topically relevant to the research question using keyword overlap +
    /// embedding similarity when available.
    /// </summary>
    private async Task<bool> IsContentRelevantAsync(Snapshot snapshot, string prompt, CancellationToken ct)
    {
        try
        {
            // Read up to 800 chars of page text
            string? pageText = null;
            if (!string.IsNullOrEmpty(snapshot.TextPath) && File.Exists(snapshot.TextPath))
                pageText = await ReadFirstCharsAsync(snapshot.TextPath, 800, ct);

            if (string.IsNullOrEmpty(pageText) || pageText.Length < 50)
                return true; // Can't determine relevance — allow through

            // Quick keyword overlap check
            var promptKeywords = TokenizeForRelevance(prompt);
            var contentWords = TokenizeForRelevance(pageText);
            if (promptKeywords.Length == 0) return true;

            int matches = promptKeywords.Count(pk =>
                contentWords.Any(cw => cw.Contains(pk, StringComparison.OrdinalIgnoreCase)
                    || pk.Contains(cw, StringComparison.OrdinalIgnoreCase)));
            double keywordScore = (double)matches / promptKeywords.Length;

            // If strong keyword match, accept immediately
            if (keywordScore >= 0.25) return true;

            // If very low keyword match, try embedding similarity as a second check
            if (keywordScore < 0.1)
            {
                // Cache the prompt embedding — it's the same for every snapshot in a run
                if (_cachedPromptEmbedding == null || _cachedPromptText != prompt)
                {
                    _cachedPromptEmbedding = await _embeddingService.GetEmbeddingAsync(prompt, ct);
                    _cachedPromptText = prompt;
                }
                var contentEmb = await _embeddingService.GetEmbeddingAsync(pageText[..Math.Min(pageText.Length, 500)], ct);
                if (_cachedPromptEmbedding != null && contentEmb != null)
                {
                    var similarity = EmbeddingService.CosineSimilarity(_cachedPromptEmbedding, contentEmb);
                    return similarity >= 0.3f; // Moderate bar — reject only clearly unrelated
                }
            }

            // Between 0.1 and 0.25 keyword score with no embedding — marginal, allow through
            return true;
        }
        catch
        {
            return true; // On error, don't block
        }
    }

    private static async Task<string?> ReadFirstCharsAsync(string path, int chars, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[chars];
            int read = await reader.ReadBlockAsync(buffer.AsMemory(0, chars), ct);
            return read > 0 ? new string(buffer, 0, read) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Parallel multi-lane search: fires all Playwright engines concurrently per query,
    /// runs ALL queries in parallel (throttled by browser context pool), and adds Google as a supplementary lane.
    /// B2: Removed batch limit — pool gate in BrowserSearchService controls concurrency.
    /// </summary>
    private async Task<List<string>> SearchMultiLaneAsync(List<string> queries, DomainPack pack, CancellationToken ct)
    {
        var allUrls = new ConcurrentDictionary<string, byte>(); // dedup on insert
        var engineFailures = new ConcurrentDictionary<string, int>();
        var targetUrlCount = Math.Min(60, Math.Max(40, queries.Count * 8)); // B4: dynamic cap
        var timeRange = _settings.SearchTimeRange;

        // Fire ALL queries concurrently — the browser context pool limits actual parallelism
        var queryTasks = queries.Select(async query =>
        {
            if (ct.IsCancellationRequested || allUrls.Count >= targetUrlCount) return;

            // ── Fire ALL Playwright engines in parallel for this query ──
            var engineTasks = BrowserSearchService.SearchEngines
                .Where(e => !engineFailures.TryGetValue(e.Name, out var f) || f < 2)
                .Select(async engine =>
                {
                    if (allUrls.Count >= targetUrlCount) return; // B4: early exit
                    try
                    {
                        var resultUrls = await _browserSearch.SearchAsync(query, engine.Name, engine.UrlTemplate, timeRange, ct);
                        if (resultUrls.Count == 0)
                        {
                            engineFailures.AddOrUpdate(engine.Name, 1, (_, c) => c + 1);
                        }
                        else
                        {
                            foreach (var u in resultUrls) allUrls.TryAdd(u, 0);
                        }
                    }
                    catch
                    {
                        engineFailures.AddOrUpdate(engine.Name, 1, (_, c) => c + 1);
                    }
                })
                .ToList();

            await Task.WhenAll(engineTasks);

            // ── Google (serialized via its internal lock, but launched alongside Playwright) ──
            if (!ct.IsCancellationRequested
                && _googleSearch.SessionSearchCount < GoogleSearchService.MaxQueriesPerSession
                && (!engineFailures.TryGetValue("google", out var gf) || gf < 2))
            {
                try
                {
                    var googleUrls = await _googleSearch.SearchAsync(query, timeRange, ct);
                    if (googleUrls.Count == 0)
                        engineFailures.AddOrUpdate("google", 1, (_, c) => c + 1);
                    else
                        foreach (var u in googleUrls) allUrls.TryAdd(u, 0);
                }
                catch
                {
                    engineFailures.AddOrUpdate("google", 1, (_, c) => c + 1);
                }
            }
        }).ToList();

        await Task.WhenAll(queryTasks);

        // Reset Google session count for next research cycle
        _googleSearch.ResetSessionCount();

        // Deduplicate and normalize
        return allUrls.Keys
            .Select(NormalizeUrl)
            .Where(u => !string.IsNullOrEmpty(u))
            .Distinct()
            .Take(targetUrlCount)
            .ToList();
    }

    /// <summary>
    /// Extracts a short (3-4 word) title from a prompt for report naming.
    /// </summary>
    private static string ShortTitle(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return "Research";
        // Take the first meaningful words, skip filler
        var words = prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !_fillerWords.Contains(w.ToLowerInvariant()))
            .Take(4)
            .ToArray();
        if (words.Length == 0) return "Research";
        var title = string.Join(' ', words);
        // Cap at 40 chars
        return title.Length > 40 ? title[..40] : title;
    }

    private static readonly HashSet<string> _fillerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "how", "what", "does", "this", "that",
        "with", "from", "into", "about", "which", "their", "there", "have",
        "has", "been", "its", "can", "will", "would", "should", "could",
        "was", "were", "you", "your", "our"
    };

    private static string NormalizeUrl(string url)
    {
        // Strip tracking params
        try
        {
            var uri = new Uri(url);
            var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var cleanParams = new List<string>();
            foreach (string key in queryParams)
            {
                if (key != null && !key.StartsWith("utm_") && key != "fbclid" && key != "gclid" && key != "ref")
                    cleanParams.Add($"{key}={queryParams[key]}");
            }
            var cleanQuery = cleanParams.Count > 0 ? "?" + string.Join("&", cleanParams) : "";
            return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}{cleanQuery}";
        }
        catch { return url; }
    }

    private static List<string> GetSearchLanes(DomainPack pack) => pack switch
    {
        DomainPack.GeneralResearch => new() { "encyclopedic", "authoritative", "academic", "secondary", "contrarian" },
        DomainPack.HistoryPhilosophy => new() { "primary_sources", "academic", "historiography", "timeline" },
        DomainPack.Math => new() { "textbooks", "papers", "proof_verification", "computation" },
        DomainPack.MakerMaterials => new() { "standards", "academic", "maker", "safety" },
        DomainPack.ChemistrySafe => new() { "sds", "academic", "safety", "references" },
        DomainPack.ProgrammingResearchIP => new() { "official_docs", "standards", "benchmarks", "oss_concepts", "license" },
        _ => new() { "general", "academic", "contrarian" }
    };

    /// <summary>
    /// Multi-level search: agent analyzes acquired content to identify key concepts,
    /// terms, or cited works that warrant targeted drill-down searches.
    /// Returns up to 3 targeted queries for second-level deep search.
    /// </summary>
    private async Task<List<string>> GenerateDeepSearchQueriesAsync(
        string sessionId, string originalPrompt, DomainPack pack, CancellationToken ct)
    {
        try
        {
            // Get a sample of indexed content to analyze
            var topChunks = await _retrievalService.HybridSearchAsync(sessionId, originalPrompt, 8, ct);
            if (topChunks.Count < 2) return new List<string>();

            var contentSample = string.Join("\n---\n",
                topChunks.Take(6).Select(c => c.Chunk.Text.Length > 300 ? c.Chunk.Text[..300] : c.Chunk.Text));

            var deepSearchPrompt = $@"You are a research agent analyzing initial findings. Based on the content below from an initial search, identify 2-3 specific drill-down topics that would significantly deepen the research.

Original research question: {originalPrompt}
Domain: {pack}

Content from initial search:
{contentSample}

Generate 2-3 specific, targeted search queries for topics that:
- Were mentioned but not fully explored in the initial results
- Represent key cited works, authors, or studies worth investigating directly
- Cover alternative perspectives or contradicting evidence  
- Address technical details that would strengthen the analysis

Format your response as:
1. [specific drill-down query]
2. [specific drill-down query]
3. [specific drill-down query]

Be specific and targeted — these should find different content than the original queries.";

            var response = await _llmService.GenerateAsync(deepSearchPrompt, maxTokens: 250, ct: ct);
            return ExtractQueries(response, "").Take(3).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static readonly System.Text.RegularExpressions.Regex QueryLineRegex = new(
        @"^\s*\d+[\.\)\:\-]\s*(.+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static List<string> ExtractQueries(string planResponse, string fallbackPrompt)
    {
        var queries = new List<string>();
        var lines = planResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var match = QueryLineRegex.Match(line);
            if (match.Success)
            {
                var query = CleanSearchQuery(match.Groups[1].Value.Trim().Trim('"'));
                if (!string.IsNullOrWhiteSpace(query) && query.Length > 5)
                    queries.Add(query);
            }
        }

        if (queries.Count == 0)
        {
            queries.Add($"{fallbackPrompt} authoritative sources");
            queries.Add($"{fallbackPrompt} research findings");
            queries.Add($"{fallbackPrompt} analysis comparison");
            queries.Add($"{fallbackPrompt} recent developments");
            queries.Add($"{fallbackPrompt} expert review");
        }

        return queries;
    }

    /// <summary>
    /// Cleans LLM-generated search queries: strips boolean operators, excessive quotes,
    /// and academic formatting that confuse consumer search engines.
    /// </summary>
    internal static string CleanSearchQuery(string query)
    {
        // Strip boolean operators that confuse consumer search engines
        query = query.Replace(" AND ", " ", StringComparison.OrdinalIgnoreCase);
        query = query.Replace(" OR ", " ", StringComparison.OrdinalIgnoreCase);
        query = query.Replace(" NOT ", " ", StringComparison.OrdinalIgnoreCase);
        // Remove parenthetical grouping
        query = query.Replace("(", "").Replace(")", "");
        // Remove excessive double-quotes (keep single pairs if present)
        var quoteCount = query.Count(c => c == '"');
        if (quoteCount > 2)
            query = query.Replace("\"", "");
        // Trim and collapse whitespace
        query = System.Text.RegularExpressions.Regex.Replace(query.Trim(), @"\s+", " ");
        // Cap query length — overly long queries return poor results
        if (query.Length > 120)
            query = query[..120];
        return query;
    }

    /// <summary>
    /// Generate synthesis output, using tool calling if enabled and a cloud provider is active.
    /// Falls back to standard GenerateAsync if tool calling is off or Ollama-only.
    /// </summary>
    private async Task<string> GenerateSynthesisAsync(
        string sessionId, string prompt, string draftPrompt,
        List<RetrievalResult> evidenceResults, List<Citation> citations,
        Dictionary<string, string> sourceUrlMap, ResearchJob job,
        CancellationToken ct)
    {
        if (!_settings.EnableToolCalling)
            return await _llmService.GenerateAsync(draftPrompt, ct: ct);

        var toolExecutor = CreateToolExecutor(sessionId, prompt, evidenceResults, citations, sourceUrlMap, job, ct);

        return await _llmService.GenerateWithToolsAsync(
            draftPrompt,
            "You are a research synthesis engine with access to tools. " +
            "Use tools to search for additional evidence, verify claims, or read full sources when needed. " +
            "Produce a comprehensive, well-cited report.",
            ResearchTools.All,
            toolExecutor,
            ct);
    }

    /// <summary>
    /// Creates a tool execution callback for the LLM tool-calling loop.
    /// Handles: search_evidence, search_web, get_source, verify_claim.
    /// </summary>
    private Func<ToolCall, Task<string>> CreateToolExecutor(
        string sessionId, string prompt,
        List<RetrievalResult> evidenceResults, List<Citation> citations,
        Dictionary<string, string> sourceUrlMap, ResearchJob job,
        CancellationToken ct)
    {
        var db = _sessionManager.GetSessionDb(sessionId);
        var session = _sessionManager.GetSession(sessionId)!;

        return async (ToolCall tc) =>
        {
            var args = tc.Function.ParseArgs();
            var toolName = tc.Function.Name;

            try
            {
                switch (toolName)
                {
                    case "search_evidence":
                    {
                        var query = args.GetValueOrDefault("query", prompt);
                        var results = await _retrievalService.HybridSearchAsync(sessionId, query, 8, ct);
                        if (results.Count == 0) return "No relevant evidence found for this query.";

                        var sb = new StringBuilder();
                        foreach (var r in results)
                        {
                            var url = sourceUrlMap.TryGetValue(r.SourceId, out var u) ? u : r.SourceId;
                            sb.AppendLine($"[Score: {r.Score:F2}] (Source: {url})");
                            sb.AppendLine(r.Chunk.Text.Length > 600 ? r.Chunk.Text[..600] + "..." : r.Chunk.Text);
                            sb.AppendLine();
                        }
                        return sb.ToString();
                    }

                    case "search_web":
                    {
                        var query = args.GetValueOrDefault("query", prompt);
                        var urls = await SearchMultiLaneAsync(new List<string> { query }, session.Pack, ct);
                        if (urls.Count == 0) return "No web results found for this query.";

                        // Fetch and index the first 3 results
                        var fetchedSources = new List<string>();
                        foreach (var url in urls.Take(3))
                        {
                            if (ct.IsCancellationRequested) break;
                            try
                            {
                                var snap = await _snapshotService.CaptureUrlAsync(sessionId, url, ct: ct);
                                if (snap != null && !snap.IsBlocked)
                                {
                                    job.AcquiredSourceIds.Add(snap.Id);
                                    await _indexService.IndexSnapshotAsync(sessionId, snap, ct);
                                    sourceUrlMap[snap.Id] = url;
                                    fetchedSources.Add($"- {snap.Title ?? url} ({url})");
                                }
                            }
                            catch { /* skip failed fetch */ }
                        }

                        if (fetchedSources.Count == 0) return "Found URLs but failed to fetch any content.";

                        // Now search the newly indexed content
                        var newResults = await _retrievalService.HybridSearchAsync(sessionId, query, 5, ct);
                        var sb = new StringBuilder();
                        sb.AppendLine($"Fetched {fetchedSources.Count} new source(s):");
                        foreach (var s in fetchedSources) sb.AppendLine(s);
                        sb.AppendLine();
                        sb.AppendLine("Relevant extracts from new sources:");
                        foreach (var r in newResults.Take(5))
                        {
                            sb.AppendLine(r.Chunk.Text.Length > 400 ? r.Chunk.Text[..400] + "..." : r.Chunk.Text);
                            sb.AppendLine();
                        }
                        return sb.ToString();
                    }

                    case "get_source":
                    {
                        var label = args.GetValueOrDefault("citation_label", "[1]");
                        // Parse the number from the label
                        var numStr = new string(label.Where(char.IsDigit).ToArray());
                        if (!int.TryParse(numStr, out var num) || num < 1 || num > evidenceResults.Count)
                            return $"Citation {label} not found. Available: [1] through [{evidenceResults.Count}].";

                        var er = evidenceResults[num - 1];
                        var snapshot = db.GetSnapshot(er.SourceId);
                        if (snapshot == null) return $"Source for {label} not found in database.";

                        var sb = new StringBuilder();
                        sb.AppendLine($"Full source {label}: {snapshot.Title ?? "Untitled"}");
                        sb.AppendLine($"URL: {snapshot.Url ?? "N/A"}");
                        sb.AppendLine();

                        // Read content from filesystem (text file preferred, then HTML)
                        string content = "No text content available.";
                        if (!string.IsNullOrEmpty(snapshot.TextPath) && File.Exists(snapshot.TextPath))
                            content = await File.ReadAllTextAsync(snapshot.TextPath, ct);
                        else if (!string.IsNullOrEmpty(snapshot.HtmlPath) && File.Exists(snapshot.HtmlPath))
                            content = await File.ReadAllTextAsync(snapshot.HtmlPath, ct);

                        sb.AppendLine(content.Length > 3000 ? content[..3000] + "...[truncated]" : content);
                        return sb.ToString();
                    }

                    case "verify_claim":
                    {
                        var claim = args.GetValueOrDefault("claim", "");
                        if (string.IsNullOrWhiteSpace(claim)) return "No claim provided to verify.";

                        var results = await _retrievalService.HybridSearchAsync(sessionId, claim, 5, ct);
                        if (results.Count == 0) return "UNVERIFIED: No evidence found supporting or refuting this claim.";

                        var topScore = results[0].Score;
                        var verdict = topScore > 0.1 ? "SUPPORTED" : "WEAKLY SUPPORTED";

                        var sb = new StringBuilder();
                        sb.AppendLine($"Claim: \"{claim}\"");
                        sb.AppendLine($"Verdict: {verdict} (top relevance score: {topScore:F2})");
                        sb.AppendLine($"Supporting evidence ({results.Count} matches):");
                        foreach (var r in results.Take(3))
                        {
                            var url = sourceUrlMap.TryGetValue(r.SourceId, out var u) ? u : r.SourceId;
                            sb.AppendLine($"  [{r.Score:F2}] {r.Chunk.Text[..Math.Min(200, r.Chunk.Text.Length)]}... (Source: {url})");
                        }
                        return sb.ToString();
                    }

                    default:
                        return $"Unknown tool: {toolName}";
                }
            }
            catch (Exception ex)
            {
                return $"Tool '{toolName}' failed: {ex.Message}";
            }
        };
    }

    // ──────────────────────────────────────────────────────────
    // Synthesis Prompt + Multi-Pass + Executive Summary
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a comprehensive synthesis prompt with depth guidance, sub-question coverage,
    /// and explicit structure for producing a thorough research report.
    /// </summary>
    private static string BuildSynthesisPrompt(string prompt, string subQContext, string evidenceContext, int citationCount)
    {
        return $@"You are a research synthesis engine producing a comprehensive, well-structured report.

RESEARCH QUESTION: {prompt}
{subQContext}

INSTRUCTIONS:
- Write a thorough, in-depth research report (target 1500-2500 words).
- Address each sub-question explicitly within the appropriate section.
- Every substantive claim MUST reference a citation label [1], [2], etc.
- Organize findings by topic/sub-question, NOT by source.
- Include specific data points, statistics, comparisons, and concrete examples.
- If something is unsubstantiated, explicitly label it as hypothesis.

REQUIRED SECTIONS (use these exact headings):

## Key Findings
A bulleted list of 5-8 major findings, each with inline citation(s). Be specific and concrete.

## Most Supported View
The primary, evidence-weighted analysis. This should be 3-5 paragraphs covering the major themes. Go in depth — explain WHY the evidence supports this view, not just WHAT the evidence says.

## Detailed Analysis
Topic-by-topic deep dive organized around the sub-questions. For each topic:
- State the finding with data
- Cite the specific evidence
- Compare sources where they agree or disagree
- Note the strength of the evidence

## Credible Alternatives / Broader Views
Alternative interpretations with citations. Explain why the most-supported view is favored over these alternatives.

## Limitations
What would change the conclusion, evidence gaps, methodological caveats, areas needing further research.

## Sources
List every cited source with format: [N] Title — URL
Use the URLs provided alongside each evidence chunk.

EVIDENCE ({citationCount} sources):
{evidenceContext}";
    }

    /// <summary>
    /// Generates a report section-by-section from a template. Each section gets its own
    /// targeted evidence retrieval and focused LLM prompt, producing longer and more detailed
    /// reports than a single monolithic call — especially from local models.
    /// </summary>
    private async Task<string> GenerateSectionalReportAsync(
        string sessionId, string prompt, List<string> subQuestions,
        List<RetrievalResult> allResults, List<Citation> citations,
        Dictionary<string, string> sourceUrlMap, string fullEvidenceContext,
        DomainPack pack, ResearchJob job, Action<string, double> emitProgress,
        bool enableWebSearch, CancellationToken ct)
    {
        var template = ReportTemplateService.GetResearchTemplate(pack);
        var report = new StringBuilder();
        report.AppendLine($"# Research Report: {ShortTitle(prompt)}\n");

        var subQContext = subQuestions.Count > 1
            ? string.Join("\n", subQuestions.Select((q, i) => $"{i + 1}. {q}"))
            : "";

        // ── Partition sections into parallel-safe vs sequential ──
        // Sections like "Limitations" need the accumulated report text.
        // "Sources" we generate locally from citation data (no LLM call needed).
        // Everything else is independent and can run in parallel.
        var parallelSections = new List<TemplateSection>();
        TemplateSection? limitationsSection = null;
        TemplateSection? sourcesSection = null;

        foreach (var section in template.Sections)
        {
            if (section.Heading == "Sources")
                sourcesSection = section;
            else if (section.Heading == "Limitations")
                limitationsSection = section;
            else
                parallelSections.Add(section);
        }

        // ── Phase 1: Run independent sections in parallel ──
        var totalSections = template.Sections.Count;
        emitProgress($"Writing {parallelSections.Count} sections in parallel...", 0);

        var sectionResults = new ConcurrentDictionary<string, string>();
        var parallelTasks = parallelSections.Select(async section =>
        {
            if (ct.IsCancellationRequested) return;

            var sectionEvidence = await GetTargetedEvidenceForSectionAsync(
                sessionId, section.Heading, section.Instruction, prompt,
                allResults, citations, sourceUrlMap, ct);

            var webSearchNote = enableWebSearch
                ? "\nAlso search the web for additional authoritative sources to supplement the evidence provided.\n"
                : "";

            var sectionPrompt = $@"You are writing the ""{section.Heading}"" section of a research report.

RESEARCH QUESTION: {prompt}
{(subQContext.Length > 0 ? $"\nSUB-QUESTIONS:\n{subQContext}\n" : "")}

SECTION INSTRUCTIONS:
{section.Instruction}
{webSearchNote}
Write ONLY the content for this section. Do NOT include the heading (it will be added automatically).
Every substantive claim MUST reference a citation label [N].
Target length: approximately {section.TargetTokens / 3} to {section.TargetTokens / 2} words.

EVIDENCE:
{sectionEvidence}";

            string sectionContent;
            if (enableWebSearch && _settings.EnableToolCalling)
            {
                var toolExecutor = CreateToolExecutor(sessionId, prompt, allResults, citations, sourceUrlMap, job, ct);
                sectionContent = await _llmService.GenerateWithToolsAsync(
                    sectionPrompt,
                    $"Write the {section.Heading} section of a research report.",
                    ResearchTools.All, toolExecutor, ct);
            }
            else
            {
                sectionContent = await _llmService.GenerateAsync(sectionPrompt, maxTokens: section.TargetTokens, ct: ct);
            }

            sectionContent = StripSectionHeading(sectionContent, section.Heading);
            sectionResults[section.Heading] = sectionContent;
            emitProgress($"Completed: {section.Heading}", 0);
        }).ToList();

        await Task.WhenAll(parallelTasks);

        // Assemble parallel sections in template order
        foreach (var section in parallelSections)
        {
            if (sectionResults.TryGetValue(section.Heading, out var content))
            {
                report.AppendLine($"## {section.Heading}\n");
                report.AppendLine(content);
                report.AppendLine();
            }
        }

        // ── Phase 2: Limitations (needs accumulated report for accurate assessment) ──
        if (limitationsSection != null && !ct.IsCancellationRequested)
        {
            emitProgress($"Writing section: {limitationsSection.Heading}...", 0);

            var limEvidence = await GetTargetedEvidenceForSectionAsync(
                sessionId, limitationsSection.Heading, limitationsSection.Instruction, prompt,
                allResults, citations, sourceUrlMap, ct);

            var limPrompt = $@"You are writing the ""Limitations"" section of a research report.

RESEARCH QUESTION: {prompt}

SECTION INSTRUCTIONS:
{limitationsSection.Instruction}
{(enableWebSearch ? "\nAlso search the web for additional authoritative sources to supplement the evidence provided.\n" : "")}
Write ONLY the content for this section. Do NOT include the heading (it will be added automatically).
Every substantive claim MUST reference a citation label [N].
Target length: approximately {limitationsSection.TargetTokens / 3} to {limitationsSection.TargetTokens / 2} words.

REPORT WRITTEN SO FAR (assess the evidence actually cited here, not just pre-gathered DB):
{report}

EVIDENCE:
{limEvidence}";

            string limContent;
            if (enableWebSearch && _settings.EnableToolCalling)
            {
                var toolExecutor = CreateToolExecutor(sessionId, prompt, allResults, citations, sourceUrlMap, job, ct);
                limContent = await _llmService.GenerateWithToolsAsync(
                    limPrompt, "Write the Limitations section of a research report.",
                    ResearchTools.All, toolExecutor, ct);
            }
            else
            {
                limContent = await _llmService.GenerateAsync(limPrompt, maxTokens: limitationsSection.TargetTokens, ct: ct);
            }

            limContent = StripSectionHeading(limContent, "Limitations");
            report.AppendLine("## Limitations\n");
            report.AppendLine(limContent);
            report.AppendLine();
            emitProgress("Completed: Limitations", 0);
        }

        // ── Phase 3: Sources — generated locally from citation data (no LLM call) ──
        if (sourcesSection != null)
        {
            emitProgress("Compiling source bibliography...", 0);
            report.AppendLine("## Sources\n");
            report.AppendLine(BuildSourceBibliography(citations, sourceUrlMap));
            report.AppendLine();
        }

        return report.ToString();
    }

    /// <summary>
    /// Builds a formatted source bibliography from citation data.
    /// Eliminates the need for an LLM call to produce the Sources section.
    /// </summary>
    private static string BuildSourceBibliography(List<Citation> citations, Dictionary<string, string> sourceUrlMap)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int num = 1;

        foreach (var c in citations)
        {
            var url = sourceUrlMap.TryGetValue(c.SourceId, out var u) ? u : "";
            var key = !string.IsNullOrEmpty(url) ? url : c.SourceId;
            if (!seen.Add(key)) continue;

            var excerpt = c.Excerpt?.Length > 80 ? c.Excerpt[..80].Trim() + "..." : c.Excerpt ?? "";
            var title = !string.IsNullOrEmpty(excerpt) ? excerpt : $"Source {c.SourceId}";

            if (!string.IsNullOrEmpty(url))
                sb.AppendLine($"[{num}] {title} — {url}");
            else
                sb.AppendLine($"[{num}] {title}");
            num++;
        }

        if (num == 1)
            sb.AppendLine("*Sources were cited inline via Codex web search. See citation URLs in the report text above.*");

        return sb.ToString();
    }

    /// <summary>Strip heading the LLM may have included despite instructions.</summary>
    private static string StripSectionHeading(string content, string heading)
    {
        content = content.Trim();
        if (content.StartsWith($"## {heading}", StringComparison.OrdinalIgnoreCase))
            content = content[(content.IndexOf('\n') + 1)..].Trim();
        if (content.StartsWith($"# {heading}", StringComparison.OrdinalIgnoreCase))
            content = content[(content.IndexOf('\n') + 1)..].Trim();
        return content;
    }

    /// <summary>
    /// Gets evidence targeted to a specific report section by searching with the section
    /// heading + instruction as context alongside the original prompt.
    /// </summary>
    private async Task<string> GetTargetedEvidenceForSectionAsync(
        string sessionId, string sectionHeading, string sectionInstruction, string prompt,
        List<RetrievalResult> allResults, List<Citation> citations,
        Dictionary<string, string> sourceUrlMap, CancellationToken ct)
    {
        // Search for evidence relevant to this specific section
        var sectionQuery = $"{prompt} {sectionHeading} {sectionInstruction}";
        var sectionResults = await _retrievalService.HybridSearchAsync(sessionId, sectionQuery, 8, ct);

        // If targeted search yields too few results, fall back to the full evidence
        if (sectionResults.Count < 3)
            return BuildEvidenceContext(allResults.Take(15).ToList(), citations.Take(15).ToList(), sourceUrlMap);

        // Map section results to their citation labels
        var citationMap = citations.ToDictionary(c => c.ChunkId, c => c);
        var sectionCitations = new List<Citation>();
        foreach (var sr in sectionResults)
        {
            if (citationMap.TryGetValue(sr.Chunk.Id, out var cit))
                sectionCitations.Add(cit);
            else
                sectionCitations.Add(new Citation { Label = $"[{sectionCitations.Count + 1}]", Excerpt = sr.Chunk.Text });
        }

        return BuildEvidenceContext(sectionResults, sectionCitations, sourceUrlMap);
    }

    /// <summary>
    /// Multi-pass synthesis for local models: generates findings for batched sub-questions
    /// in focused, manageable calls, then merges into a cohesive report.
    /// Small models (8B) produce much better output with narrow, focused tasks.
    /// Batches 2-3 sub-questions per pass (max 3 passes) to balance quality vs speed.
    /// </summary>
    private async Task<string> MultiPassSynthesisAsync(
        string sessionId, string prompt, List<string> subQuestions,
        List<RetrievalResult> results, List<Citation> citations,
        Dictionary<string, string> sourceUrlMap, string evidenceContext,
        ResearchJob job, CancellationToken ct)
    {
        // Batch sub-questions: 2-3 per pass, max 3 passes (handles up to 9 sub-Qs)
        var sqToProcess = subQuestions.Take(6).ToList(); // Cap at 6 sub-Qs
        var batchSize = sqToProcess.Count <= 3 ? sqToProcess.Count : (int)Math.Ceiling(sqToProcess.Count / 3.0);
        var batches = new List<List<string>>();
        for (int i = 0; i < sqToProcess.Count; i += batchSize)
            batches.Add(sqToProcess.Skip(i).Take(batchSize).ToList());

        // Pass 1: Generate focused findings per batch of sub-questions
        var subFindings = new List<string>();
        foreach (var batch in batches)
        {
            if (ct.IsCancellationRequested) break;

            var questionsBlock = string.Join("\n", batch.Select((q, i) => $"{i + 1}. {q}"));

            var focusedPrompt = $@"Based on the evidence below, write a focused analysis answering these questions. Include inline citation references [1], [2] etc. Be detailed and specific. Write 2-3 paragraphs per question.

Questions:
{questionsBlock}

Evidence:
{evidenceContext}

Write ONLY the analysis, nothing else.";

            var finding = await _llmService.GenerateAsync(focusedPrompt, maxTokens: 800, ct: ct);
            if (!string.IsNullOrWhiteSpace(finding))
                subFindings.Add(finding.Trim());
        }

        // Pass 2: Merge findings into a cohesive report with proper structure
        var mergedFindings = string.Join("\n\n---\n\n", subFindings);

        var mergePrompt = $@"You are a research editor. Combine these per-question findings into a single well-structured research report.

Research Question: {prompt}

Per-Question Findings:
{mergedFindings}

Create a report with these sections:

## Key Findings
5-8 bullet points summarizing the major findings with citation references [N].

## Most Supported View
2-3 paragraphs with the main evidence-weighted conclusions.

## Detailed Analysis
Reorganize the per-question findings into a coherent narrative organized by theme/topic.

## Credible Alternatives / Broader Views
Alternative viewpoints or caveats found in the research.

## Limitations
Evidence gaps, methodological caveats, areas for further research.

## Sources
List cited sources as: [N] Title — URL
Available URLs:
{string.Join("\n", citations.Select(c => $"{c.Label} {(sourceUrlMap.TryGetValue(c.SourceId, out var u) ? u : c.SourceId)}"))}

Keep all [N] citation references intact. Do not invent new citations.";

        return await _llmService.GenerateAsync(mergePrompt, maxTokens: 3000, ct: ct);
    }

    /// <summary>
    /// Generate a proper LLM-based executive summary from the completed report.
    /// Includes stats header + concise analytical summary + key findings bullets.
    /// </summary>
    private async Task<string> GenerateExecutiveSummaryAsync(
        string prompt, string draftResponse, ResearchJob job, CancellationToken ct)
    {
        var header = $"# Executive Summary\n\n" +
            $"**Sources analyzed:** {job.AcquiredSourceIds.Count} | " +
            $"**Grounding Score:** {job.GroundingScore:P0} | " +
            $"**Sub-Questions:** {job.SubQuestions.Count} ({job.SubQuestionCoverage.Count(kv => kv.Value == "answered")} answered)\n\n---\n\n";

        // For local models, skip the LLM call — the report already has structured sections.
        // This saves ~15-30s on local Ollama for marginal improvement.
        var isLocalOnly = _settings.Routing == RoutingStrategy.LocalOnly
            || (_settings.Routing == RoutingStrategy.LocalWithCloudFallback && !_settings.UsePaidProvider);

        if (isLocalOnly)
        {
            // Use the MostSupportedView as the executive summary (already extracted)
            var templateSummary = job.MostSupportedView ?? "See full report for details.";
            return header + templateSummary;
        }

        try
        {
            var truncatedReport = draftResponse.Length > 4000 ? draftResponse[..4000] : draftResponse;
            var summaryPrompt = $@"Write a concise executive summary (200-300 words) of this research report. Include:
1. The research question and its importance
2. Key methodology (number of sources, approach)
3. 3-4 most important findings with their citation numbers [N]
4. Primary conclusion
5. Notable limitations or gaps

Research Report:
{truncatedReport}

Write ONLY the executive summary text, no headings.";

            var summary = await _llmService.GenerateAsync(summaryPrompt, maxTokens: 500, ct: ct);
            return header + summary;
        }
        catch
        {
            // Fallback to template if LLM fails
            return header + (job.MostSupportedView ?? "See full report for details.");
        }
    }

    /// <summary>
    /// Deduplicates evidence results by DOMAIN: keeps only the highest-scoring chunks
    /// per unique domain (max 3 per domain), ensuring citation diversity across different sites.
    /// After dedup, fills remaining slots with additional chunks if needed.
    /// </summary>
    internal static List<RetrievalResult> DeduplicateEvidenceBySource(
        List<RetrievalResult> results, Dictionary<string, string> sourceUrlMap)
    {
        const int maxChunksPerDomain = 3;
        var domainCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var primary = new List<RetrievalResult>();    // Diverse domain chunks
        var overflow = new List<RetrievalResult>();   // Excess chunks from saturated domains

        foreach (var er in results)
        {
            var sourceKey = sourceUrlMap.TryGetValue(er.SourceId, out var url) ? url : er.SourceId;
            var domain = ExtractDomain(sourceKey);

            domainCounts.TryGetValue(domain, out var count);
            if (count < maxChunksPerDomain)
            {
                primary.Add(er);
                domainCounts[domain] = count + 1;
            }
            else
            {
                overflow.Add(er);
            }
        }

        // Return diverse results first, then overflow if slots remain
        var combined = new List<RetrievalResult>(primary);
        combined.AddRange(overflow);
        return combined;
    }

    /// <summary>
    /// Extracts the domain (e.g. "example.com") from a URL or source ID.
    /// Strips "www." prefix for consistent dedup.
    /// </summary>
    private static string ExtractDomain(string urlOrId)
    {
        try
        {
            if (urlOrId.StartsWith("http"))
            {
                var host = new Uri(urlOrId).Host.ToLowerInvariant();
                return host.StartsWith("www.") ? host[4..] : host;
            }
        }
        catch { /* not a valid URL */ }
        return urlOrId;
    }

    private string BuildEvidenceContext(List<RetrievalResult> results, List<Citation> citations, Dictionary<string, string>? sourceUrlMap = null)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(results.Count, citations.Count); i++)
        {
            var url = sourceUrlMap != null && sourceUrlMap.TryGetValue(results[i].SourceId, out var u) ? u : results[i].SourceId;
            sb.AppendLine($"{citations[i].Label} (Source URL: {url}, Score: {results[i].Score:F2}):");
            sb.AppendLine(results[i].Chunk.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string ExtractSection(string text, string sectionName)
    {
        var pattern = $"## {sectionName}";
        var idx = text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return text;

        var start = idx + pattern.Length;
        var nextSection = text.IndexOf("\n## ", start);
        return nextSection > 0 ? text[start..nextSection].Trim() : text[start..].Trim();
    }

    internal static List<string> ExtractClaims(string text)
    {
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Trim().Length > 20 && !l.Trim().StartsWith("#") && !l.Trim().StartsWith("*"))
            .Take(20)
            .ToList();
    }

    private static CoverageResult EvaluateCoverage(List<RetrievalResult> results, ResearchJob job)
    {
        var gaps = new List<string>();

        if (results.Count == 0)
            return new CoverageResult { Score = 0, Gaps = new() { "no results found" } };

        // Factor 1: Source count coverage (max 0.3)
        var sourceCountScore = Math.Min(1.0, (double)job.AcquiredSourceIds.Count / job.TargetSourceCount) * 0.3;

        // Factor 2: Result relevance (max 0.3) — average of top scores
        var topScores = results.Take(10).Select(r => (double)r.Score).ToList();
        var relevanceScore = (topScores.Count > 0 ? topScores.Average() : 0) * 0.3;

        // Factor 3: Source diversity (max 0.2) — unique domains
        var uniqueDomains = results.Select(r => r.SourceId).Distinct().Count();
        var diversityScore = Math.Min(1.0, uniqueDomains / 3.0) * 0.2;

        // Factor 4: Content volume (max 0.2) — total text length
        var totalTextLength = results.Sum(r => r.Chunk.Text.Length);
        var volumeScore = Math.Min(1.0, totalTextLength / 5000.0) * 0.2;

        var score = sourceCountScore + relevanceScore + diversityScore + volumeScore;

        if (results.Count < 3)
            gaps.Add("insufficient sources");
        if (topScores.All(s => s < 0.5))
            gaps.Add("low relevance scores");
        if (uniqueDomains < 2)
            gaps.Add("insufficient source diversity");
        if (totalTextLength < 1000)
            gaps.Add("insufficient content depth");

        // Heuristic sub-question coverage: estimate based on overall score
        var answered = new List<string>();
        var unanswered = new List<string>();
        if (job.SubQuestions.Count > 0 && score > 0.3)
        {
            // Assume coverage is proportional — mark sub-questions based on score
            var answeredCount = (int)Math.Round(job.SubQuestions.Count * Math.Min(1.0, score));
            for (int i = 0; i < job.SubQuestions.Count; i++)
            {
                if (i < answeredCount)
                {
                    answered.Add(job.SubQuestions[i]);
                    job.SubQuestionCoverage[job.SubQuestions[i]] = "answered";
                }
                else
                {
                    unanswered.Add(job.SubQuestions[i]);
                    job.SubQuestionCoverage[job.SubQuestions[i]] = "unanswered";
                }
            }
        }

        return new CoverageResult
        {
            Score = Math.Min(1.0, score),
            Gaps = gaps,
            AnsweredSubQuestions = answered,
            UnansweredSubQuestions = unanswered
        };
    }

    // ──────────────────────────────────────────────────────────
    // A1: Question Decomposition — break prompt into sub-questions
    // ──────────────────────────────────────────────────────────
    private async Task<List<string>> DecomposeQuestionAsync(string prompt, DomainPack pack, CancellationToken ct)
    {
        var decompositionPrompt = $@"You are a research analyst. Break this research question into 3-4 focused sub-questions that each need independent evidence to answer. Be specific and concise.

Research question: {prompt}
Domain: {pack}

Return ONLY a numbered list of sub-questions:
1. [question]
2. [question]
3. [question]
4. [question]";

        try
        {
            var response = await _llmService.GenerateAsync(decompositionPrompt, maxTokens: 400, ct: ct);
            var subQuestions = new List<string>();
            foreach (var line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                // Match lines starting with a number followed by . or )
                if (trimmed.Length > 3 && char.IsDigit(trimmed[0]) && (trimmed.Contains('.') || trimmed.Contains(')')))
                {
                    var dotIdx = trimmed.IndexOf('.');
                    var parenIdx = trimmed.IndexOf(')');
                    var sepIdx = dotIdx >= 0 && parenIdx >= 0 ? Math.Min(dotIdx, parenIdx) : Math.Max(dotIdx, parenIdx);
                    if (sepIdx > 0 && sepIdx < trimmed.Length - 1)
                    {
                        var q = trimmed[(sepIdx + 1)..].Trim();
                        if (q.Length > 10)
                            subQuestions.Add(q);
                    }
                }
            }
            return subQuestions.Count >= 2 ? subQuestions.Take(4).ToList() : new List<string> { prompt };
        }
        catch
        {
            return new List<string> { prompt };
        }
    }

    // ──────────────────────────────────────────────────────────
    // A2: Semantic Coverage Evaluation via LLM
    // ──────────────────────────────────────────────────────────
    private async Task<CoverageResult> EvaluateCoverageWithLlmAsync(
        string prompt, List<string> subQuestions, List<RetrievalResult> results,
        ResearchJob job, CancellationToken ct)
    {
        // Fast fallback if no sub-questions or very few results
        if (subQuestions.Count <= 1 || results.Count < 2)
            return EvaluateCoverage(results, job);

        var evidenceSummary = new StringBuilder();
        foreach (var r in results.Take(10))
        {
            var text = r.Chunk.Text.Length > 300 ? r.Chunk.Text[..300] : r.Chunk.Text;
            evidenceSummary.AppendLine($"- (Score: {r.Score:F2}) {text}");
        }

        var subQList = string.Join("\n", subQuestions.Select((q, i) => $"{i + 1}. {q}"));

        var evalPrompt = $@"You are evaluating research coverage. Given these sub-questions and evidence, determine which sub-questions are well-answered, partially-answered, or unanswered.

Sub-questions:
{subQList}

Evidence collected:
{evidenceSummary}

Respond in EXACTLY this JSON format (no markdown, no code fences):
{{""answered"": [1, 3], ""partial"": [2], ""unanswered"": [5, 6], ""score"": 0.65}}

Use the sub-question numbers. Score should be 0.0-1.0 reflecting overall coverage.";

        try
        {
            var response = await _llmService.GenerateAsync(evalPrompt, maxTokens: 300, ct: ct);

            // Strip markdown code fences if present
            response = response.Trim();
            if (response.StartsWith("```")) response = response[(response.IndexOf('\n') + 1)..];
            if (response.EndsWith("```")) response = response[..response.LastIndexOf("```")];
            response = response.Trim();

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            var score = root.TryGetProperty("score", out var scoreProp) ? scoreProp.GetDouble() : 0.5;
            var unanswered = new List<string>();
            var answered = new List<string>();

            if (root.TryGetProperty("unanswered", out var uProp))
            {
                foreach (var item in uProp.EnumerateArray())
                {
                    var idx = item.GetInt32() - 1;
                    if (idx >= 0 && idx < subQuestions.Count)
                        unanswered.Add(subQuestions[idx]);
                }
            }

            if (root.TryGetProperty("answered", out var aProp))
            {
                foreach (var item in aProp.EnumerateArray())
                {
                    var idx = item.GetInt32() - 1;
                    if (idx >= 0 && idx < subQuestions.Count)
                        answered.Add(subQuestions[idx]);
                }
            }

            // Update sub-question coverage tracking
            foreach (var sq in subQuestions)
            {
                if (answered.Contains(sq)) job.SubQuestionCoverage[sq] = "answered";
                else if (unanswered.Contains(sq)) job.SubQuestionCoverage[sq] = "unanswered";
                else job.SubQuestionCoverage[sq] = "partial";
            }

            return new CoverageResult
            {
                Score = Math.Clamp(score, 0, 1),
                Gaps = unanswered,
                AnsweredSubQuestions = answered,
                UnansweredSubQuestions = unanswered
            };
        }
        catch
        {
            // Fallback to heuristic
            return EvaluateCoverage(results, job);
        }
    }

    // ──────────────────────────────────────────────────────────
    // A3: Answer Sufficiency Check — post-draft verification
    // ──────────────────────────────────────────────────────────
    private async Task<SufficiencyResult> VerifyAnswerSufficiencyAsync(
        string prompt, List<string> subQuestions, string draftReport, CancellationToken ct)
    {
        var subQList = subQuestions.Count > 1
            ? string.Join("\n", subQuestions.Select((q, i) => $"{i + 1}. {q}"))
            : "(no sub-questions)";

        var verifyPrompt = $@"You are a research quality reviewer. Evaluate whether this draft report adequately answers the research question.

Research Question: {prompt}

Sub-Questions:
{subQList}

Draft Report (first 3000 chars):
{(draftReport.Length > 3000 ? draftReport[..3000] : draftReport)}

Respond in EXACTLY this JSON format (no markdown, no code fences):
{{""sufficient"": true, ""score"": 0.85, ""missingTopics"": [""topic 1"", ""topic 2""], ""weakClaims"": [""claim without evidence""], ""suggestions"": ""Consider searching for ...""}}

- sufficient: true if the report substantially answers the main question
- score: 0.0-1.0 overall quality
- missingTopics: topics the report doesn't cover that it should
- weakClaims: claims made without citation support
- suggestions: brief advice for improvement";

        try
        {
            var response = await _llmService.GenerateAsync(verifyPrompt, maxTokens: 400, ct: ct);

            response = response.Trim();
            if (response.StartsWith("```")) response = response[(response.IndexOf('\n') + 1)..];
            if (response.EndsWith("```")) response = response[..response.LastIndexOf("```")];
            response = response.Trim();

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            return new SufficiencyResult
            {
                Sufficient = root.TryGetProperty("sufficient", out var suf) && suf.GetBoolean(),
                Score = root.TryGetProperty("score", out var sc) ? sc.GetDouble() : 0.5,
                MissingTopics = root.TryGetProperty("missingTopics", out var mt)
                    ? mt.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
                    : new(),
                WeakClaims = root.TryGetProperty("weakClaims", out var wc)
                    ? wc.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
                    : new(),
                Suggestions = root.TryGetProperty("suggestions", out var sg) ? sg.GetString() ?? "" : ""
            };
        }
        catch
        {
            return new SufficiencyResult { Sufficient = true, Score = 0.5 };
        }
    }

    // ──────────────────────────────────────────────────────────
    // A4: Claim-Evidence Grounding Score
    // ──────────────────────────────────────────────────────────
    private static readonly System.Text.RegularExpressions.Regex CitationRefRegex = new(
        @"\[\d+\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static double ComputeGroundingScore(List<string> claims)
    {
        if (claims.Count == 0) return 0;
        int cited = claims.Count(c => CitationRefRegex.IsMatch(c));
        return (double)cited / claims.Count;
    }

    private static void AddStep(Data.SessionDb db, ResearchJob job, string action, string detail, JobState state, bool success = true, string? error = null)
    {
        var step = new JobStep
        {
            JobId = job.Id,
            StepNumber = job.Steps.Count + 1,
            Action = action,
            Detail = detail,
            StateAfter = state,
            Success = success,
            Error = error
        };
        job.Steps.Add(step);
        db.SaveJobStep(step);
    }

    private static void AddReplay(ResearchJob job, string type, string title, string description, string? linkedSourceId = null)
    {
        job.ReplayEntries.Add(new ReplayEntry
        {
            Order = job.ReplayEntries.Count + 1,
            Title = title,
            Description = description,
            EntryType = type,
            LinkedSourceId = linkedSourceId
        });
    }
}

internal class CoverageResult
{
    public double Score { get; set; }
    public List<string> Gaps { get; set; } = new();
    public List<string> AnsweredSubQuestions { get; set; } = new();
    public List<string> UnansweredSubQuestions { get; set; } = new();
}

internal class SufficiencyResult
{
    public bool Sufficient { get; set; }
    public double Score { get; set; }
    public List<string> MissingTopics { get; set; } = new();
    public List<string> WeakClaims { get; set; } = new();
    public string Suggestions { get; set; } = "";
}
