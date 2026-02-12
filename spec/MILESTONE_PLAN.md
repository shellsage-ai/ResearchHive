# Milestone Plan (Autonomous, No Approval Gates)

The agent MUST execute Milestones 1–9 sequentially without stopping for approval.

Milestone 1 — Solution Skeleton + Sessions Hub
- WPF .NET 8 solution (MVVM)
- Sessions sidebar: create/list/search/tag + status + last-report preview backed by real data
- Session workspace folder creation per spec
- Session registry persistence

Milestone 2 — ArtifactStore + Inbox + Notebook + FTS
- Content-addressed ArtifactStore (immutable)
- File watcher ingestion
- Notebook markdown notes
- FTS keyword search UI

Milestone 3 — Playwright SnapshotService + Sources Viewer
- URL capture to snapshot bundles + offline viewer

Milestone 4 — OCR Captures + Citation Highlight
- Screenshot capture + OCR with boxes
- Citation click highlights region

Milestone 5 — Embeddings + Hybrid Retrieval + Evidence Panel
- Semantic index + hybrid search + evidence pinning

Milestone 6 — Agentic Research Jobs + Reports + Replay
- Persisted state machine + checkpointing + resume after restart
- Citation-enforced reports: exec + full + activity + replay

Milestone 7 — Discovery Studio
- Known map + idea cards + novelty sanity-check + scoring + export

Milestone 8 — Programming Research + IP Studio
- Approach matrix + IP summary + design-around + export + replay integration

Milestone 9 — Materials Explorer + Idea Fusion + Packaging
- Materials Explorer end-to-end + exports + replay
- Idea Fusion workspace + provenance mapping + export
- Packaging output produced
- End-to-end demo checklist passes (spec/END_TO_END_DEMO.md)

Global requirement (applies to all milestones): Implement multi-lane search locations and polite browsing rules per spec/SEARCH_LANES_AND_COURTESY.md.
