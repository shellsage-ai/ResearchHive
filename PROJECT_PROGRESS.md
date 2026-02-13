# ResearchHive — Project Progress

## Status
- Current milestone: All milestones (1-9) + Phases 10-25 complete
- Build status: ✅ PASSING (0 errors)
- Test baseline: 597 total — 597 passed, 2 skipped, 0 failures
- Services: 42 DI registrations (38 unique concrete services incl. interfaces + App.xaml.cs)
- Last verified: Full test suite green (Phase 25 commit `fadf193`)

## Build / Run Commands
```
dotnet build ResearchHive.sln
dotnet run --project src/ResearchHive/ResearchHive.csproj
dotnet publish src/ResearchHive/ResearchHive.csproj -c Release -o publish/ResearchHive_Package
```

## Completed Milestones

### Milestone 1 — Solution Skeleton + Sessions Hub ✅
- WPF .NET 8 solution with MVVM (CommunityToolkit.Mvvm)
- Sessions sidebar: create/list/search/tag + status + last-report preview
- Session workspace folder creation (8 subdirectories per spec)
- Session registry persistence (global SQLite registry.db)
- Per-session SQLite databases with full schema (16+ tables)

### Milestone 2 — ArtifactStore + Inbox + Notebook + FTS ✅
- Content-addressed ArtifactStore (SHA256 immutable store)
- InboxWatcher: FileSystemWatcher with auto-ingest and auto-index
- Notebook markdown notes (save/display/persist)
- FTS keyword search via SQLite FTS5

### Milestone 3 — SnapshotService + Sources Viewer ✅
- URL capture to snapshot bundles (HTML, text, metadata)
- Offline viewer (snapshot text/HTML display in UI)
- Blocked/paywall detection (403/451 + keyword scanning)
- Retry logic with CourtesyPolicy integration

### Milestone 4 — OCR Captures + Citation ✅
- Screenshot capture + OCR with bounding boxes (Windows.Media.Ocr via PowerShell)
- Citation model (4 types: WebSnapshot, Pdf, OcrImage, File)
- Citation persistence in per-session DB
- OCR text indexed and searchable

### Milestone 5 — Embeddings + Hybrid Retrieval + Evidence Panel ✅
- Embedding service (Ollama nomic-embed-text + trigram-hash fallback)
- Hybrid search: FTS keyword + semantic cosine similarity
- Configurable weights (0.6 semantic, 0.4 keyword)
- Evidence panel pin/unpin in UI

### Milestone 6 — Agentic Research Jobs + Reports + Replay ✅
- Persisted state machine (8 states: Planning→Completed/Failed)
- Checkpointing: state saved after each step, resume via ResumeAsync
- Pause/resume with CancellationToken
- 4 report types: Executive Summary, Full Report, Activity Log, Research Replay
- Multi-lane search: DuckDuckGo, Brave, Bing (HTML scraping, no API keys)
- "Most Supported View" + "Credible Alternatives / Broader Views" with citations
- Replay entries for step-by-step visualization

### Milestone 7 — Discovery Studio ✅
- Problem framing → Known map → Idea card generation
- Novelty sanity-check against existing research
- 5-dimension scoring (Novelty, Feasibility, Impact, Testability, Safety)
- Idea card UI with full details + export

### Milestone 8 — Programming Research + IP Studio ✅
- Multi-approach comparison matrix
- IP/License signal analysis per approach
- Risk flags (copyleft, patent, proprietary, viral licensing)
- Design-around generation
- Implementation plan (no verbatim code copying)

### Milestone 9 — Materials Explorer + Idea Fusion + Packaging ✅
- Property-based material search with filters and avoidance lists
- Safety labels: hazard level, PPE, hazards, environment, disposal
- Ranked candidates with fit scores and test checklists
- Idea Fusion: 4 modes (Blend, CrossApply, Substitute, Optimize)
- Provenance mapping traces each fused element to source
- Packaging output: publish/ResearchHive_Package/ with run.bat + README

### Phase 10 — Fix Generation + Repo RAG + Hive Mind ✅

**Phase 10a — Fix Generation (Steps 1-4)** ✅
- `AppSettings`: Added `LocalContextSize = 16384`, repo chunking params
- `LlmService`: Added `num_ctx` to Ollama calls, cloud `maxTokens` passthrough
- `LlmResponse` record: WasTruncated + FinishReason metadata from all providers
- `GenerateWithMetadataAsync`: Auto-retry on truncation (doubles token budget, caps at 8000)
- `ProjectFusionEngine`: Rewritten to outline-then-expand (1 outline + 8 parallel section calls)

**Phase 10b — Repo RAG (Steps 5-11)** ✅
- `RepoCloneService`: git clone --depth 1 with ZIP fallback, file discovery with extension whitelist
- `CodeChunker`: Regex-based code/doc splitting, class/method boundary detection, cross-language
- `RepoIndexService`: Clone → discover → chunk → embed → save pipeline with TreeSha cache invalidation
- `SessionDb`: 4 new columns on `repo_profiles` (code_book, tree_sha, indexed_file_count, indexed_chunk_count) with migration
- `RetrievalService`: Added `sourceTypeFilter` overload for hybrid search (repo_code/repo_doc filtering)
- `CodeBookGenerator`: 6 architecture queries → top 20 chunks → LLM-generated structured CodeBook
- `RepoIntelligenceJobRunner`: Wired indexing + CodeBook generation + RAG-powered Q&A on repos

**Phase 10c — Hive Mind / Global Memory (Steps 12-17)** ✅
- `GlobalDb`: SQLite global.db with `global_chunks` table + FTS5 full-text index + 3 column indexes
- `GlobalMemoryService`: Promote session chunks, extract strategies, cross-session RAG Q&A
- Strategy extraction: LLM distills "what worked / what to avoid" per job, stored as `source_type = "strategy"`
- `ResearchJobRunner`: Fire-and-forget strategy extraction on job completion
- DI wired in `ServiceRegistration.cs` with factory lambdas + property injection for GlobalMemory
- Hive Mind tab in UI: 🧠 header, Ask Q&A, Promote Session, stats display, legacy cross-session search preserved

**Tests** ✅ — 24 new tests across 4 files:
- `CodeChunkerTests` (7): C#, markdown, empty, small, Python, JSON, index ordering
- `GlobalDbTests` (8): save, batch, FTS search, filter by source type, strategies, delete, session delete, embeddings
- `LlmTruncationTests` (5): LlmResponse record, equality, deconstruction, GlobalChunk defaults, MemoryScope enum
- `SessionDbRepoProfileTests` (3): new fields round-trip, null defaults, update existing

### Phase 11 — Polish, Curation & Health Monitoring ✅

**Step 1 — PDF Ingestion** ✅
- `PdfIngestionService`: Two-tier extraction (PdfPig text layer + per-page OCR fallback at < 50 char threshold)
- `IndexService`: Replaced broken BT/ET regex parser with PdfIngestionService
- `SnapshotService`: PDF URL auto-detection + IngestPdfResponseAsync
- NuGet: Added PdfPig 0.1.13

**Step 2 — ViewModel Decomposition** ✅
- Decomposed 2578-line SessionWorkspaceViewModel into 12 files (root + 9 partials + SubViewModels)
- Used PowerShell extraction script for precise line-range splitting

**Step 3 — Hive Mind Curation UI** ✅
- `GlobalDb`: Added GetChunks (paginated, filtered), GetDistinctSourceTypes
- `GlobalMemoryService`: Added BrowseChunks, DeleteChunk, DeleteSessionChunks, GetSourceTypes
- XAML: Knowledge Curation card with filter, ListView, pagination, delete buttons

**Step 4 — Search Engine Health Monitoring** ✅
- `SearchEngineHealthEntry` model: EngineName, attempts/succeeded/failed, IsSkipped, computed StatusDisplay/StatusIcon
- `ResearchJobRunner`: ConcurrentDictionary per-engine tracking in SearchMultiLaneAsync
- XAML: WrapPanel of engine status cards

**Step 5 — Job Completion Notifications** ✅
- `NotificationService`: P/Invoke FlashWindowEx + SystemSounds.Asterisk for taskbar flash + sound
- Wired into RunResearch, Discovery, RepoScan completion paths
- Config: AppSettings.NotificationsEnabled (default: true)

**Step 6 — Tests** ✅ — 14 new tests in Phase11FeatureTests.cs:
- GlobalDb curation: pagination, source/domain/session filters, ordering, distinct types, delete (7)
- SearchEngineHealthEntry: Idle, Healthy, Degraded, Failed, Skipped states (5)
- PdfExtractionResult model shape (1)
- DeleteChunk with FTS cleanup (1)

### Phase 12 — RAG-Grounded Repo Analysis ✅
- Pipeline redesign: Scan(metadata only) → Clone+Index → CodeBook → RAG analysis(12 queries, 30 chunks) → Gap verification → Complements
- Zero truncation: Removed all README/manifest truncation; full content preserved via chunked retrieval
- Gap verification: Each gap claim checked against actual codebase via per-gap RAG queries; false positives pruned by LLM
- 16 new tests (RagGroundedAnalysisTests.cs) — 357 total

### Phase 13 — Model Attribution + Complement Enforcement ✅
- Model attribution: Every AI-generated output tracks which LLM model produced it (`LlmResponse.ModelName`, domain model fields, DB persistence)
- Full provider coverage: Ollama, Anthropic, Gemini, OpenAI-compat (5 providers), Codex CLI
- Minimum 5 complements enforced with fallback categories
- 23 new tests (ModelAttributionTests.cs) — 378 total

### Phase 14 — Repo Scan Quality Fixes ✅
- Deep .csproj discovery: Recurse 2 levels into `src/` and `tests/` directories
- Gap quality enforcement: Explicit prompt instructions distinguishing REAL (missing capability) from FALSE (critique of existing) gaps
- Minimum 3-gap rule, verification system prompt fix, GitHub URL enrichment, anti-hallucination prompts
- 380 total tests

### Phase 15 — Pipeline Telemetry, Framework Detection & Parallelism ✅
- Full `ScanTelemetry` model tracking every LLM call, phase timing, RAG/web/API counts
- Deterministic framework detection (`DetectFrameworkHints`): ~40 known packages → human-readable labels
- RAG retrieval parallelism, GitHub enrichment parallelism
- 11 new tests — 391 total

### Phase 16 — Smart Pipeline: Codex Consolidation, JSON Output, Parallelism ✅
- Codex call consolidation (4→2 LLM calls for cloud providers)
- Ollama structured JSON output (`GenerateJsonAsync`)
- 5 parallelism fixes (web search, enrichment, CodeBook RAG, metadata scan, multi-scan)
- 22 new tests — 413 total

### Phase 17 — Model Tiering, Agentic Codex, Infrastructure Hardening ✅
- `ModelTier` enum (Default/Mini/Full) with `MiniModelMap` per provider
- `GenerateAgenticAsync`: Single Codex call with web search for full analysis
- `ILlmService`, `IRetrievalService`, `IBrowserSearchService` interfaces
- `LlmCircuitBreaker`: Open/closed/half-open state machine with exponential backoff + jitter
- Structured logging (`ILogger<T>`) + Microsoft.Extensions.Logging
- 26 new tests — 439 total

### Phase 18 — Agentic Timeout Fix, Cascade Removal, Ctrl+F Search ✅
- Timeout forwarding fix for CodexCliService; remove cascade fallback (root cause of 9-min scans)
- 3-way agentic result handling with graceful fallback
- Ctrl+F Find overlay: floating search bar with match counter, prev/next, visual tree walk
- 439 total tests

### Phase 19 — Deterministic Fact Sheet Pipeline ✅
- 7-layer pre-analysis pipeline: Package classification → Capability fingerprinting → Diagnostics → Type inference → Post-scan verification
- `RepoFactSheetBuilder.cs` (~790 lines), `PostScanVerifier.cs` (~363 lines)
- `RepoFactSheet`, `PackageEvidence`, `CapabilityFingerprint` models
- 44 new tests — 485 total

### Phase 20 — Tighten Fingerprints, Filter Docs, Fix Evidence Formatting ✅
- Source-file filtering: Exclude .md/.txt/.yml/.json/.xml/.csproj from fingerprint scanning
- Tightened ~15 fingerprint patterns to require specific usage, not generic mentions
- Clean evidence formatting + anti-embellishment prompt rules
- 7 new tests — 492 total

### Phase 21 — Self-Referential Fix, Complement Diversity, Local Path Scanning ✅
- Exclude scanner's own source files + test files from fingerprint detection
- `MinimumComplementFloor(3)` with HARD/SOFT severity + category diversity enforcement
- `IsLocalPath` multi-heuristic + `ScanLocalAsync` for local directory scanning
- 51 new tests — 543 total

### Phase 22 — App-Type Gap Pruning, Active-Package Rejection, Domain-Aware Search ✅
- `PruneAppTypeInappropriateGaps`: Desktop→prune auth/Docker/middleware; DB contradiction→prune ORM
- Active package + wrong-DB hard rejection for complements
- `InferDomainSearchTopics()` derives queries from proven capabilities and app type
- `BuildJsonComplementPrompt` injects PROJECT CONTEXT block + 4 anti-hallucination rules
- 14 new tests — 557 total

### Phase 23 — Dockerfile Gap Fix, Meta-Project Filter, Project Discovery ✅
- `InjectConfirmedGaps` checks `GapsRemoved` before re-injecting
- `IsMetaProjectNotUsableDirectly` filter for infrastructure engines
- MinimumComplements 5→8, MinimumComplementFloor 3→5
- `GitHubDiscoveryService` + Project Discovery UI panel (search/filter/batch scan)
- Session navigation fix (clear SelectedSession on Settings/Home)
- 12 new tests — 567 total

### Phase 24 — Dynamic Anti-Hallucination Pipeline (4-Layer Filtering) ✅
- Layer 1: Expanded models + 5 dynamic inference methods
- Layer 2: Structured enrichment + 7 deterministic complement checks
- Layer 3: LLM relevance check
- Layer 4: Dynamic search topics (17 rules) + diverse categories
- 30 new + 7 updated tests — 597 total

### Phase 25 — Research Report Quality + Readability Overhaul ✅
- Fix 6 root causes of shallow reports: iteration cap (1→2), citation label collisions, early-exit thresholds, sufficiency skip in sectional mode, expert-level search queries, target sources (5→8)
- Report readability: All prompts instruct LLM to use **bold**, tables, blockquotes, `code`, structured lists
- `ReportTemplateService` section instructions updated with per-section formatting guidance
- 597 tests passing, 0 failures

## End-to-End Demo Checklist

| # | Check | Status |
|---|-------|--------|
| 1 | Sessions hub: create, persist, search by tag/title | ✅ |
| 2 | Evidence: snapshot URL + OCR screenshot (searchable) | ✅ |
| 3 | Retrieval: keyword + semantic + evidence pin/remove | ✅ |
| 4 | Polite browsing: concurrency, delays, backoff, caching | ✅ |
| 5 | Research job: run/pause/resume, 4 reports, MSV + alternatives | ✅ |
| 6 | Discovery Studio: idea cards + novelty + scoring + export | ✅ |
| 7 | Programming: approach matrix + IP + design-around | ✅ |
| 8 | Materials: property+filters → ranked + safety + export | ✅ |
| 9 | Fusion: combined proposal with provenance + export | ✅ |
| 10 | Packaging: portable directory with run instructions | ✅ |

## Assumptions Log

1. **Ollama optional**: App works without Ollama via deterministic fallbacks (trigram-hash embeddings, template-based synthesis). No paid API keys needed.

2. **Windows-only OCR**: Using Windows.Media.Ocr via PowerShell interop. Works on Windows 10/11 with language packs installed. Falls back gracefully if OCR unavailable.

3. **No Playwright dependency**: Removed to avoid requiring browser installation. Web snapshots use HttpClient with full HTML capture. JS-rendered pages may not be fully captured — users can OCR screenshots for those.

4. **PDF text extraction**: PdfPig 0.1.13 extracts text layer; pages with < 50 chars auto-fallback to OCR (likely scanned/image pages). Two-tier approach covers both native PDFs and scanned documents.

5. **SQLite per-session**: Each session is fully isolated with its own SQLite DB. Export = zip the folder. No external database server needed.

6. **Content-addressed artifacts**: SHA256-hashed immutable storage provides deduplication. Original filenames preserved in metadata.

7. **Multi-lane search via HTML scraping**: DuckDuckGo/Brave/Bing searched via HTML parsing (no API keys). Results may vary with page structure changes.

8. **Embedding dimensions**: Fallback trigram-hash embeddings use 384 dimensions. Ollama nomic-embed-text uses 768 dimensions. The system handles both transparently.

9. **Safety labels always present**: Materials Explorer always generates SafetyAssessment for every candidate. This is not optional.

10. **No verbatim code reproduction**: Programming Research explicitly instructs the LLM to avoid copying code and focus on approaches, standards, and original implementation plans.

## Open Risks / Questions
- **OCR approach**: Resolved — using Windows.Media.Ocr via PowerShell interop with bounding box support.
- **Embeddings strategy**: Resolved — Ollama nomic-embed-text with trigram-hash fallback. Per-session SQLite storage for chunks and embeddings.
- **Search lane stability**: HTML scraping of search engines may break if they change page structure. The system degrades gracefully (returns empty results for failed lanes).

## Configuration Locations
- App settings: `%LOCALAPPDATA%\ResearchHive\appsettings.json`
- Session data: `%LOCALAPPDATA%\ResearchHive\Sessions\<session_folder>\`
- Global registry: `%LOCALAPPDATA%\ResearchHive\registry.db`
- Global memory (Hive Mind): `%LOCALAPPDATA%\ResearchHive\global.db`
- Cloned repos: `%LOCALAPPDATA%\ResearchHive\repos\`
- Published package: `publish\ResearchHive_Package\`
