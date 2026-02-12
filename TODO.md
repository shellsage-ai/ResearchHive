# Research Quality & Speed Upgrade — Implementation Plan

## Part A: Closed-Loop Verification & Answer Quality

### A1. Question Decomposition (Pre-Search)
> Before searching, break the user's prompt into atomic sub-questions via LLM so coverage evaluation becomes semantic, not just statistical.

- [x] **A1.1** Add `DecomposeQuestionAsync(string prompt, DomainPack pack, CancellationToken ct)` method to `ResearchJobRunner`
  - LLM call: "Break this research question into 3–7 atomic sub-questions that each need independent evidence"
  - Parse response into `List<string>` of sub-questions
  - Store sub-questions on the `ResearchJob` model (new `SubQuestions` property)
- [x] **A1.2** Add `SubQuestions` property (`List<string>`) to `ResearchJob` model in `Jobs.cs`
- [x] **A1.3** Add `SubQuestionCoverage` property (`Dictionary<string, bool>`) to `ResearchJob` for tracking which sub-questions have evidence
- [x] **A1.4** Wire decomposition into `RunAsync` — call after job creation, before the search loop begins
  - Use sub-questions to generate better search queries (replace or supplement the initial query list)
  - Add replay entry: "Question Decomposition" showing the sub-questions
- [x] **A1.5** Wire decomposition into `ContinueJobAsync` for resumed jobs (carry forward existing sub-questions)

### A2. Semantic Coverage Evaluation (Replace Heuristic)
> Replace the current purely-quantitative `EvaluateCoverage` with an LLM-based check that maps evidence to sub-questions.

- [x] **A2.1** Create `EvaluateCoverageWithLlmAsync(string sessionId, string prompt, List<string> subQuestions, List<RetrievalResult> results, CancellationToken ct)` method
  - Build evidence summary from top 10 retrieval results
  - LLM prompt: "Given these sub-questions and this evidence, which sub-questions are well-answered, partially-answered, or unanswered? Return JSON: `{answered: [...], partial: [...], unanswered: [...], score: 0.0-1.0}`"
  - Parse JSON response into a `CoverageResult` (reuse existing model, populate Gaps from unanswered list)
- [x] **A2.2** Update `CoverageResult` model — add `AnsweredSubQuestions`, `UnansweredSubQuestions` lists
- [x] **A2.3** Replace `EvaluateCoverage` call sites in `RunAsync` iteration loop with the LLM-based version
  - Keep the old heuristic as fast fallback if LLM call fails
- [x] **A2.4** Use unanswered sub-questions to generate targeted refinement queries (replace current gap-based query refinement at step 7)
- [x] **A2.5** Update replay entries to show per-sub-question coverage status
- [x] **A2.6** Also update `ContinueJobAsync` to use the same LLM-based coverage

### A3. Answer Sufficiency Check (Post-Draft Verification)
> After the draft report is generated, send it back to the LLM with the original question to verify completeness. If gaps remain, trigger another search+draft cycle.

- [x] **A3.1** Add `VerifyAnswerSufficiencyAsync(string prompt, List<string> subQuestions, string draftReport, CancellationToken ct)` method
  - LLM prompt: "You are a research quality reviewer. Given this research question, its sub-questions, and the draft report below, evaluate: (1) Does the report directly answer the main question? (2) Which sub-questions are fully addressed vs. missing? (3) Are claims backed by citations or stated as hypothesis? Return JSON: `{sufficient: true/false, score: 0.0-1.0, missingTopics: [...], weakClaims: [...], suggestions: string}`"
  - Returns a `SufficiencyResult` record
- [x] **A3.2** Create `SufficiencyResult` model — `bool Sufficient`, `double Score`, `List<string> MissingTopics`, `List<string> WeakClaims`, `string Suggestions`
- [x] **A3.3** Wire into `RunAsync` after the draft is generated (step 8) and before validation (step 9)
  - If `!sufficient && Score < 0.7`: trigger a remediation cycle — search for missing topics, re-acquire, re-index, re-draft
  - Cap at 1 remediation cycle to avoid infinite loops
  - Add replay entry: "Sufficiency Check" showing result
- [x] **A3.4** On remediation: generate targeted queries from `MissingTopics`, run `SearchMultiLaneAsync`, acquire + index, then re-run the draft with expanded evidence
- [x] **A3.5** Update progress UI — emit "Verifying answer completeness..." and "Remediation: searching for N missing topics..." status messages

### A4. Claim-Evidence Grounding Score
> After the draft, measure the ratio of cited vs. uncited claims as a hard confidence metric.

- [x] **A4.1** Refactor `ExtractClaims` + claim ledger loop into a `ComputeGroundingScore` method
  - Count claims with citation references vs. claims without
  - Return `double groundingScore` (ratio of cited / total)
- [x] **A4.2** Store grounding score on `ResearchJob` (new `GroundingScore` property)
- [x] **A4.3** Display grounding score in the Executive Summary report header
  - E.g. "**Grounding:** 85% of claims are citation-backed"
- [x] **A4.4** If grounding score < 0.5 and sufficiency check passed, add a warning to the report: "⚠ Many claims lack direct citations — verify independently"
- [x] **A4.5** Add grounding score to replay timeline as a "Quality Metrics" entry

---

## Part B: Browser Scaling & Search Speed

### B1. Browser Pool Architecture
> Replace the single `IBrowser` instance in `BrowserSearchService` with a pool of up to 15 browser contexts that can run searches in parallel.

- [x] **B1.1** Add `MaxBrowserContexts` setting to `AppSettings` (default: 8, max: 15)
  - This controls how many concurrent browser contexts (each with its own cookies/session) can run simultaneously
- [x] **B1.2** Add `BrowserContextPool` — a `Channel<IBrowserContext>` or `SemaphoreSlim`-guarded pool inside `BrowserSearchService`
  - On init: create the single `IBrowser` as before (Playwright shares one browser process efficiently)
  - Pool manages N pre-warmed `IBrowserContext` instances
  - `RentContextAsync()` → returns available context (or creates one up to max)
  - `ReturnContext(IBrowserContext ctx)` → returns to pool for reuse
  - `DisposePool()` → closes all contexts on shutdown
- [x] **B1.3** Update `SearchAsync` to rent a context from the pool instead of creating/disposing one per call
  - Currently creates + disposes a context per search call — this is the bottleneck
  - With pooling: rent → navigate → extract → return
  - Context reuse avoids repeated cold-start overhead (cookie setup, JS injection)
- [x] **B1.4** Update `DisposeAsync` to drain and close the pool cleanly

### B2. Parallel Query Execution (Scale Up)
> Currently limited to 2 queries in parallel. Scale to process ALL queries concurrently, bounded by the context pool.

- [x] **B2.1** Remove the `maxParallelQueries = 2` batch loop in `SearchMultiLaneAsync`
  - Replace with: fire all (query × engine) combinations concurrently, throttled only by the context pool semaphore
  - Pool size (B1.2) naturally limits concurrency to `MaxBrowserContexts`
- [x] **B2.2** Compute optimal concurrency automatically:
  - `effectiveConcurrency = Math.Min(MaxBrowserContexts, queries.Count * SearchEngines.Length)`
  - Log the chosen concurrency level in progress output
- [x] **B2.3** Update Google search to also run concurrently with Playwright searches
  - Currently Google is launched "alongside" but sequentially within each query batch
  - Move Google search tasks into the same `Task.WhenAll` as the Playwright engine tasks
  - Google still serialized internally via its `_searchLock` — that's fine, it just won't block Playwright

### B3. Acquisition Speed
> SnapshotService uses HttpClient — scale up concurrent fetches from 2 to auto-tuned based on URL count.

- [x] **B3.1** Update `MaxConcurrentFetches` default from 2 → 6 in `AppSettings`
- [x] **B3.2** Add auto-tuning in `RunAsync` acquisition block:
  - `int fetchConcurrency = Math.Min(15, Math.Max(4, urlsToAcquire.Count / 2))`
  - Use this instead of hardcoded `_settings.MaxConcurrentFetches` for the `SemaphoreSlim`
  - Same for deep search acquisition and `ContinueJobAsync`
- [x] **B3.3** Add per-domain rate limiting (reuse existing `MaxConcurrentPerDomain` / `MinDomainDelaySeconds` settings)
  - Group URLs by domain, enforce max 2 concurrent per domain even while fetching 6-15 globally
  - Prevents hammering a single site while still achieving high throughput across different hosts

### B4. Early-Exit Optimization
> Stop searching early when we already have enough high-quality URLs.

- [x] **B4.1** Make the 40-URL cap in `SearchMultiLaneAsync` dynamic: `targetSources * 4` (capped at 60)
  - More sources requested → more URLs collected → better selection
- [x] **B4.2** Add a `CancellationTokenSource` within `SearchMultiLaneAsync` that fires when URL count reaches the dynamic cap
  - Cancel remaining engine tasks gracefully (they'll get `OperationCanceledException` and return)
  - Prevents wasting time on slow engines when we already have enough URLs
- [x] **B4.3** Prioritize URL deduplication during collection (not just at the end)
  - Check `allUrls` before adding to skip known duplicates immediately
  - Use `ConcurrentHashSet` pattern (ConcurrentDictionary<string, byte>) instead of ConcurrentBag

---

## Part C: Progress Visibility & Quality Metrics

### C1. Enhanced Progress Reporting
- [x] **C1.1** Emit sub-question decomposition results in progress log: "Decomposed into N sub-questions: ..."
- [x] **C1.2** Emit per-sub-question coverage status during evaluation: "✅ Q1 answered | ❌ Q2 unanswered | ⚠ Q3 partial"
- [x] **C1.3** Emit sufficiency check results: "Answer sufficiency: 82% — 1 topic needs more evidence"
- [x] **C1.4** Emit grounding score: "Grounding: 85% of claims cited"
- [x] **C1.5** Emit browser pool utilization: "Searching with N browser contexts across M engine×query combinations"
- [x] **C1.6** Update Overview tab `OverviewText` to include grounding score and sufficiency rating after research completes

### C2. Build & Test Verification
- [x] **C2.1** Build `ResearchHive.Core` — verify 0 errors
- [x] **C2.2** Run all tests — verify 104+ tests pass
- [x] **C2.3** Build full WPF project (if not locked)

---

## Execution Order

1. **A1** (Question Decomposition) — foundation for everything else
2. **A2** (Semantic Coverage) — depends on A1 sub-questions
3. **B1** (Browser Pool) — independent, can be done in parallel with A2
4. **B2** (Parallel Query Scale) — depends on B1
5. **B3** (Acquisition Speed) — independent of B1/B2
6. **B4** (Early-Exit) — depends on B2
7. **A3** (Sufficiency Check) — depends on A1 for sub-questions, independent of B*
8. **A4** (Grounding Score) — lightweight, depends on draft being generated
9. **C1** (Progress Reporting) — after all A* and B* items
10. **C2** (Build & Test) — final verification

## Expected Impact

| Metric | Before | After |
|--------|--------|-------|
| Search speed (5 queries × 4 engines) | ~60-90s (2 parallel) | ~15-25s (8-15 parallel) |
| Acquisition speed (10 URLs) | ~15-30s (2 concurrent) | ~5-10s (6+ concurrent) |
| Coverage evaluation | Quantitative heuristic (source count, scores) | Semantic: maps evidence to sub-questions |
| Answer verification | None — draft is final | Post-draft sufficiency check + 1 remediation cycle |
| Confidence metric | None | Grounding score: % of claims with citations |
| Quality feedback loop | Stop at 0.7 coverage heuristic | Stop when sub-questions are answered OR remediation exhausted |
