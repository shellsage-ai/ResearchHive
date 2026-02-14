# Changelog

All changes are tracked in `CAPABILITY_MAP.md` (Change Log section) for granular file-level detail.
This file provides a high-level summary per milestone.

## 2026-02-14 — Phase 32: Report Export Quality
### 7-Step fix for issues discovered during export quality audit
- **Step 1 — GitHub API logging**: `FetchJsonAsync` in RepoScannerService now logs 403 (rate-limit with `GitHubPat` hint), 401 (bad token), and general failures with `X-RateLimit-Remaining` header. `ILogger<RepoScannerService>` injected via optional constructor parameter. `LastHttpFailureStatus` property tracks last failure code.
- **Step 2 — Setext header cleanup**: `ConvertSetextToAtx` preprocessor in ExportService converts setext-style headers (`===`/`---` underlines) to ATX-style (`#`/`##`). Orphaned separator lines removed. Prompt rule added in ProjectFusionEngine to prefer ATX headers.
- **Step 3 — Projected Capabilities bullet-only**: Prompt rules in `BuildSectionInstructions` and `GetSectionGuidance` now enforce "Output ONLY bullet points — no prose between items." `ParseList` rewritten to filter out non-bullet prose lines (keeps only `-`, `*`, `•`, or numbered items).
- **Step 4 — Gaps Closed logical connection**: Prompt instructions require resolutions to be "logically connected — the resolving capability must DIRECTLY address the gap domain." `FusionPostVerifier.ValidateGapsClosed` now validates bullet items with `→` arrows against profile capabilities, flagging fabricated or unverifiable claims.
- **Step 5 — Strength coverage increase**: RepoScannerService prompt now requests "at least 10-15 specific strengths covering all major subsystems" (up from 5).
- **Step 6 — Complement hallucination guard**: Complement prompt now requires "Use ONLY the descriptions provided alongside each URL. If no description was provided, set purpose to 'Description not available'."
- **Step 7 — Framework deduplication**: `DetectFrameworkHints` now guards bare "WPF" with `!hints.Any(h => h.StartsWith("WPF"))` to prevent duplication when "WPF + MVVM" already present. Same for Windows Forms.
- **Tests**: 16 new (Phase32ReportQualityTests.cs + ExportServiceTests.cs additions) — 667 total (667 passed, 0 failed)

## 2026-02-13 — Batch Scan Fix: Robustness & Error Recovery
- **Silent early-return fix**: `ScanMultiRepoAsync` now sets `RepoScanStatus` feedback when `RepoUrlList` is empty or no URLs are parsed, instead of returning silently.
- **Continue on individual failure**: Each repo scan is wrapped in its own try-catch. One failed repo no longer aborts the entire batch — remaining repos continue scanning.
- **Partial results preserved**: `LoadSessionData()` is now called on both error and cancellation paths, so any successfully scanned repos appear in the UI.
- **Per-scan notifications**: `NotifyRepoScanComplete` fires after each individual scan, matching single-scan behavior.
- **Summary reporting**: Status shows `succeeded/total` counts plus individual failure details.
- **Tests**: 12 new batch scan URL parsing tests — 651 total (651 passed, 0 failed)
- **Context transfer file**: Created `CONTEXT_TRANSFER.md` for session continuity.

## 2026-02-13 — Phase 31: Anti-Hallucination & Factual Accuracy Hardening
### 6-Step Pipeline to Reduce LLM Hallucination in Scan and Fusion Outputs
- **Step 1 — Strength grounding (PostScanVerifier)**: `GroundStrengthDescriptions` matches overstatement patterns ("parallel RAG", "retry logic", "structured logging", etc.) against fact sheet evidence. `DeflateDescription` removes vague adjectives (robust, comprehensive, powerful, advanced, sophisticated, seamless). `FindMatchingCapability` uses keyword-overlap matching to replace inflated claims with verified capability descriptions.
- **Step 2 — Scan self-validation LLM pass**: `SelfValidateStrengthsAsync` in RepoIntelligenceJobRunner cross-checks strengths vs fact sheet via Mini-tier LLM call. Parses ORIGINAL/CORRECTED pairs and applies inline corrections.
- **Step 3 — FusionPostVerifier** (new file, ~505 lines): 5 validators for fusion output — `ValidateTechStackTable` (removes fabricated tech rows), `ValidateFeatureMatrix` (re-attributes misattributed features), `ValidateGapsClosed` (removes fabricated gap closures), `ValidateProvenance` (removes orphaned entries), `ValidateProseAsync` (LLM fact-checks UNIFIED_VISION and ARCHITECTURE).
- **Step 4 — Wire FusionPostVerifier**: Registered in DI (ServiceRegistration.cs), injected into ProjectFusionEngine, runs after section expansion with replay logging.
- **Step 5 — Cross-section consistency**: `BuildConsistencyContext` extracts identity/tech/feature decisions from batch 1 sections, passes `priorSectionContext` into later expansion batches. Tightened `UNIFIED_VISION` guidance (require source attribution per capability) and `ARCHITECTURE` guidance (every component must trace to a real class).
- **Step 6 — Prompt tightening**: `AppendFormatInstructions` expanded with "STRENGTH DESCRIPTION PRECISION" rules — precise verbs, anti-inflation examples, class-name requirement.
- **Bug fix**: `ValidateFeatureMatrix` table rows were incorrectly matched by `FEATURE: X | SOURCE: Y` regex, bypassing re-attribution logic. Tables now processed first.
- **Tests**: 35 new (Phase31VerifierTests.cs) — 639 total (639 passed, 0 failed)

## 2026-02-13 — Export Markdown Depth-Limit Fix
- **SafeMarkdownToHtml**: New method in ExportService.cs wraps `Markdig.Markdown.ToHtml()` with try/catch for "too deeply nested" errors
- **FlattenMarkdownNesting**: Pre-processes markdown to collapse 4+ levels of nested blockquotes/lists to 3 levels
- Applied to all 3 `Markdig.Markdown.ToHtml()` call sites in ExportService
- **Tests**: 604 total (604 passed, 0 failed)

## 2026-02-13 — Phase 30: Scan Identity Confusion & Cross-Contamination Fix
### 8-Step Fix for Report Accuracy
- **Fix 1 — Wipe stale profiles before re-scan**: `ScanRepoAsync` now deletes existing profile for same URL before writing new one, preventing merges of old + new data
- **Fix 2 — Source-ID filter in RAG queries**: All 12 RAG analysis queries and per-gap verification queries now filter chunks by `sourceIdFilter` matching the scan's `profileId`, preventing cross-repo chunk contamination
- **Fix 3 — Identity context in analysis prompt**: LLM analysis prompt now starts with explicit identity block — "You are analyzing {owner}/{name}" with owner, URL, and primary language set as hard constraints
- **Fix 4 — Identity context in gap verification**: Gap verification prompt includes identity header preventing "this project" confusion
- **Fix 5 — Stronger anti-confusion instructions**: System prompt for analysis now includes 3 explicit rules: never confuse projects, never attribute capabilities from other repos, if asked about repo X only answer about repo X
- **Fix 6 — CodeBook identity header**: CodeBook generation prompt now includes project identity header
- **Fix 7 — AnalysisSummary field**: New `RepoProfile.AnalysisSummary` field captures the summary from analysis; `InfrastructureStrengths` now populated from parsed `### Infrastructure` sub-section
- **Fix 8 — Report generation identity**: Report generation includes identity reminder in system prompt
- **Tests**: 597 total (597 passed, 0 failed) — committed as `6f3ca51`

## 2026-02-14 — Phase 29: Identity Scan (Dedicated Pipeline Phase 2.25)
### Product-Level Identity Recognition
- **Dedicated identity scan phase**: `RunIdentityScanAsync` reads README + docs/spec/project briefs + entry points (6000 char cap), runs 1 focused LLM call (~800 tokens), produces `ProductCategory` + `CoreCapabilities`
- **Local repo description**: Fills empty `Description` for local repos via deterministic first-paragraph extraction (`ExtractFirstParagraph`)
- **Enriched fusion input**: `FormatProfileForLlm` now includes ProductCategory + CoreCapabilities
- **UI**: ProductCategory badge (amber) + CoreCapabilities list (purple) on scan cards
- **Tests**: 599 total (597 passed, 2 skipped)

## 2026-02-14 — Phase 28: Fusion Identity Grounding + Scan/Fusion Cancellation
### Anti-Hallucination Rules #11-12 + Cancellation Support
- **12 grounding rules**: Added #11 (cross-attribution prevention) and #12 (distinct purpose preservation) to fusion system prompt
- **Identity-first outline**: 2-step outline generation — verify identities then outline
- **Per-section identity reminder injection**: Every section expansion call includes identity markers
- **╔══ PROJECT ══╗ markers**: Prominent visual markers in `FormatProfileForLlm`
- **CodeBook limit**: 2500→4000 chars for richer context
- **Stronger Summary prompts**: All 4 scan paths include explicit project summary instructions
- **Cancellation**: CancellationTokenSource per scan/fusion/discovery operation, Cancel buttons with confirmation dialogs
- **Tests**: 599 total (597 passed, 2 skipped)

## 2026-02-14 — Phase 27: Scan & Fusion Quality Overhaul
### ProjectSummary + ProjectedCapabilities + Anti-Hallucination
- **`ProjectSummary` field**: Concise 1-3 sentence project summary extracted during scan via `## Summary` section in all 4 scan prompt paths + parsers
- **`ProjectedCapabilities`**: New PROJECTED_CAPABILITIES fusion section — forward-looking capability predictions, goal-aware (compare vs merge variations)
- **Anti-hallucination rules**: 3 new grounding rules in all scan prompts preventing analysis tool citation as strengths
- **UI panels**: New Summary and ProjectedCapabilities sections on scan/fusion cards
- **Tests**: 599 total (597 passed, 2 skipped)

## 2026-02-14 — Phase 26: Project Fusion Quality Overhaul
### Anti-Hallucination Grounding for Fusion Engine
- **Grounded system prompt**: 7 critical rules — only reference technologies from input data, no inventing libraries, every claim must be traceable to input profiles
- **Comprehensive input formatting**: `FormatProfileForLlm` now includes all dependencies with versions/licenses, Topics, OpenIssues, LastCommitUtc, TopLevelEntries (file tree), CodeBook (architecture summary), up to 40 deps (was 20 names only)
- **New PROJECT_IDENTITIES section**: Every fusion now starts with identity cards — what each project IS, source URL, language/framework, 3-5 capabilities, maturity indicators

### Goal-Specific Fusion Modes (Merge / Extend / Compare / Architect)
- **Detailed goal instructions**: 5-line contextual instructions per mode (was generic one-liners)
- **Section-specific expand prompts**: Each of the 9 sections gets goal-aware writing guidance via `GetSectionGuidance()` switch expression
- **Compare mode differentiation**: Vision→"Comparison Overview", Architecture→"Architecture Comparison" with tables, GapsClosed→"Complementary Strengths", Provenance→"Recommendation Map"
- **Report headings adapt per goal**: `GenerateReport` uses goal-aware section titles

### Source References & Provenance
- **Source Projects header in report**: Every fusion report now lists input URLs/paths at the top
- **Input URLs tracked through pipeline**: `inputUrls` collected during gather phase, passed to report generation
- **FormatFusionForLlm enhanced**: Prior fusions now include ProjectIdentities and InputSummary

### UI Improvements
- **Goal description shown in UI**: Fusion artifact cards now display what each mode means
- **Goal-aware section labels**: XAML binds to `VisionLabel`, `ArchitectureLabel`, `GapsClosedLabel`, `NewGapsLabel`, `ProvenanceLabel`
- **Project Identities visible**: New section in artifact card (auto-hidden when empty for backward compat)
- **Template descriptions enriched**: Each built-in fusion template now explains what the output will show

### Model & ViewModel Updates
- `ProjectFusionArtifact.ProjectIdentities` — new string field (backward-compatible, defaults empty)
- `ProjectFusionArtifactViewModel` — 8 new properties: `GoalDescription`, `ProjectIdentities`, `HasProjectIdentities`, `VisionLabel`, `ArchitectureLabel`, `GapsClosedLabel`, `NewGapsLabel`, `ProvenanceLabel`
- `ProjectFusionEngine.GoalDescription()` — public static method for UI use

### Stats
- Engine: 493→689 lines (net +196)
- All 597 tests pass, 0 failures

## 2026-02-13 — Phase 25: Research Report Quality + Readability Overhaul
### 6 Pipeline Bug Fixes (root causes of shallow, poorly-cited reports)
- **Iteration cap raised**: Was force-capped at 1, now allows 2 — gap-focused refinement pass adds 3-6 high-value sources
- **Sectional citation label collision fix** (HIGHEST IMPACT): `GetTargetedEvidenceForSectionAsync` was creating fresh `[1],[2]` labels per section, causing label collisions across sections (grounding would drop 60%→15%). Now reuses master labels via SourceId fallback + offsets truly-new labels beyond master count
- **Tighter early-exit thresholds**: Coverage must reach ≥0.85 with all sub-questions answered, or ≥0.6 with target sources met + no unanswered sub-Qs (was ≥0.7 or sources≥target + 0.4)
- **Sufficiency check runs in sectional mode**: Pre-grounding skip was based on monolithic draft, but sectional mode regenerates text differently — now always runs sufficiency when `SectionalReports = true`
- **Expert-level search queries**: 5 targeted angles (comparative/architectural, quantitative/benchmark, decision-framework, case-study, expert/academic) instead of generic "5 diverse queries" prompt
- **Default target sources 5→8**: Richer evidence base for complex topics

### Report Readability Improvements
- All prompts (monolithic `BuildSynthesisPrompt`, sectional per-section, executive summary) now instruct LLM to use **bold** for key terms, tables for comparisons, `>` blockquotes for takeaways, `` `code` `` formatting for technical terms, structured lists
- `ReportTemplateService` section instructions updated with per-section formatting guidance (bold findings, comparison tables, blockquote conclusions)
- All formatting renders natively via existing `MarkdownViewer` (bold, italic, tables, blockquotes, code inline all supported)
- **Tests**: 597 passing, 0 failures

## 2026-02-13 — Phase 24: Dynamic Anti-Hallucination Pipeline (4-Layer Filtering)
### Layer 1 — Expanded Models + Dynamic Inference
- `RepoFactSheet`: +DeploymentTarget, ArchitectureStyle, DomainTags, ProjectScale, InapplicableConcepts
- `ComplementProject`: +Stars, IsArchived, LastPushed, RepoLanguage
- `GitHubEnrichmentResult` class with `ToDescriptionString()`
- 5 new inference methods in `RepoFactSheetBuilder` (all table-driven/deterministic)

### Layer 2 — Structured Enrichment + 7 Deterministic Complement Checks
- `EnrichGitHubUrlAsync` refactored to return `GitHubEnrichmentResult`
- URL dedup via `NormalizeGitHubUrl` + seenUrls HashSet
- `PostScanVerifier`: 7 new complement checks (archived, staleness 2yr/3yr, stars <10/<50, repo language vs ecosystem, inapplicable concepts)
- `IsRepoLanguageCompatible` with 12 ecosystem language tables
- `PruneAppTypeInappropriateGaps` rewritten with InapplicableConcepts matching

### Layer 3 — LLM Relevance Check
- `LlmRelevanceCheckAsync` sends full project identity + complements for verdict
- Non-fatal (try/catch), respects MinimumComplementFloor

### Layer 4 — Dynamic Search Topics
- `InferDomainSearchTopics`: 17-rule table-driven (was 6 hardcoded if-blocks)
- `InferDiverseCategories`: 9 categories filtered by inapplicable concepts
- `BuildJsonComplementPrompt`: passes 8 additional fact sheet fields
- **Tests**: 30 new, 7 updated — 597 total (597 pass, 2 skip)

## 2026-02-13 — Phase 23: Dockerfile Gap Fix, Meta-Project Filter, Project Discovery
- **Dockerfile gap re-injection fix**: `InjectConfirmedGaps` now checks `GapsRemoved` for deliberately-pruned items before re-injecting
- **Meta-project filter**: `IsMetaProjectNotUsableDirectly` rejects infrastructure engines (dependabot-core/Ruby, renovate/Node) that aren't installable packages
- **Complement tuning**: MinimumComplements 5→8, MinimumComplementFloor 3→5
- **Project Discovery panel**: `GitHubDiscoveryService` (GitHub Search API), search/language-filter/min-stars UI, checkbox selection, one-click batch scan
- **Session nav fix** (commit `341a145`): Clear `SelectedSession` on Settings/Home navigation so re-clicking a session works
- **Tests**: 567 total (567 pass, 2 skip)

## 2026-02-13 — Phase 22: App-Type Gap Pruning, Active-Package Rejection, Domain-Aware Search
- **App-type gap pruning**: Desktop/WPF: prune auth/JWT/OAuth gaps; Desktop/console: prune Docker/K8s gaps; Desktop: prune middleware/API gateway gaps; DB contradiction: prune ORM/EF Core when using raw SQLite
- **Active package rejection**: Hard-reject complements matching already-installed packages or suggesting contradicted DB tech
- **Domain-aware search**: `InferDomainSearchTopics()` derives queries from proven capabilities (AI/LLM, RAG, Research, WPF, Resilience, Logging domains)
- **Context-enhanced prompts**: `BuildJsonComplementPrompt` now injects PROJECT CONTEXT block + 4 new anti-hallucination rules
- **Tests**: 14 new — 557 total (555 pass, 2 skip)

## 2026-02-13 — Phase 21: Self-Referential Fix, Complement Diversity, Local Path Scanning
- **Self-referential exclusion**: Scanner's own source files + test files excluded from fingerprint detection and `GetSourceCodeOnly`
- **Complement floor + diversity**: `MinimumComplementFloor(3)` with HARD/SOFT severity; `GetComplementCategory` for 10-category classification; backfill from soft-reject pool
- **Local directory scanning**: `IsLocalPath` multi-heuristic; `ScanLocalAsync` reads README/languages/manifests/git dates; `CloneOrUpdateAsync` passes through local paths
- **UI update**: Placeholders show 'GitHub URL or C:\path\to\project'
- **Tests**: 51 new — 543 total (541 pass, 2 skip)

## 2026-02-13 — Phase 20: Tighten Fingerprints, Filter Docs, Fix Evidence Formatting
- **Source-file filtering**: `DetectCapabilities` excludes .md, .txt, .yml, .json, .xml, .csproj, and 20+ non-source extensions
- **Tightened ~15 fingerprint patterns**: OpenTelemetry, Benchmark, Plugin, Authentication, Integration tests, Circuit breaker, Retry logic, Rate limiting, RAG, Embedding, FTS, DPAPI, Citation, Logging, Swagger — all require specific usage patterns
- **Clean evidence formatting**: `InjectProvenStrengths` produces 'Capability (verified in FileName.cs)' instead of raw regex
- **Anti-embellishment prompt rules**: 3 new LLM rules preventing embellishment, unlisted capability claims, and vague evidence
- **Tests**: 7 new — 492 total (490 pass, 2 skip)

## 2026-02-13 — Phase 19: Deterministic Fact Sheet Pipeline (Zero-Hallucination Repo Scans)
- **7-layer pre-analysis pipeline**: Package classification (active vs phantom, 30+ rules) → Capability fingerprinting (15+ regex patterns) → Diagnostic file checks → Type inference (app type, DB tech, test framework, ecosystem) → Post-scan verification (prune hallucinated gaps/strengths, inject proven ones, validate complement URLs)
- **New files**: `RepoFactSheetBuilder.cs` (~790 lines), `PostScanVerifier.cs` (~363 lines)
- **New models**: `RepoFactSheet`, `PackageEvidence`, `CapabilityFingerprint`
- **Prompt injection**: All 4 prompt builders include VERIFIED GROUND TRUTH section
- **Tests**: 44 new — 485 total (483 pass, 2 skip)

## 2026-02-13 — Phase 18: Agentic Timeout Fix, Cascade Removal, Ctrl+F Search
- **Timing fixes**: CodexCliService forwards `timeoutSeconds`; LlmService returns empty on agentic failure instead of cascading to 3 more 180s attempts
- **3-way agentic fallback**: Agentic → consolidated analysis → separate calls
- **Ctrl+F Find overlay**: Floating search bar with match counter, prev/next navigation, walks visual tree across TextBox + MarkdownViewer
- **Tests**: 439 total (437 pass, 2 skip)

## 2026-02-13 — Phase 17: Model Tiering, Agentic Codex, Infrastructure Hardening
- **ModelTier enum** (Default/Mini/Full) with provider-specific MiniModelMap
- **CodexMiniModel** (gpt-5.1-codex-mini) for routine tasks: CodeBook, gap verify, complements
- **GenerateAgenticAsync**: Single Codex 5.3 call with web search for full analysis
- **ILlmService, IRetrievalService, IBrowserSearchService** interfaces for testability
- **LlmCircuitBreaker**: Open/closed/half-open state machine per provider with exponential backoff + jitter
- **Structured logging**: `ILogger<T>` in pipeline-critical paths, Microsoft.Extensions.Logging added
- **Tests**: 26 new — 439 total (437 pass, 2 skip)
- **Consolidated analysis**: For cloud/Codex providers (`IsLargeContextProvider`), CodeBook + RAG Analysis + Gap Verification combined into 1 LLM call with self-verification, reducing 3 calls to 1
- **Intelligent routing**: `LlmService.IsLargeContextProvider` property determines pipeline mode — CloudOnly and CloudPrimary use consolidated, LocalOnly and LocalWithCloudFallback use separate calls
- **Consolidated prompt**: `BuildConsolidatedAnalysisPrompt` sends all 40 deduplicated chunks (18 RAG queries: 6 architecture + 12 analysis) in one prompt requesting CodeBook + Frameworks + Strengths + self-verified Gaps
- **Consolidated parser**: `ParseConsolidatedAnalysis` extracts all four sections from a single response, preserving ### subheadings within the CodeBook section
- **Separate pipeline preserved**: Ollama/local models still use 3 separate calls (CodeBook → Analysis → Gap Verification) optimized for small context windows

### Ollama Structured Output (JSON format enforcement)
- **`GenerateJsonAsync` method**: New LlmService method that adds `format: "json"` to Ollama API requests, enforcing valid JSON output from smaller models
- **JSON complement evaluation**: Complement prompts now request JSON output with explicit schema, parsed by `ParseJsonComplements` — eliminates the "5 logging libraries" problem
- **Fallback parsing**: JSON parsing attempted first; if it fails, falls back to existing markdown `ParseComplements` parser — backward compatible
- **Diversity enforcement**: JSON complement prompt explicitly requires category diversity and derives project names from URLs only

### Parallelism (5 sequential operations fixed)
- **Web search parallelism** (HIGH): `ComplementResearchService` web searches now parallel with `SemaphoreSlim(4)` — saves 24-64s for 10 topics
- **Web enrichment parallelism**: GitHub URL enrichment flattened to single `Task.WhenAll` across all topics (was sequential per topic)
- **CodeBook RAG parallelism**: 6 architecture queries now parallel via `Task.WhenAll` — saves 3-6s
- **Metadata scan parallelism**: 4 initial GitHub API calls (repo, languages, readme, root contents) now parallel — saves 2-3s
- **File indexing parallelism**: `RepoIndexService` file reading uses `Parallel.ForEachAsync` (max 8 threads) with `ConcurrentBag` — handles 100+ file repos efficiently
- **Multi-scan parallelism**: `RunMultiScanFusionAsync` scans repos in parallel with `SemaphoreSlim(2)` concurrency limit — N×60s → ~60s

### Tests
- 22 new tests: consolidated prompt builder (4), consolidated parser (5), JSON complement prompt/parser (5), IsLargeContextProvider routing (4), backward compatibility (3), real-world response (1)
- **Tests**: 391 → 413 (411 passed, 2 skipped, 0 failed)

## 2026-02-12 — Phase 15: Pipeline Telemetry, Framework Detection & Parallelism
- **LLM call tracking**: Full `ScanTelemetry` model — tracks every LLM call (purpose, model, duration, prompt/response length), phase timing, RAG query count, web search count, GitHub API call count
- **Pipeline instrumentation**: Every phase in `RepoIntelligenceJobRunner.RunAnalysisAsync` wrapped with `Stopwatch` timing; all 4 LLM calls individually timed
- **Telemetry in reports**: Generated report now includes `## Pipeline Telemetry` section with call table, phase breakdown, and totals
- **Telemetry in UI**: `FullProfileText` export includes pipeline summary; `AddReplay` posts telemetry to activity log
- **Executive summary**: Now includes pipeline stats: `"Pipeline: 4 LLM calls (12.3s) | 18 RAG queries | 7 web searches | 25 GitHub API calls | Total: 45.0s"`
- **Deterministic framework detection**: `DetectFrameworkHints` maps ~40 known packages → human-readable labels (.NET, React, Django, etc.) + parses `TargetFramework` from `.csproj`. Runs before LLM analysis so frameworks appear even with weak models
- **Framework deduplication**: `ParseAnalysis` skips LLM-suggested frameworks that overlap with deterministically-detected ones
- **RAG retrieval parallelism**: 12 analysis queries + N gap verification queries now fire via `Task.WhenAll` instead of sequential `foreach`
- **GitHub enrichment parallelism**: URL enrichment calls in `ComplementResearchService` now parallel per topic
- **Counter properties**: `RepoScannerService.LastScanApiCallCount`, `ComplementResearchService.LastSearchCallCount/LastEnrichCallCount/LastLlmDurationMs`
- **Tests**: 11 new — ScanTelemetry summary/defaults, LlmCallRecord fields, PhaseTimingRecord, DetectFrameworkHints (.NET/JS/Python/empty/csproj), ParseAnalysis dedup, RepoProfile.Telemetry default
- **Tests**: 380 → 391 (389 passed, 2 skipped, 0 failed)

## 2026-02-13 — Phase 14: Repo Scan Quality Fixes
- **Deep .csproj discovery**: Expanded `RepoScannerService` to recurse 2 levels into `src/` and `tests/` directories, fixing dependency=0 for standard .NET repo layouts (`src/ProjectName/ProjectName.csproj`)
- **Gap quality enforcement**: Added explicit prompt instructions distinguishing "missing capability" gaps (REAL) from "critique of existing feature" gaps (FALSE), with good/bad examples in both `AppendFormatInstructions` and `BuildGapVerificationPrompt`
- **Minimum 3-gap rule**: `VerifyGapsViaRag` now enforces ≥3 verified gaps — supplements with originals if LLM over-prunes, keeps all originals if verification returns 0
- **Verification system prompt fix**: Gaps about ABSENT things (no CI, no docs) are now explicitly marked REAL when no counter-evidence exists, preventing false-positive pruning of legitimate gaps
- **GitHub URL enrichment**: `ComplementResearchService` now fetches real descriptions, star counts, and licenses from GitHub API before LLM evaluation — eliminates hallucinated project names/descriptions
- **Anti-hallucination prompts**: Complement LLM prompt now explicitly forbids inventing project names, requires deriving names from URLs only
- **Improved search queries**: More targeted search template (`{lang} {topic} library github stars:>100`) replacing overly verbose previous query
- **Tests**: 380 total — 378 passed, 2 skipped, 0 failed

## 2026-02-12 — UI/WPF Documentation Overhaul
- **UI_WPF_SPEC.md**: Expanded from 14 lines to comprehensive spec (~600 lines) covering every view, tab, control, style, converter, ViewModel, and interaction
- Documents all 20 session workspace tabs, 24 sub-ViewModels, 10 value converters, 14 named styles, full color palette, keyboard shortcuts, tab visibility rules by domain pack, and UX patterns

## 2026-02-12 — Phase 13: Model Attribution + Complement Enforcement
- **Model attribution**: Every AI-generated output now tracks which LLM model produced it (`LlmResponse.ModelName`, `LastModelUsed` on LlmService)
- **Domain model fields**: `ResearchJob.ModelUsed`, `Report.ModelUsed`, `QaMessage.ModelUsed`, `RepoProfile.AnalysisModelUsed`
- **DB persistence**: Schema migration adds `model_used`/`analysis_model_used` columns to jobs, reports, qa_messages, repo_profiles tables
- **Full provider coverage**: Ollama (SynthesisModel), Anthropic, Gemini, OpenAI-compat (5 providers), Codex CLI — all pass model name through
- **Wired through callers**: ResearchJobRunner (16 LLM call sites), RepoIntelligenceJobRunner, ProjectFusionEngine, ComplementResearchService, Q&A
- **Minimum 5 complements**: ComplementResearchService now enforces ≥5 complement suggestions with general improvement categories as fallback
- **UI model display**: RepoProfileViewModel.AnalysisModel, QaMessageViewModel.ModelUsed, FullProfileText export includes model
- **Tests**: 23 new (ModelAttributionTests.cs) — LlmResponse model name, domain model fields, DB persistence round-trip, migration safety, complement parsing
- **Tests**: 357 → 378 (378 passed, 0 failed)

## 2026-02-12 — Phase 12: RAG-Grounded Repo Analysis
- **Pipeline redesign**: Scan(metadata only) → Clone+Index → CodeBook → RAG analysis(12 queries, 30 chunks) → Gap verification → Complements
- **Zero truncation**: Removed all README/manifest truncation; full content preserved and used via chunked retrieval
- **Gap verification**: Each gap claim checked against actual codebase via per-gap RAG queries; false positives pruned by LLM
- **RepoScannerService**: Split into metadata-only scan + shallow fallback; LLM analysis moved to runner post-indexing
- **Tests**: 16 new (RagGroundedAnalysisTests.cs) including self-scan simulation proving cloud providers, Hive Mind, notifications all captured
- **Tests**: 341 → 357 (355 passed, 2 skipped)

## 2026-02-12 — Phase 11: Polish, Curation & Health Monitoring
- **Step 1 PDF Ingestion**: PdfPig-based text extraction + per-page OCR fallback (< 50 char threshold); replaces broken BT/ET regex parser
- **Step 2 ViewModel Decomposition**: Refactored 2578-line SessionWorkspaceViewModel into 12 partial class files (~550-line root + 9 partials + SubViewModels)
- **Step 3 Hive Mind Curation UI**: GlobalDb pagination with source type/domain pack/session filters, browse/delete/paginate XAML controls
- **Step 4 Search Engine Health**: Per-engine health tracking (ConcurrentDictionary) in ResearchJobRunner, WrapPanel status cards in UI
- **Step 5 Job Completion Notifications**: P/Invoke FlashWindowEx + SystemSounds.Asterisk when jobs complete while app unfocused; AppSettings.NotificationsEnabled toggle
- **Step 6 Tests**: 14 new tests (GlobalDb curation, SearchEngineHealthEntry states, DeleteChunk, PdfExtractionResult)
- **Services**: 35 → 37 (+ PdfIngestionService, NotificationService)
- **Tests**: 327 → 341 (339 passed, 2 skipped)

## 2026-02-12 — Phase 10: Fix Generation + Repo RAG + Hive Mind
- **10a Fix Generation**: LlmResponse metadata, truncation detection + auto-retry, num_ctx/maxTokens fixes, sectional fusion rewrite
- **10b Repo RAG**: RepoCloneService, CodeChunker, RepoIndexService, CodeBookGenerator, RAG-powered repo Q&A, SessionDb migration (4 new columns)
- **10c Hive Mind**: GlobalDb (FTS5-indexed), GlobalMemoryService, strategy extraction, cross-session RAG, Hive Mind tab UI
- **Tests**: 24 new tests (CodeChunker, GlobalDb, LlmTruncation, SessionDbRepoProfile)
- **Docs**: CAPABILITY_MAP.md created, orchestrator enforcement rules added

## 2026-02-12 — Capability Map + Documentation Enforcement
- Created `CAPABILITY_MAP.md` — authoritative feature index (35 services, 22 tabs, 20+ DB tables, 327 tests)
- Added documentation enforcement rules to `agents/orchestrator.agent.md`
- Updated `PROJECT_CONTEXT.md` with current scope and scale
- Fixed stale "Global Search" reference in `EXAMPLES.md` → "Hive Mind"

## Pre-2026 — Milestones 1–9
- **M1**: Solution skeleton + Sessions Hub (WPF .NET 8 + MVVM, session CRUD, per-session SQLite)
- **M2**: ArtifactStore + Inbox + Notebook + FTS5 keyword search
- **M3**: SnapshotService + offline viewer + blocked/paywall detection + retry/courtesy
- **M4**: OCR captures + Citation model (4 types) + citation persistence
- **M5**: Embeddings (Ollama + trigram fallback) + hybrid retrieval + evidence panel
- **M6**: Agentic research jobs (8-state machine, checkpoint/pause/resume, 4 report types, multi-lane search, MSV + alternatives)
- **M7**: Discovery Studio (problem framing, idea cards, 5D scoring, novelty check)
- **M8**: Programming Research + IP (approach matrix, IP/license analysis, design-arounds)
- **M9**: Materials Explorer + Idea Fusion (4 modes) + packaging output

## Initial
- Autonomous project-generation pack initialized
- Domain capability packs: General, History/Philosophy, Math, Maker/Materials, Chemistry-Safe, Programming+IP, Repo Intelligence
- Safety system + IP awareness layer
- Reporting + Learning Replay requirements
