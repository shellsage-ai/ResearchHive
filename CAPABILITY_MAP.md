# ResearchHive — Capability Map

> **Purpose**: Authoritative index of every implemented capability, mapped to source files, design rationale, and test coverage.
> This file is the single source of truth for "what exists, where, and why."
>
> **Maintenance rule**: Updated after every implementation step per `agents/orchestrator.agent.md` enforcement rules.
> **Last verified**: 2026-02-12 — 357 tests (355 passed, 2 skipped), 0 build errors.

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

- [x] LlmResponse metadata (WasTruncated, FinishReason) — Artifacts.cs (LlmResponse record), LlmService.cs
  WHY: Detect truncation from all providers; parse Anthropic stop_reason, Gemini finishReason, OpenAI finish_reason, Ollama done_reason
  TESTS: LlmTruncationTests.cs (5)

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

- [x] Complement research (find projects that fill gaps) — ComplementResearchService.cs
  WHY: After identifying a repo's gaps, automatically suggest complementary OSS projects

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

- [x] Project Fusion (4 goals: Merge, Extend, Compare, Architect × 6 templates) — ProjectFusionEngine.cs
  WHY: Fuse multiple scanned repos into unified architecture documents with provenance
  TESTS: RepoIntelligenceTests.cs

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
- [x] Value converters — ValueConverters.cs
- [x] Global styles — Styles.xaml

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

## 13. DI Registrations (ServiceRegistration.cs)

37 singleton services registered via `AddResearchHiveCore()` + App.xaml.cs:

| # | Service | Registration | Notes |
|---|---------|-------------|-------|
| 1 | AppSettings | Direct | |
| 2 | SecureKeyStore | Direct | |
| 3 | SessionManager | Direct | |
| 4 | CourtesyPolicy | Direct | |
| 5 | EmbeddingService | Direct | |
| 6 | CodexCliService | Direct | |
| 7 | LlmService | Factory lambda | Depends on AppSettings, SecureKeyStore, CodexCliService |
| 8 | ArtifactStore | Factory lambda | Depends on AppSettings |
| 9 | SnapshotService | Direct | |
| 10 | OcrService | Direct | |
| 11 | IndexService | Direct | |
| 12 | RetrievalService | Direct | |
| 13 | BrowserSearchService | Factory lambda | Depends on CourtesyPolicy, AppSettings |
| 14 | GoogleSearchService | Direct | |
| 15 | ResearchJobRunner | Factory lambda | 9 constructor params + GlobalMemory property injection |
| 16 | DiscoveryJobRunner | Direct | |
| 17 | ProgrammingJobRunner | Direct | |
| 18 | MaterialsJobRunner | Direct | |
| 19 | FusionJobRunner | Direct | |
| 20 | RepoScannerService | Direct | |
| 21 | ComplementResearchService | Direct | |
| 22 | RepoCloneService | Direct | |
| 23 | CodeChunker | Direct | |
| 24 | RepoIndexService | Direct | |
| 25 | CodeBookGenerator | Direct | |
| 26 | GlobalDb | Factory lambda | Depends on AppSettings.GlobalDbPath |
| 27 | GlobalMemoryService | Direct | |
| 28 | RepoIntelligenceJobRunner | Factory lambda | 7 constructor params + GlobalMemory property injection |
| 29 | ProjectFusionEngine | Direct | |
| 30 | ExportService | Direct | |
| 31 | InboxWatcher | Direct | |
| 32 | CrossSessionSearchService | Direct | |
| 33 | CitationVerificationService | Direct | |
| 34 | ContradictionDetector | Direct | |
| 35 | ResearchComparisonService | Direct | |
| 36 | PdfIngestionService | Direct | PDF text extraction + OCR fallback |
| 37 | NotificationService | App.xaml.cs | Taskbar flash + sound via P/Invoke |

---

## 14. Test Coverage

| Test File | Count | Covers |
|-----------|-------|--------|
| AiModelSelectionTests.cs | 18 | LlmService fallback, SecureKeyStore CRUD, routing strategy, cloud models, tool calling, AppSettings |
| AppSettingsTests.cs | — | AppSettings serialization + defaults |
| CodeChunkerTests.cs | 7 | C# / Markdown / Python / JSON chunking, empty/small files, index ordering |
| ExportServiceTests.cs | 7 | ZIP export, Markdown export, HTML export, research packet, blocked snapshot exclusion |
| FeatureTests.cs | — | Snapshot capture, inbox watcher, artifact store |
| GlobalDbTests.cs | 8 | Save, batch save, FTS search, source type filter, strategies, delete, session delete, embeddings |
| LlmTruncationTests.cs | 5 | LlmResponse record, equality, deconstruction, GlobalChunk defaults, MemoryScope enum |
| ModelTests.cs | — | Core model classes |
| NewFeatureTests.cs | — | Verification summary, new features |
| PipelineVsDirectComparisonTest.cs | — | Pipeline vs direct LLM comparison |
| PolishFeatureTests.cs | — | UI polish features |
| RepoIntelligenceTests.cs | 12 | RepoProfile CRUD, ProjectFusion CRUD, DomainPack enum, serialization |
| ResearchPipelineTests.cs | — | Full pipeline integration |
| SearchResultExtractorTests.cs | — | HTML extraction for all 6 search engines |
| SessionDbDeleteTests.cs | — | Cascade delete operations |
| SessionDbRepoProfileTests.cs | 3 | New repo profile fields round-trip, null defaults, update existing |
| SessionManagerTests.cs | — | Session CRUD operations |
| StreamlinedCodexTests.cs | — | Codex CLI integration |
| RagGroundedAnalysisTests.cs | 16 | RAG analysis prompt building (no truncation, all chunks/deps), gap verification parsing, self-scan simulation (cloud providers, tests, Hive Mind, notifications captured), false positive detection |

**Total: 357 tests — 355 passed, 2 skipped, 0 failures**

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
      PdfIngestionService.cs       ← NEW Phase 11: PDF text extraction + OCR fallback
      (36 service classes — see DI Registrations above)
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
    (20 test files — see Test Coverage above)
```

---

## Change Log

| Date | Change | Files |
|------|--------|-------|
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
