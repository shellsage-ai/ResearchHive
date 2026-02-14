# ResearchHive — Capability Map

> **Purpose**: Authoritative index of every implemented capability, mapped to source files, design rationale, and test coverage.
> This file is the single source of truth for "what exists, where, and why."
>
> **Maintenance rule**: Updated after every implementation step per `agents/orchestrator.agent.md` enforcement rules.
> **Last verified**: 2026-02-14 — 667 tests (667 passed, 0 failed), 0 build errors. Phase 32 — Report Export Quality.

---

## How to Read This File

```
- [x] Capability name — PrimaryFile.cs, SecondaryFile.cs
  WHY: One-line design rationale
  TESTS: TestFile.cs (count)
```

Status: `[x]` = implemented + tested | `[~]` = implemented, untested or partial | `[ ]` = planned / stub

---

## 1. Session Management

- [x] Session CRUD (create, list, search, filter, delete) — SessionManager.cs, RegistryDb.cs
  WHY: Sessions are the top-level organizational unit; registry.db stores the global index
  TESTS: SessionManagerTests.cs

- [x] Per-session SQLite database (20 tables) — SessionDb.cs
  WHY: Full isolation — each session is a self-contained DB; export = zip the folder
  TESTS: SessionDbDeleteTests.cs, SessionDbRepoProfileTests.cs (3)

- [x] Session status lifecycle (Active → Paused → Completed → Archived) — Session.cs
  WHY: State machine for job tracking and UI filtering

- [x] Tag-based filtering + text search on sidebar — SessionsSidebarViewModel.cs, SessionsSidebarView.xaml
  WHY: Fast navigation across dozens of sessions

- [x] 7 domain packs with per-pack tab visibility — Session.cs (DomainPack enum), SessionWorkspaceViewModel.cs (InitializeVisibleTabs)
  WHY: Each domain shows only relevant tabs — e.g., Materials hidden for Programming pack
  PACKS: GeneralResearch, HistoryPhilosophy, Math, MakerMaterials, ChemistrySafe, ProgrammingResearchIP, RepoIntelligence

---

## 2. Evidence Capture

- [x] URL snapshot capture (HTML + text + metadata) — SnapshotService.cs
  WHY: Immutable evidence — captured HTML can be re-read offline even if the site changes
  TESTS: FeatureTests.cs

- [x] Blocked/paywall detection (403/451 + keyword scanning) — SnapshotService.cs
  WHY: Don't index paywalled content that would produce garbage chunks

- [x] Screenshot capture + OCR with bounding boxes — OcrService.cs
  WHY: Capture non-textual evidence; Windows.Media.Ocr via PowerShell interop (Windows 10/11 only)

- [x] Content-addressed artifact store (SHA256 dedup) — ArtifactStore.cs
  WHY: Never store the same file twice; original filenames preserved in metadata

- [x] Inbox file watcher (auto-ingest dropped files) — InboxWatcher.cs
  WHY: Drag-and-drop workflow — drop a PDF/image into the session folder, auto-indexed
  TESTS: FeatureTests.cs

- [x] PDF ingestion with OCR fallback — PdfIngestionService.cs, IndexService.cs
  WHY: PdfPig extracts text layer; pages with < 50 chars auto-fall back to OcrService per page
  TESTS: Phase11FeatureTests.cs (PdfExtractionResult model)

- [x] Citation model (4 types: WebSnapshot, Pdf, OcrImage, File) — Artifacts.cs (Citation class), SessionDb.cs (citations table)
  WHY: Every claim must link to auditable evidence with type-specific metadata

---

## 3. Indexing & Retrieval

- [x] Text chunking (size + overlap configurable) — IndexService.cs
  WHY: Break documents into embeddable chunks; 500 chars / 50 overlap default
  CONFIG: AppSettings.DefaultChunkSize, DefaultChunkOverlap

- [x] Embedding service (Ollama nomic-embed-text + trigram-hash fallback) — EmbeddingService.cs
  WHY: Semantic search needs vectors; trigram-hash fallback works without Ollama (384-dim vs 768-dim)
  CONFIG: AppSettings.EmbeddingModel, EmbeddingConcurrency

- [x] Hybrid search (FTS5 keyword + cosine semantic, configurable weights) — RetrievalService.cs
  WHY: Neither keyword nor semantic search alone is sufficient; RRF merge gives best recall
  CONFIG: AppSettings.SemanticWeight (0.5), KeywordWeight (0.5), DefaultTopK (10)

- [x] Source type filtering on hybrid search — RetrievalService.cs (5-param HybridSearchAsync overload)
  WHY: Repo RAG needs to search only repo_code/repo_doc chunks, not session evidence

- [x] FTS5 full-text index per session — SessionDb.cs (fts_chunks virtual table)
  WHY: Fast keyword search across all session chunks without loading into memory

- [x] Report content search — RetrievalService.cs (SearchReportContentAsync)
  WHY: Search inside generated reports, not just evidence chunks

---

## 4. LLM Pipeline

- [x] Multi-provider routing (Ollama → Anthropic/Gemini/OpenAI/OpenRouter/DeepSeek/Groq/Mistral/Codex) — LlmService.cs
  WHY: Local-first with cloud fallback; 8 cloud providers for flexibility
  CONFIG: AppSettings.Routing (LocalWithCloudFallback | LocalOnly | CloudOnly | RoundRobin)

- [x] LlmResponse metadata (WasTruncated, FinishReason, ModelName) — Artifacts.cs (LlmResponse record), LlmService.cs
  WHY: Detect truncation from all providers; track which model generated each response for attribution
  TESTS: LlmTruncationTests.cs (5), ModelAttributionTests.cs (23)

- [x] Model attribution across all AI outputs — LlmService.cs (LastModelUsed), Jobs.cs (ModelUsed), DomainModels.cs (AnalysisModelUsed)
  WHY: Every AI-generated output tracks the model that produced it (Ollama/Anthropic/Gemini/OpenAI/Codex)
  TESTS: ModelAttributionTests.cs (23 — LlmResponse model name, domain model fields, DB persistence, migration safety)

- [x] Auto-retry on truncation (double token budget, cap 8K) — LlmService.cs (GenerateWithMetadataAsync → RouteGenerationAsync)
  WHY: Silently recover from truncated responses without caller awareness
  TESTS: LlmTruncationTests.cs

- [x] Ollama context window configuration — LlmService.cs (num_ctx = LocalContextSize)
  WHY: Ollama defaults to 2048 context which truncates; now configurable (default 16384)
  CONFIG: AppSettings.LocalContextSize

- [x] Cloud maxTokens passthrough — LlmService.cs (CallCloudWithMetadataAsync)
  WHY: Cloud providers were hardcoded to 4000 tokens; now caller-configurable

- [x] Tool calling (function-calling with LLM) — LlmService.cs (GenerateWithToolsAsync), ResearchTools.cs
  WHY: Let LLM decide which research tools to invoke during agentic loop
  CONFIG: AppSettings.EnableToolCalling, MaxToolCallsPerPhase

- [x] Codex CLI integration — CodexCliService.cs
  WHY: Alternative LLM path via OpenAI Codex CLI with OAuth
  CONFIG: AppSettings.CodexNodePath, CodexScriptPath, CodexModel, StreamlinedCodexMode

- [x] Model tiering (Default/Mini/Full) — LlmService.cs (ModelTier enum, MiniModelMap)
  WHY: Use cheaper/faster mini models for routine tasks (CodeBook, gap verify, complements); full models for analysis
  CONFIG: AppSettings.CodexMiniModel (gpt-5.1-codex-mini)
  TESTS: Phase17AgenticTests.cs

- [x] Agentic Codex full analysis — LlmService.cs (GenerateAgenticAsync), RepoIntelligenceJobRunner.cs (RunAgenticFullAnalysisAsync)
  WHY: Single Codex call with web search for complete repo analysis in one shot; 3-tier fallback (agentic → consolidated → separate)
  TESTS: Phase17AgenticTests.cs

- [x] LLM circuit breaker — LlmCircuitBreaker.cs
  WHY: Open/closed/half-open state machine per provider with exponential backoff + jitter (1s base, 2x factor, 8s cap, ±25%)
  TESTS: Phase17AgenticTests.cs

- [x] Structured logging — ILogger<T> across pipeline-critical paths
  WHY: Diagnostic visibility into LLM calls, failures, and circuit breaker state changes
  DEPENDS: Microsoft.Extensions.Logging

- [x] Service interfaces — ILlmService.cs, IRetrievalService.cs, IBrowserSearchService.cs
  WHY: Testability — mock LLM/retrieval/search in unit tests without real API calls

- [x] Secure API key storage (DPAPI-encrypted) — SecureKeyStore.cs
  WHY: Never store API keys in plaintext; DPAPI ties encryption to the Windows user
  CONFIG: AppSettings.KeySource (Direct | EnvironmentVariable), KeyEnvironmentVariable
  TESTS: AiModelSelectionTests.cs

---

## 5. Research Engine

- [x] Agentic research job runner (8-state machine: Planning→Searching→Capturing→Indexing→Analyzing→Synthesizing→Completed/Failed) — ResearchJobRunner.cs
  WHY: Long-running jobs need deterministic state machine with checkpointing
  TESTS: ResearchPipelineTests.cs

- [x] Checkpointing + pause/resume/cancel — ResearchJobRunner.cs (RunAsync, ResumeAsync, PauseJob, CancelJob)
  WHY: Research can take minutes; user must be able to pause and resume without data loss

- [x] Continue research (extend existing job with more sources) — ResearchJobRunner.cs (ContinueResearchAsync)
  WHY: User may want more coverage without re-running from scratch

- [x] Multi-lane web search (DuckDuckGo + Brave + Bing HTML scraping) — BrowserSearchService.cs, SearchResultExtractor.cs
  WHY: No API keys needed; HTML scraping across 3 engines for redundancy
  TESTS: SearchResultExtractorTests.cs

- [x] Google Search integration (Selenium-based) — GoogleSearchService.cs
  WHY: Higher quality results when available; separate from BrowserSearchService due to different driver model

- [x] Courtesy policy (rate limiting, circuit breaker, domain delays) — CourtesyPolicy.cs
  WHY: Polite browsing — per-domain concurrency limits, exponential backoff, circuit breaker after 5 failures
  CONFIG: AppSettings.MaxConcurrentFetches (6), MaxConcurrentPerDomain (2), MinDomainDelaySeconds (1.5), CircuitBreakerThreshold (5)

- [x] Source quality scoring — SourceQualityScorer.cs
  WHY: Rank sources by reliability tier (academic > official docs > news > blogs > unknown)
  CONFIG: AppSettings.SourceQualityRanking (on/off toggle)

- [x] Time-range filtering on searches — AppSettings.SearchTimeRange
  WHY: User can limit results to past day/week/month/year for freshness

- [x] Search engine health monitoring (per-engine status tracking) — ResearchJobRunner.cs, Jobs.cs (SearchEngineHealthEntry)
  WHY: Track per-engine query attempts/successes/failures/skip-detection during multi-lane search
  TESTS: Phase11FeatureTests.cs (5 — Idle/Healthy/Degraded/Failed/Skipped states)

- [x] "Most Supported View" + "Credible Alternatives" synthesis — ResearchJobRunner.cs
  WHY: Reports show consensus AND dissenting views with citations, not just majority opinion

- [x] Sectional report generation (template-based, per-section LLM) — ResearchJobRunner.cs (GenerateSectionalReportAsync), ReportTemplateService.cs
  WHY: Each section gets targeted evidence retrieval and focused prompt; parallel generation of independent sections
  CONFIG: AppSettings.SectionalReports (default: true)

- [x] Report formatting & highlighting — ResearchJobRunner.cs (BuildSynthesisPrompt, section prompts), ReportTemplateService.cs
  WHY: LLM instructed to use **bold** for key terms, tables for comparisons, blockquotes for takeaways, `code` for technical terms — all render natively in MarkdownViewer
  DEPENDS: MarkdownViewer.cs (supports bold, italic, tables, blockquotes, code inline)

- [x] Expert-level search query generation — ResearchJobRunner.cs (planPrompt)
  WHY: 5 targeted angles (comparative/architectural, quantitative/benchmark, decision-framework, case-study, expert/academic) instead of generic "diverse queries"

- [x] Stable sectional citation labels — ResearchJobRunner.cs (GetTargetedEvidenceForSectionAsync)
  WHY: Section-targeted search reuses master citation labels via SourceId fallback + offsets for truly-new sources; prevents label collisions across sections

- [x] Strategy extraction on job completion (fire-and-forget) — ResearchJobRunner.cs (TryExtractStrategyAsync)
  WHY: Learn from each research run — distill "what worked / what to avoid" for Hive Mind
  DEPENDS: GlobalMemoryService

---

## 6. Domain Pack Runners

- [x] Discovery Studio (problem framing → known map → idea cards → novelty check → 5D scoring) — DiscoveryJobRunner.cs
  WHY: Novel hypothesis generation with Novelty/Feasibility/Impact/Testability/Safety scoring

- [x] Programming Research & IP (approach matrix → IP/license analysis → design-arounds → implementation plan) — ProgrammingJobRunner.cs
  WHY: No verbatim code copying; focus on approaches, standards, and lawful design-arounds

- [x] Materials Explorer (property search → safety labels → ranked candidates → test checklists) — MaterialsJobRunner.cs
  WHY: Physical-world research needs hazard levels, PPE requirements, disposal protocols

- [x] Idea Fusion Engine (4 modes: Blend, CrossApply, Substitute, Optimize) — FusionJobRunner.cs
  WHY: Combine insights from evidence into novel proposals with provenance mapping

---

## 7. Repo Intelligence

- [x] GitHub repo scanning (metadata, README, deps, languages) — RepoScannerService.cs
  WHY: Parse actual manifests (package.json, .csproj, Cargo.toml, etc.) for ground-truth dependency data; no LLM analysis here — metadata only

- [x] RAG-grounded repo analysis (index-first, multi-query retrieval, gap verification) — RepoIntelligenceJobRunner.cs
  WHY: Strengths/gaps assessed AFTER deep indexing — 12 diverse RAG queries retrieve 30 top chunks, LLM analyzes actual code not truncated README; verified gaps checked per-gap against codebase
  TESTS: RagGroundedAnalysisTests.cs (16)

- [x] Complement research (≥5 suggestions, ranked by relevance) — ComplementResearchService.cs
  WHY: After identifying a repo's gaps, automatically suggest at least 5 complementary OSS projects; adds general improvement categories when gaps < 5
  TESTS: ModelAttributionTests.cs (complement parsing, MinimumComplements constant)

- [x] Repo analysis orchestrator (scan → complement → index → CodeBook → Q&A) — RepoIntelligenceJobRunner.cs
  WHY: Full pipeline from URL to interactive Q&A about any GitHub repo
  TESTS: RepoIntelligenceTests.cs

- [x] Git clone + file discovery — RepoCloneService.cs
  WHY: Shallow clone (depth 1) for code analysis; ZIP fallback if git unavailable
  CONFIG: AppSettings.RepoMaxFiles (200), RepoMaxFileSizeBytes (150K), RepoClonePath

- [x] Code chunking (regex class/method boundary detection) — CodeChunker.cs
  WHY: Language-agnostic splitting at semantic boundaries (class, method, function) without Roslyn dependency
  CONFIG: AppSettings.RepoChunkSize (800), RepoChunkOverlap (100)
  TESTS: CodeChunkerTests.cs (7)

- [x] Repo indexing pipeline (clone → discover → chunk → embed → save) — RepoIndexService.cs
  WHY: TreeSha-based cache invalidation — skip re-indexing if repo HEAD unchanged

- [x] CodeBook generation (6 architecture queries → top 20 chunks → structured doc) — CodeBookGenerator.cs
  WHY: LLM-generated architecture summary from actual code chunks, not just README

- [x] RAG-powered repo Q&A (hybrid search with source type filter) — RepoIntelligenceJobRunner.cs (AskAboutRepoAsync)
  WHY: Answer questions about any repo using indexed code + CodeBook as context

- [x] Deterministic fact sheet pipeline (7-layer, zero-hallucination) — RepoFactSheetBuilder.cs (~790 lines)
  WHY: Builds verified ground truth BEFORE LLM analysis: active vs phantom package classification (30+ rules), capability fingerprinting (15+ regex patterns), diagnostic file checks, type inference (app type, DB tech, test framework, ecosystem)
  TESTS: FactSheetAndVerifierTests.cs (44+)

- [x] Post-scan verification — PostScanVerifier.cs (~363 lines)
  WHY: Prunes hallucinated gaps/strengths contradicting fact sheet; validates complement URLs; injects proven strengths; prunes app-type-inappropriate gaps (desktop→auth/Docker/middleware, DB contradiction→ORM); rejects active-package complements
  TESTS: FactSheetAndVerifierTests.cs

- [x] Dynamic anti-hallucination pipeline (4-layer complement filtering) — ComplementResearchService.cs, PostScanVerifier.cs, RepoIntelligenceJobRunner.cs
  WHY: Layer 1 (expanded models + 5 inference methods) → Layer 2 (7 deterministic checks: archived, stale, low-stars, language mismatch, inapplicable concepts) → Layer 3 (LLM relevance check) → Layer 4 (17-rule dynamic search topics + 9 diverse categories)
  TESTS: FactSheetAndVerifierTests.cs

- [x] GitHub Project Discovery — GitHubDiscoveryService.cs, SessionWorkspaceViewModel.ProjectDiscovery.cs
  WHY: GitHub Search API integration for discovering relevant repos; search/language-filter/min-stars UI with batch scan
  TESTS: Phase 23 tests

- [x] Local directory scanning — RepoCloneService.cs (IsLocalPath, ScanLocalAsync)
  WHY: Scan local projects (C:\path\to\project) without GitHub; reads README, detects languages, finds manifests
  TESTS: Phase 21 tests

- [x] Pipeline telemetry — ScanTelemetry model, RepoIntelligenceJobRunner.cs
  WHY: Tracks every LLM call (purpose, model, duration), phase timing, RAG/web/API counts; displayed in reports and UI

- [x] Deterministic framework detection — RepoScannerService.cs (DetectFrameworkHints)
  WHY: Maps ~40 known packages → human-readable labels; runs before LLM analysis so frameworks appear even with weak models

- [x] Project Fusion (4 goals: Merge, Extend, Compare, Architect × 6 templates) — ProjectFusionEngine.cs
  - Anti-hallucination grounded (12 rules), identity-first outline generation, per-section identity reminder injection
  - PROJECTED_CAPABILITIES section: forward-looking capability predictions after fusion, goal-aware
  - Identity delineation: ╔══ PROJECT: name ══╗ markers, cross-attribution prevention (rules #11-12)
  - CodeBook limit 4000 chars (was 2500), ownership labels on architecture summaries
  WHY: Fuse multiple scanned repos into unified architecture documents with provenance
  TESTS: RepoIntelligenceTests.cs

- [x] Scan & Fusion cancellation — SessionWorkspaceViewModel.RepoIntelligence.cs, SessionWorkspaceViewModel.ProjectDiscovery.cs
  WHY: Users need to stop long-running scans/fusions without restarting the app; CancellationTokenSource per operation, confirmation dialog, OperationCanceledException handling
  UI: "✕ Cancel Scan" and "✕ Cancel Fusion" buttons (DangerButton style) visible during operations

- [x] Project Summary in scans — RepoScannerService.cs (## Summary section in all prompts)
  WHY: Concise 1-3 sentence project summary extracted during scan; answers "what is this project and what does it do"
  TESTS: SmartPipelineTests.cs, Phase17AgenticTests.cs

- [x] Identity Scan (Phase 2.25) — RepoScannerService.cs (RunIdentityScanAsync, GatherIdentityDocuments, BuildIdentityPrompt, ParseIdentityScanResponse)
  WHY: Dedicated scan phase focused exclusively on product-level identity (what the project IS), separate from code analysis; reads README, docs/, spec/, project briefs, entry points; produces ProductCategory + CoreCapabilities + enriched Summary; fills empty Description for local repos via ExtractFirstParagraph
  MODEL FIELDS: RepoProfile.ProductCategory, RepoProfile.CoreCapabilities
  UI: ProductCategory badge (amber) + CoreCapabilities list (purple) on scan cards

- [x] Scan anti-hallucination rules — RepoScannerService.cs (AppendFormatInstructions, BuildConsolidatedAnalysisPrompt, BuildFullAgenticPrompt)
  WHY: Prevents LLM from listing analysis tool/model as a repo strength, inventing licenses, or confusing project identities

- [x] Scan identity confusion fix (Phase 30) — RepoIntelligenceJobRunner.cs, RepoScannerService.cs, CodeBookGenerator.cs
  WHY: 8-step fix: wipe stale profiles before re-scan, source-ID filter on all RAG queries, identity context in analysis/gap-verification/CodeBook prompts, anti-confusion system prompt rules, AnalysisSummary + InfrastructureStrengths parsing
  TESTS: 597 total at commit

- [x] Strength grounding in PostScanVerifier (Phase 31) — PostScanVerifier.cs
  WHY: Deterministic replacement of overstatement patterns ("parallel RAG", "retry logic", etc.) with fact-sheet-grounded descriptions; `DeflateDescription` removes vague adjectives; `FindMatchingCapability` keyword-overlap matching
  TESTS: Phase31VerifierTests.cs (7 — deflation, grounding, no-op, infra, patterns)

- [x] Scan self-validation LLM pass (Phase 31) — RepoIntelligenceJobRunner.cs (SelfValidateStrengthsAsync)
  WHY: Mini-tier LLM cross-checks strengths against fact sheet evidence; parses ORIGINAL/CORRECTED pairs; catches remaining overstatements after deterministic grounding

- [x] FusionPostVerifier (Phase 31) — FusionPostVerifier.cs (~505 lines)
  WHY: Post-generation verifier for fusion output — 5 validators: fabricated tech rows, misattributed features, fabricated gap closures, orphaned provenance, LLM prose fact-check
  TESTS: Phase31VerifierTests.cs (15 — vocab builder, tech stack, feature matrix, gaps closed, provenance, integration, result summary)

- [x] Cross-section fusion consistency (Phase 31) — ProjectFusionEngine.cs (BuildConsistencyContext, ExpandSectionAsync)
  WHY: Batch 1 section decisions (identity, tech stack, feature attributions) injected into later batches to prevent contradictions

- [x] Export markdown depth-limit fix — ExportService.cs (SafeMarkdownToHtml, FlattenMarkdownNesting)
  WHY: Markdig throws "too deeply nested" on some LLM-generated markdown; pre-flattens 4+ level nesting, wraps all ToHtml calls with try/catch fallback
  TESTS: ExportServiceTests.cs

---

## 8. Hive Mind / Global Memory

- [x] Global SQLite database (global_chunks + FTS5 index) — GlobalDb.cs
  WHY: Cross-session knowledge store; persistent between sessions unlike per-session DBs
  CONFIG: AppSettings.GlobalDbPath
  TESTS: GlobalDbTests.cs (8)

- [x] Promote session chunks to global store — GlobalMemoryService.cs (PromoteSessionChunks, PromoteChunks)
  WHY: User-triggered — elevate best findings from a session to the global knowledge base

- [x] Strategy extraction via LLM — GlobalMemoryService.cs (ExtractAndSaveStrategyAsync)
  WHY: Distill reusable "what worked / what to avoid" meta-knowledge from completed jobs

- [x] Cross-session RAG Q&A (BM25 + semantic + RRF + strategy context) — GlobalMemoryService.cs (AskHiveMindAsync)
  WHY: Ask questions across ALL sessions — the global memory acts as a collective knowledge base

- [x] Hive Mind stats — GlobalMemoryService.cs (GetStats → HiveMindStats)
  WHY: Show user how much knowledge is in the global store (chunk count, strategy count, session count)

- [x] Knowledge curation UI (browse, filter, paginate, delete chunks) — GlobalDb.cs (GetChunks, GetDistinctSourceTypes), GlobalMemoryService.cs (BrowseChunks, DeleteChunk, DeleteSessionChunks)
  WHY: Users need to inspect, filter, and prune the global knowledge store
  TESTS: Phase11FeatureTests.cs (7 — pagination, source/domain/session filters, ordering, distinct types)

- [x] Legacy cross-session search (keyword search across all session DBs) — CrossSessionSearchService.cs
  WHY: Pre-Hive-Mind cross-session search; still available as fallback in Hive Mind tab

---

## 9. Reporting & Verification

- [x] 4 report types (Executive Summary, Full Report, Activity Log, Research Replay) — ResearchJobRunner.cs, ReportTemplateService.cs
  WHY: Different audiences need different depth; replay shows step-by-step research process

- [x] Sectional report generation (outline → expand per section) — ProjectFusionEngine.cs (RunAsync)
  WHY: Avoids single-call truncation; each section gets its own LLM call with focused context

- [x] Export to ZIP / Markdown / HTML / Research Packet — ExportService.cs
  WHY: Multiple export formats for sharing; research packet includes all evidence
  TESTS: ExportServiceTests.cs

- [x] Citation verification (quick + deep) — CitationVerificationService.cs
  WHY: Verify that claims in reports actually trace back to evidence in the session

- [x] Contradiction detection (quick + deep with LLM) — ContradictionDetector.cs
  WHY: Surface conflicting evidence so the user can resolve it

- [x] Research comparison (cross-job diff) — ResearchComparisonService.cs
  WHY: Compare two research runs on the same topic to see what changed

- [x] Application packaging (publishable output with run.bat) — ExportService.cs (PackageApplication)
  WHY: One-click portable distribution
  TESTS: ExportServiceTests.cs

---

## 10. UI Surface (WPF)

### Shell
- [x] Main window with sidebar + content area — MainWindow.xaml, MainViewModel.cs
- [x] Ollama health check (30s polling) — MainViewModel.cs
- [x] Session sidebar (create/list/search/filter) — SessionsSidebarView.xaml, SessionsSidebarViewModel.cs
- [x] Settings panel (all config editing, API keys, model listing) — SettingsView.xaml, SettingsViewModel.cs
- [x] Welcome screen — WelcomeView.xaml, WelcomeViewModel.cs
- [x] Markdown rendering control — MarkdownViewer.cs
  WHY: Custom FlowDocument-based Markdown renderer using Markdig; supports headings, bold, italic, tables, code blocks, blockquotes, links, code inline, thematic breaks
- [x] Value converters — ValueConverters.cs
- [x] Global styles — Styles.xaml

- [x] Ctrl+F Find overlay — FindOverlay.cs, SessionWorkspaceView.xaml
  WHY: Floating search bar with match counter, prev/next navigation; walks visual tree across TextBox + MarkdownViewer
  KEYBIND: Ctrl+F to open, Enter/Shift+Enter to cycle, Escape to close

- [x] Analysis model display — SessionWorkspaceView.xaml (RepoScan tab)
  WHY: Blue-bordered banner with bold 16px model name shown in each repo profile card

- [x] Job completion notifications (taskbar flash + system sound) — NotificationService.cs
  WHY: P/Invoke FlashWindowEx to flash taskbar + SystemSounds when research/discovery/repo-scan completes while app is unfocused
  CONFIG: AppSettings.NotificationsEnabled (default: true)

### ViewModel Decomposition (Phase 11)

SessionWorkspaceViewModel decomposed from 2578 lines into 12 files using partial classes:

| File | Lines | Responsibility |
|------|-------|----------------|
| SessionWorkspaceViewModel.cs (root) | ~550 | Fields, properties, constructor, InitializeVisibleTabs, LoadSessionData |
| .Research.cs | ~210 | RunResearch, progress handler, pause/resume/cancel |
| .Evidence.cs | ~190 | Sort, capture, search, pin/unpin evidence |
| .NotebookQa.cs | ~80 | AddNote, AskFollowUp |
| .Crud.cs | ~170 | 9 delete commands, IngestFile, HandleDroppedFiles |
| .DomainRunners.cs | ~200 | Discovery, Materials, Programming, Fusion runners |
| .RepoIntelligence.cs | ~200 | ScanRepo, MultiRepo, AskAboutRepo, ProjectFusion |
| .Export.cs | ~90 | Copy, export session/report/packet, view logs |
| .HiveMind.cs | ~150 | Search global, promote, stats, curation commands |
| .Verification.cs | ~160 | Citations, contradictions, compare, continue research |
| SessionWorkspaceSubViewModels.cs | ~650 | 27+ sub-ViewModel classes |

### 22 Workspace Tabs — SessionWorkspaceView.xaml, SessionWorkspaceViewModel.cs

| # | Tab | Tag | Category | Key Commands |
|---|-----|-----|----------|-------------|
| 1 | 📋 Overview | Overview | Core | Session metadata display |
| 2 | 🔬 Research | Research | Core | RunResearch, Pause, Resume, Continue, Cancel |
| 3 | 🌐 Snapshots | Snapshots | Core | CaptureSnapshot, delete, sort |
| 4 | 📷 OCR | OCR | Tools | CaptureScreenshot |
| 5 | 🔍 Evidence | Evidence | Core | SearchEvidence, Pin, Unpin, sort |
| 6 | 📓 Notebook | Notebook | Core | AddNote, MarkDirty, auto-save |
| 7 | 📊 Reports | Reports | Core | View/delete reports |
| 8 | 💬 Q&A | QA | Core | AskFollowUp (scoped to session or report) |
| 9 | ⏪ Replay | Replay | Core | Step-by-step job replay view |
| 10 | 💡 Discovery | Discovery | Analysis | RunDiscovery |
| 11 | 🧪 Materials | Materials | Tools | RunMaterialsSearch |
| 12 | 💻 Programming | Programming | Tools | RunProgrammingResearch |
| 13 | 🔗 Fusion | Fusion | Analysis | RunFusion (Idea Fusion, not Project Fusion) |
| 14 | 📦 Artifacts | Artifacts | Analysis | IngestFile, delete |
| 15 | 📜 Logs | Logs | Meta | ViewLogs |
| 16 | 📤 Export | Export | Meta | ExportSession, ExportReportHtml, ExportPacket |
| 17 | ✅ Verify | Verify | Analysis | VerifyCitations, DeepVerify |
| 18 | ⚡ Contradictions | Contradictions | Analysis | DetectContradictions, DeepDetect |
| 19 | 📈 Compare | Compare | Analysis | CompareResearch |
| 20 | 🧠 Hive Mind | GlobalSearch | Meta | AskHiveMind, PromoteSession, LoadStats |
| 21 | 🔎 Repo Scan | RepoScan | Tools | ScanRepo, ScanMulti, AskAboutRepo |
| 22 | 🏗️ Project Fusion | ProjectFusion | Analysis | RunProjectFusion |

### Tab Visibility per Domain Pack

| Tab | General | History | Math | Maker | Chemistry | Programming | Repo Intel |
|-----|---------|---------|------|-------|-----------|-------------|------------|
| OCR | ✅ | ✅ | ❌ | ✅ | ✅ | ❌ | ❌ |
| Materials | ❌ | ❌ | ❌ | ✅ | ✅ | ❌ | ❌ |
| Programming | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ✅ |
| Repo Scan | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ✅ |
| Project Fusion | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ✅ |
| All others | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

---

## 11. Data Layer

### SessionDb — 20 tables (per-session SQLite)

| Table | Purpose |
|-------|---------|
| artifacts | Content-addressed file store metadata |
| snapshots | URL captures (HTML, text, metadata, status) |
| captures | Screenshot + OCR captures |
| chunks | Text chunks with embeddings (for retrieval) |
| fts_chunks | FTS5 virtual table for keyword search |
| citations | Evidence citations (4 types) |
| jobs | Research job state machine |
| job_steps | Individual steps within a job |
| claim_ledger | Claims extracted from evidence |
| reports | Generated reports (4 types) |
| safety_assessments | Hazard/PPE/disposal labels |
| ip_assessments | IP/license risk analysis |
| idea_cards | Discovery Studio output |
| material_candidates | Materials Explorer results |
| fusion_results | Idea Fusion output |
| notebook_entries | User notes |
| qa_messages | Q&A conversation history |
| pinned_evidence | User-pinned evidence items |
| audit_log | Session activity log |
| repo_profiles | Scanned GitHub repo profiles (+ code_book, tree_sha, indexed_file_count, indexed_chunk_count) |
| project_fusions | Project Fusion architecture documents |

### GlobalDb — 2 tables (global.db)

| Table | Purpose |
|-------|---------|
| global_chunks | Cross-session knowledge chunks (with embeddings, source_type, domain_pack) |
| fts_global | FTS5 virtual table for BM25 search over global chunks |

### RegistryDb — 1 table (registry.db)

| Table | Purpose |
|-------|---------|
| sessions | Global session index (id, title, pack, status, path, timestamps) |

---

## 12. Configuration (AppSettings.cs)

| Group | Properties | Defaults |
|-------|-----------|----------|
| **Paths** | DataRootPath, SessionsPath, RegistryDbPath, GlobalDbPath, RepoClonePath | %LOCALAPPDATA%\ResearchHive\... |
| **LLM** | OllamaBaseUrl, EmbeddingModel, SynthesisModel, Routing, LocalContextSize | localhost:11434, nomic-embed-text, llama3.1:8b, LocalWithCloudFallback, 16384 |
| **Cloud** | UsePaidProvider, PaidProvider, PaidProviderApiKey/Endpoint/Model, KeySource, KeyEnvironmentVariable | false, None |
| **Codex** | CodexNodePath, CodexScriptPath, CodexModel, CodexEnableWebSearch, ChatGptPlusAuth, StreamlinedCodexMode | gpt-5.3-codex, true, CodexOAuth, true |
| **Search** | SourceQualityRanking, SearchTimeRange, SectionalReports | false, "any", true |
| **Notifications** | NotificationsEnabled | true |
| **Tool Calling** | EnableToolCalling, MaxToolCallsPerPhase | true, 10 |
| **Courtesy** | MaxConcurrentFetches, MaxConcurrentPerDomain, MaxBrowserContexts, MinDomainDelaySeconds, MaxDomainDelaySeconds, MaxRetries, BackoffBaseSeconds, CircuitBreakerThreshold, UserAgentString | 6, 2, 8, 1.5, 3.0, 3, 2.0, 5 |
| **Chunking** | DefaultChunkSize, DefaultChunkOverlap, DefaultTopK, SemanticWeight, KeywordWeight | 500, 50, 10, 0.5, 0.5 |
| **Repo** | RepoChunkSize, RepoChunkOverlap, RepoMaxFiles, RepoMaxFileSizeBytes, GitHubPat | 800, 100, 200, 150000 |
| **Embedding** | EmbeddingConcurrency | 4 |
| **Static** | KnownCloudModels | 8 providers × multiple models per provider |

---

## 13. DI Registrations (ServiceRegistration.cs + App.xaml.cs)

45 DI registrations (39 unique concrete services incl. interfaces + App.xaml.cs):

| # | Service | Registration | Notes |
|---|---------|-------------|-------|
| 1 | AppSettings | Direct | |
| 2 | SecureKeyStore | Factory lambda | Depends on AppSettings.DataRootPath |
| 3 | SessionManager | Direct | |
| 4 | CourtesyPolicy | Direct | |
| 5 | EmbeddingService | Direct | |
| 6 | CodexCliService | Direct | |
| 7 | LlmCircuitBreaker | Factory lambda | Phase 17: per-provider circuit breaker |
| 8 | LlmService | Factory lambda | Depends on AppSettings, SecureKeyStore, CodexCliService, LlmCircuitBreaker |
| 9 | ILlmService | Interface alias | → LlmService (Phase 17) |
| 10 | ArtifactStore | Factory lambda | Depends on AppSettings |
| 11 | SnapshotService | Direct | |
| 12 | OcrService | Direct | |
| 13 | PdfIngestionService | Direct | PDF text extraction + OCR fallback |
| 14 | IndexService | Direct | |
| 15 | RetrievalService | Direct | |
| 16 | IRetrievalService | Interface alias | → RetrievalService (Phase 17) |
| 17 | BrowserSearchService | Factory lambda | Depends on CourtesyPolicy, AppSettings |
| 18 | IBrowserSearchService | Interface alias | → BrowserSearchService (Phase 17) |
| 19 | GoogleSearchService | Direct | |
| 20 | ResearchJobRunner | Factory lambda | 9 constructor params + GlobalMemory property injection |
| 21 | DiscoveryJobRunner | Direct | |
| 22 | ProgrammingJobRunner | Direct | |
| 23 | MaterialsJobRunner | Direct | |
| 24 | FusionJobRunner | Direct | |
| 25 | RepoScannerService | Direct | |
| 26 | ComplementResearchService | Factory lambda | Phase 24: depends on LlmService, AppSettings, LlmCircuitBreaker, ILogger |
| 27 | RepoCloneService | Direct | |
| 28 | CodeChunker | Direct | |
| 29 | RepoIndexService | Direct | |
| 30 | CodeBookGenerator | Factory lambda | Depends on LlmService, RetrievalService, AppSettings |
| 31 | RepoFactSheetBuilder | Direct | Phase 19: deterministic fact sheet pipeline |
| 32 | PostScanVerifier | Factory lambda | Phase 19: depends on LlmService, ComplementResearchService, AppSettings |
| 33 | GlobalDb | Factory lambda | Depends on AppSettings.GlobalDbPath |
| 34 | GlobalMemoryService | Direct | |
| 35 | RepoIntelligenceJobRunner | Factory lambda | 7+ constructor params + GlobalMemory property injection |
| 36 | ProjectFusionEngine | Factory lambda | Phase 31: depends on SessionManager, LlmService, FusionPostVerifier |
| 37 | ExportService | Direct | |
| 38 | InboxWatcher | Direct | |
| 39 | CrossSessionSearchService | Direct | |
| 40 | CitationVerificationService | Direct | |
| 41 | ContradictionDetector | Direct | |
| 42 | ResearchComparisonService | Direct | |
| 43 | FusionPostVerifier | Factory lambda | Phase 31: depends on ILogger, ILlmService |
| — | **App.xaml.cs registrations:** | | |
| 44 | NotificationService | App.xaml.cs | Taskbar flash + sound via P/Invoke |
| 45 | GitHubDiscoveryService | App.xaml.cs | Phase 23: GitHub Search API |

---

## 14. Test Coverage

| Test File | Count | Covers |
|-----------|-------|--------|
| AiModelSelectionTests.cs | 18 | LlmService fallback, SecureKeyStore CRUD, routing strategy, cloud models, tool calling, AppSettings |
| AppSettingsTests.cs | — | AppSettings serialization + defaults |
| CodeChunkerTests.cs | 7 | C# / Markdown / Python / JSON chunking, empty/small files, index ordering |
| ExportServiceTests.cs | 7 | ZIP export, Markdown export, HTML export, research packet, blocked snapshot exclusion |
| FactSheetAndVerifierTests.cs | 130+ | RepoFactSheet models, package classification, capability fingerprinting, source-file filtering, post-scan verification, gap pruning, complement rejection, domain search topics, anti-hallucination layers 1-4, dynamic inference |
| FeatureTests.cs | — | Snapshot capture, inbox watcher, artifact store |
| GlobalDbTests.cs | 8 | Save, batch save, FTS search, source type filter, strategies, delete, session delete, embeddings |
| LlmTruncationTests.cs | 5 | LlmResponse record, equality, deconstruction, GlobalChunk defaults, MemoryScope enum |
| ModelAttributionTests.cs | 23 | LlmResponse.ModelName (all providers), domain model fields, DB persistence, migration safety, complement parsing |
| ModelTests.cs | — | Core model classes |
| NewFeatureTests.cs | — | Verification summary, new features |
| Phase11FeatureTests.cs | 14 | GlobalDb curation (7), SearchEngineHealthEntry states (5), PdfExtractionResult, DeleteChunk |
| Phase17AgenticTests.cs | 26 | ModelTier enum, MiniModelMap, agentic prompt building/parsing, circuit breaker, interface registrations, ILogger |
| PipelineVsDirectComparisonTest.cs | 2 (skip) | Pipeline vs direct LLM comparison (requires live Ollama) |
| PolishFeatureTests.cs | — | UI polish features |
| RagGroundedAnalysisTests.cs | 16 | RAG analysis prompt building, gap verification parsing, self-scan simulation, false positive detection |
| RepoIntelligenceTests.cs | 12 | RepoProfile CRUD, ProjectFusion CRUD, DomainPack enum, serialization |
| ResearchPipelineTests.cs | — | Full pipeline integration |
| SearchResultExtractorTests.cs | — | HTML extraction for all 6 search engines |
| SessionDbDeleteTests.cs | — | Cascade delete operations |
| SessionDbRepoProfileTests.cs | 3 | New repo profile fields round-trip, null defaults, update existing |
| SessionManagerTests.cs | — | Session CRUD operations |
| SmartPipelineTests.cs | 22 | Consolidated prompt builder/parser, JSON complement prompt/parser, IsLargeContextProvider routing |
| StreamlinedCodexTests.cs | — | Codex CLI integration |
| Phase31VerifierTests.cs | 35 | FusionPostVerifier (BuildProjectVocabulary, ValidateTechStackTable, ValidateFeatureMatrix, ValidateGapsClosed, ValidateProvenance, VerifyAsync integration, FusionVerificationResult summary), PostScanVerifier (DeflateDescription 11 cases, GroundStrengthDescriptions 5 cases) |

| Phase31VerifierTests.cs | 35 | FusionPostVerifier (BuildProjectVocabulary, ValidateTechStackTable, ValidateFeatureMatrix, ValidateGapsClosed, ValidateProvenance, VerifyAsync integration, FusionVerificationResult summary), PostScanVerifier (DeflateDescription 11 cases, GroundStrengthDescriptions 5 cases) |

**Total: 639 tests — 639 passed, 0 failed**

---

## 15. File Layout

```
src/
  ResearchHive.Core/           ← Class library (.NET 8)
    Configuration/
      AppSettings.cs           ← All config properties + enums
    Data/
      GlobalDb.cs              ← Global Hive Mind SQLite (327 lines)
      RegistryDb.cs            ← Session registry SQLite (127 lines)
      SessionDb.cs             ← Per-session SQLite (1456 lines, 20 tables)
    Models/
      Artifacts.cs             ← Artifact, Snapshot, Capture, Chunk, Citation, LlmResponse, GlobalChunk
      DomainModels.cs          ← Safety, IP, IdeaCard, Materials, Programming, Fusion, Repo, Notebook, HiveMindStats
      Jobs.cs                  ← JobType, JobState, ResearchJob, JobStep, JobProgressEventArgs
      Session.cs               ← Session, SessionStatus, DomainPack enums
    Services/
      PdfIngestionService.cs       ← Phase 11: PDF text extraction + OCR fallback
      RepoFactSheetBuilder.cs      ← Phase 19: deterministic fact sheet pipeline (~790 lines)
      PostScanVerifier.cs          ← Phase 19+31: LLM output validation + strength grounding (~1144 lines)
      FusionPostVerifier.cs          ← Phase 31: fusion output verification (~505 lines)
      LlmCircuitBreaker.cs        ← Phase 17: per-provider circuit breaker
      GitHubDiscoveryService.cs    ← Phase 23: GitHub Search API for project discovery
      ReportTemplateService.cs     ← Phase 25: section templates with formatting guidance
      (38+ service classes — see DI Registrations above)
  ResearchHive/                ← WPF app (.NET 8, net8.0-windows)
    Controls/
      MarkdownViewer.cs
    Converters/
      ValueConverters.cs
    Resources/
      Styles.xaml
    Services/
      DialogService.cs
    ViewModels/
      MainViewModel.cs
      SessionsSidebarViewModel.cs
      SessionWorkspaceViewModel.cs  ← ~550 lines (root partial; decomposed into 10 partial files)
      SessionWorkspaceViewModel.Research.cs
      SessionWorkspaceViewModel.Evidence.cs
      SessionWorkspaceViewModel.NotebookQa.cs
      SessionWorkspaceViewModel.Crud.cs
      SessionWorkspaceViewModel.DomainRunners.cs
      SessionWorkspaceViewModel.RepoIntelligence.cs
      SessionWorkspaceViewModel.Export.cs
      SessionWorkspaceViewModel.HiveMind.cs
      SessionWorkspaceViewModel.Verification.cs
      SessionWorkspaceViewModel.ProjectDiscovery.cs  ← Phase 23: GitHub project discovery
      SessionWorkspaceSubViewModels.cs
      SettingsViewModel.cs
      ViewModelFactory.cs
      WelcomeViewModel.cs
    Views/
      SessionsSidebarView.xaml
      SessionWorkspaceView.xaml     ← All 22 workspace tabs
      SettingsView.xaml
      WelcomeView.xaml
tests/
  ResearchHive.Tests/          ← xUnit + FluentAssertions + Moq
    (23 test files — see Test Coverage above)
```

---

## Change Log

| Date | Change | Files |
|------|--------|-------|
| 2026-02-14 | Phase 32: Report Export Quality — 7-step fix: (1) GitHub API logging (ILogger in RepoScannerService, 403/401/general error logging with rate-limit hints), (2) Setext→ATX conversion (ConvertSetextToAtx preprocessor + prompt rule), (3) Projected Capabilities bullet-only (prompt + ParseList prose filter), (4) Gaps Closed logical connection (prompt + FusionPostVerifier bullet validation), (5) Strength coverage increase (5→10-15), (6) Complement description guard, (7) Framework dedup (WPF/WinForms StartsWith guard). 16 new tests. | RepoScannerService.cs, ExportService.cs, ProjectFusionEngine.cs, FusionPostVerifier.cs, Phase32ReportQualityTests.cs (NEW), ExportServiceTests.cs, RagGroundedAnalysisTests.cs |
| 2026-02-13 | Phase 31: Anti-hallucination hardening — 6-step implementation: (1) strength grounding in PostScanVerifier (GroundStrengthDescriptions, DeflateDescription, FindMatchingCapability, OverstatementPatterns), (2) scan self-validation LLM pass (SelfValidateStrengthsAsync), (3) FusionPostVerifier (5 validators: tech stack, features, gaps, provenance, prose), (4) wired into DI + ProjectFusionEngine, (5) cross-section consistency (BuildConsistencyContext, priorSectionContext, tightened UNIFIED_VISION/ARCHITECTURE guidance), (6) prompt precision rules. Bug fix: ValidateFeatureMatrix table parsing order. 35 new tests. | PostScanVerifier.cs, RepoIntelligenceJobRunner.cs, FusionPostVerifier.cs (NEW), ProjectFusionEngine.cs, ServiceRegistration.cs, RepoScannerService.cs, Phase31VerifierTests.cs (NEW) |
| 2026-02-13 | Export depth-limit fix — SafeMarkdownToHtml + FlattenMarkdownNesting wraps all 3 Markdig.Markdown.ToHtml() calls; pre-flattens 4+ level nesting | ExportService.cs |
| 2026-02-13 | Phase 30: Scan identity confusion fix — 8-step: wipe stale profiles, source-ID filter on RAG, identity context in analysis/gap/CodeBook prompts, anti-confusion system prompt, AnalysisSummary + InfrastructureStrengths fields | RepoIntelligenceJobRunner.cs, RepoScannerService.cs, CodeBookGenerator.cs, DomainModels.cs |
| 2026-02-14 | Phase 29: Identity Scan — dedicated pipeline phase (2.25) for product-level identity, separate from code analysis; RunIdentityScanAsync reads README/docs/spec/project briefs/entry points (6000 char cap), 1 focused LLM call (~800 tokens), produces ProductCategory + CoreCapabilities; fills empty Description for local repos via deterministic first-paragraph extraction; FormatProfileForLlm enriched with identity fields; scan report includes identity; XAML UI: ProductCategory badge + CoreCapabilities list on scan cards | RepoScannerService.cs, DomainModels.cs, RepoIntelligenceJobRunner.cs, ProjectFusionEngine.cs, SessionWorkspaceSubViewModels.cs, SessionWorkspaceView.xaml |
| 2026-02-13 | Add PolyForm Noncommercial 1.0.0 license + commercial licensing guide + README update | LICENSE.md, COMMERCIAL_LICENSE.md, README.md |
| 2026-02-13 | Phase 28: Fusion identity grounding + scan/fusion cancellation — 12 grounding rules (added #11 cross-attribution, #12 distinct purpose), identity-first outline prompt (2-step: verify then outline), per-section identity reminder injection, ╔══ PROJECT ══╗ markers in FormatProfileForLlm, CodeBook limit 2500→4000, AnalysisModelUsed in profile, stronger Summary prompts in all 4 scan paths, CancellationTokenSource for scan/fusion/discovery, Cancel UI buttons with confirmation dialogs | ProjectFusionEngine.cs, RepoScannerService.cs, SessionWorkspaceViewModel.RepoIntelligence.cs, SessionWorkspaceViewModel.ProjectDiscovery.cs, SessionWorkspaceViewModel.cs, SessionWorkspaceView.xaml |
| 2026-02-14 | Phase 27: Scan & Fusion quality overhaul — ProjectSummary field (## Summary in all 4 scan prompts + parsers), ProjectedCapabilities (PROJECTED_CAPABILITIES fusion section, goal-aware), anti-hallucination rules in all scan prompts, 3 new fusion grounding rules, UI panels for both new fields, test assertions for Summary parsing | DomainModels.cs, RepoScannerService.cs, ProjectFusionEngine.cs, RepoIntelligenceJobRunner.cs, SessionWorkspaceSubViewModels.cs, SessionWorkspaceView.xaml, SmartPipelineTests.cs, Phase17AgenticTests.cs |
| 2026-02-14 | Phase 26: Project Fusion quality overhaul — anti-hallucination grounding (7 rules), comprehensive FormatProfileForLlm (deps+versions, Topics, file tree, CodeBook), new PROJECT_IDENTITIES section, goal-specific section prompts (Merge/Extend/Compare/Architect), Compare mode differentiation (goal-aware section titles), source URL references in reports, enriched UI (goal descriptions, dynamic labels, template descriptions) | ProjectFusionEngine.cs, DomainModels.cs, SessionWorkspaceSubViewModels.cs, SessionWorkspaceView.xaml |
| 2026-02-13 | Phase 25: Research report quality + readability overhaul — 6 pipeline bug fixes (iteration cap, citation label collisions, early-exit thresholds, sufficiency skip, expert search queries, target sources 5→8) + formatting/highlighting in all prompts | ResearchJobRunner.cs, ReportTemplateService.cs, SessionWorkspaceViewModel.cs |
| 2026-02-13 | Phase 24: Dynamic anti-hallucination pipeline — 4-layer complement filtering (expanded models, 7 deterministic checks, LLM relevance, 17-rule dynamic search) | RepoFactSheetBuilder.cs, PostScanVerifier.cs, ComplementResearchService.cs, RepoIntelligenceJobRunner.cs, DomainModels.cs, ServiceRegistration.cs |
| 2026-02-13 | Phase 23: Dockerfile gap fix, meta-project filter, Project Discovery panel, session nav fix | PostScanVerifier.cs, GitHubDiscoveryService.cs, SessionWorkspaceViewModel.ProjectDiscovery.cs, SessionWorkspaceView.xaml, MainViewModel.cs |
| 2026-02-13 | Phase 22: App-type gap pruning, active-package rejection, domain-aware search | PostScanVerifier.cs, ComplementResearchService.cs, RepoScannerService.cs |
| 2026-02-13 | Phase 21: Self-referential fix, complement diversity, local path scanning | RepoFactSheetBuilder.cs, PostScanVerifier.cs, ComplementResearchService.cs, RepoCloneService.cs, SessionWorkspaceViewModel.RepoIntelligence.cs |
| 2026-02-13 | Phase 20: Tighten fingerprints, filter docs, fix evidence formatting | RepoFactSheetBuilder.cs, PostScanVerifier.cs, RepoScannerService.cs |
| 2026-02-13 | Phase 19: Deterministic fact sheet pipeline — RepoFactSheetBuilder + PostScanVerifier + 44 tests | RepoFactSheetBuilder.cs, PostScanVerifier.cs, DomainModels.cs, RepoIntelligenceJobRunner.cs, RepoScannerService.cs, ServiceRegistration.cs, FactSheetAndVerifierTests.cs |
| 2026-02-13 | Phase 18: Agentic timeout fix, cascade removal, Ctrl+F Find overlay | CodexCliService.cs, LlmService.cs, RepoIntelligenceJobRunner.cs, FindOverlay.cs, SessionWorkspaceView.xaml |
| 2026-02-13 | Phase 17: Model tiering, agentic Codex, infrastructure hardening — interfaces, circuit breaker, structured logging | LlmService.cs, LlmCircuitBreaker.cs, ILlmService.cs, IRetrievalService.cs, IBrowserSearchService.cs, ServiceRegistration.cs, Phase17AgenticTests.cs |
| 2026-02-13 | Batch scan fix: continue on individual failure, feedback on empty input, per-scan notifications, LoadSessionData on error, 12 new tests (651 total) | SessionWorkspaceViewModel.RepoIntelligence.cs, RepoIntelligenceTests.cs, CONTEXT_TRANSFER.md |
| 2026-02-13 | Phase 16: Smart Pipeline — Codex consolidation (4→2 calls), Ollama JSON format, 5 parallelism fixes | LlmService.cs, ResearchJobRunner.cs, RepoScannerService.cs, ComplementResearchService.cs, SmartPipelineTests.cs |
| 2026-02-13 | Phase 15: Pipeline telemetry, framework detection, RAG parallelism | RepoIntelligenceJobRunner.cs, RepoScannerService.cs, DomainModels.cs |
| 2026-02-13 | Phase 14: Repo scan quality fixes — deep .csproj, gap quality, GitHub enrichment | RepoScannerService.cs, ComplementResearchService.cs, RepoIntelligenceJobRunner.cs |
| 2026-02-12 | Phase 13: Model attribution + complement enforcement — LlmResponse.ModelName, LastModelUsed, domain model fields, DB migration, ≥5 complements | Artifacts.cs, LlmService.cs, Jobs.cs, DomainModels.cs, SessionDb.cs, ResearchJobRunner.cs, RepoIntelligenceJobRunner.cs, ProjectFusionEngine.cs, ComplementResearchService.cs, SessionWorkspaceViewModel.NotebookQa.cs, SessionWorkspaceSubViewModels.cs |
| 2026-02-12 | 23 new tests: ModelAttributionTests (LlmResponse model, domain fields, DB persistence, complement parsing) | ModelAttributionTests.cs |
| 2026-02-12 | CAPABILITY_MAP.md created; enforcement rules added to orchestrator agent | CAPABILITY_MAP.md, orchestrator.agent.md |
| 2026-02-12 | Phase 10c: Hive Mind / Global Memory — GlobalDb, GlobalMemoryService, strategy extraction, Hive Mind tab UI | GlobalDb.cs, GlobalMemoryService.cs, ResearchJobRunner.cs, ServiceRegistration.cs, SessionWorkspaceView.xaml, SessionWorkspaceViewModel.cs, ViewModelFactory.cs |
| 2026-02-12 | Phase 10b: Repo RAG — cloning, chunking, indexing, CodeBook generation, RAG Q&A | RepoCloneService.cs, CodeChunker.cs, RepoIndexService.cs, CodeBookGenerator.cs, SessionDb.cs, RetrievalService.cs, RepoIntelligenceJobRunner.cs |
| 2026-02-12 | Phase 10a: Fix Generation — LlmResponse, truncation detection, auto-retry, num_ctx, sectional fusion | LlmService.cs, Artifacts.cs, DomainModels.cs, AppSettings.cs, ProjectFusionEngine.cs |
| 2026-02-12 | 24 new tests: CodeChunkerTests, GlobalDbTests, LlmTruncationTests, SessionDbRepoProfileTests | tests/ |
| 2026-02-12 | Phase 11 Step 1: PDF ingestion — PdfPig + OCR fallback per page | PdfIngestionService.cs, IndexService.cs, OcrService.cs, SnapshotService.cs, ServiceRegistration.cs |
| 2026-02-12 | Phase 11 Step 2: ViewModel decomposition — 2578→12 files via partial classes | SessionWorkspaceViewModel*.cs, SessionWorkspaceSubViewModels.cs |
| 2026-02-12 | Phase 11 Step 3: Hive Mind curation UI — browse/filter/paginate/delete global chunks | GlobalDb.cs, GlobalMemoryService.cs, SessionWorkspaceView.xaml, SessionWorkspaceViewModel.HiveMind.cs |
| 2026-02-12 | Phase 11 Step 4: Search engine health monitoring — per-engine status tracking + UI cards | Jobs.cs, ResearchJobRunner.cs, SessionWorkspaceView.xaml, SessionWorkspaceSubViewModels.cs |
| 2026-02-12 | Phase 11 Step 5: Job completion notifications — P/Invoke FlashWindowEx + SystemSounds | NotificationService.cs, AppSettings.cs, App.xaml.cs, ViewModelFactory.cs |
| 2026-02-12 | Phase 11 Step 6: Tests for Phase 11 features (14 new tests) | Phase11FeatureTests.cs |
| Pre-2026 | Milestones 1-9 complete (Sessions, Evidence, Retrieval, Research, Discovery, Programming, Materials, Fusion, Packaging) | Full codebase |
