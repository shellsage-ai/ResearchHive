# Architecture (Windows-first, agentic, auditable)

## High-level
- Desktop UI: WPF .NET 8 (MVVM)
- Background workflows: hosted services + job runners
- Session isolation: per-session workspace folder + per-session SQLite DB

## Core services
- SessionManager
- ArtifactStore (content-addressed immutable)
- SnapshotService (Playwright)
- OCRService (bounding boxes)
- IndexService (chunking + FTS + embeddings)
- RetrievalService (hybrid + optional reranking)
- CitationService (stable spans/boxes)
- JobRunners: Research, Discovery, Materials, Programming, Fusion
- ExportService (reports)
- AuditLogService (structured logs + replay)

## Long-running jobs
- Persisted state machine per job
- Checkpoint per acquired source
- Pause/resume/cancel
- Restart recovery
