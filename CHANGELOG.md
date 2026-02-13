# Changelog

All changes are tracked in `CAPABILITY_MAP.md` (Change Log section) for granular file-level detail.
This file provides a high-level summary per milestone.

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
