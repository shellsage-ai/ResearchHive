# Changelog

All changes are tracked in `CAPABILITY_MAP.md` (Change Log section) for granular file-level detail.
This file provides a high-level summary per milestone.

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
