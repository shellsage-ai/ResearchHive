# ResearchHive — Project Progress

## Status
- Current milestone: All milestones (1-9) complete
- Build status: ✅ PASSING (0 warnings, 0 errors)
- Last verified: Build + publish successful

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

4. **PDF text extraction is basic**: Removed UglyToad.PdfPig due to NuGet availability issues. Using basic BT/ET text extraction from PDF binary. For complex PDFs, users can OCR screenshot captures.

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
- Published package: `publish\ResearchHive_Package\`
