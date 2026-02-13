# ResearchHive — Agentic Research Studio

<p align="center">
  <strong>A Windows desktop app for agentic research, discovery, and idea fusion — with full citation tracking, safety awareness, and IP analysis.</strong>
</p>

---

## What Is ResearchHive?

ResearchHive is a WPF desktop application that runs **autonomous research workflows** across any topic. It plans searches, gathers evidence from the web, indexes everything into a per-session knowledge base, and synthesizes cited reports — all without manual intervention. Beyond basic research, it offers a full suite of analytical tools: hypothesis generation, materials exploration, idea fusion, contradiction detection, repo intelligence, and cross-session knowledge via **Hive Mind**.

Every substantive claim is **cited to immutable evidence** or clearly labeled as a hypothesis. When topics touch the physical world, the system automatically flags **safety hazards, PPE requirements, and disposal protocols**. When topics touch software, it surfaces **licensing signals, patent risks, and clean-room design-around options**.

---

## Features

### Core Research Engine
- **Agentic Research Loop** — Plan → Search → Extract → Chunk → Index → Synthesize → Report. Fully autonomous with pause/resume/cancel.
- **Session Workspaces** — Each research topic gets its own isolated workspace with a dedicated SQLite database, evidence store, and reports.
- **Hybrid Search** — Full-text search (FTS5) + semantic embeddings for retrieval across your evidence base.
- **Citation-Enforced Reports** — Every claim links back to source evidence. Click to open the original.
- **Incremental Research** — Continue a previous session to fill gaps. The system analyzes what's already known and targets what's missing.
- **Domain Capability Packs** — Specialized research behaviors for: General Research, History & Philosophy, Math & Formal Methods, Maker & Materials, Chemistry (Safe), and Programming Research & IP.

### Evidence & Artifacts
- **Immutable Web Snapshots** — Playwright-based page capture for reproducible citations.
- **PDF Ingestion** — Two-tier extraction via PdfPig (text layer) with automatic OCR fallback for scanned pages.
- **Screenshot OCR** — Extract text from images with bounding-box extraction.
- **Evidence Pinning** — Pin key findings for quick reference across your session.
- **Time-Range Filtering** — Focus on evidence from specific date windows.
- **Quality Scoring** — Automated source quality ranking based on domain, recency, and citation density.

### Analytical Tools

| Tool | Description |
|------|-------------|
| **Discovery Studio** | Generate novel hypotheses from collected evidence, with novelty scoring and feasibility analysis. |
| **Idea Fusion Engine** | Combine research into new proposals using 4 modes: Blend, Cross-Apply, Substitute, Optimize. Includes 10 built-in prompt templates. Each result includes a provenance map, safety flags, and IP notes. |
| **Materials Explorer** | Search for material candidates by properties, with include/avoid filters, safety labeling, and a property comparison table across all candidates. |
| **Programming Research & IP** | Generate an Approach Matrix comparing solutions across criteria, with IP/licensing analysis (license signals, risk flags, design-around options). |
| **Hive Mind** | Cross-session global memory with FTS5 + semantic search, strategy extraction, knowledge curation (browse/filter/paginate/delete), and RAG Q&A across all sessions. |
| **Repo Intelligence** | GitHub repo scanning, shallow cloning, code chunking, CodeBook generation, RAG-powered Q&A, and multi-repo Project Fusion (Merge/Extend/Compare/Architect). |
| **Citation Verification** | Quick text-match verification plus deep LLM-backed verification that citations actually support their claims. |
| **Contradiction Detection** | Fast heuristic scan plus deep embedding + LLM analysis to find conflicting claims across sources. |
| **Research Comparison** | Side-by-side comparison of sessions with overlap analysis, unique findings, and gap identification. |

### Reporting & Export
- **Sectional Report Generation** — Parallel generation of report sections for speed.
- **Q&A Tab** — Ask questions against your session's evidence with cited answers. Exportable as markdown.
- **Overview Dashboard** — At-a-glance session statistics: evidence count, source diversity, report coverage.
- **Export Formats** — HTML reports, Research Packets (ZIP), and full session archives.
- **Markdown Tables** — Full markdown rendering with advanced extensions (tables, task lists, etc.) via Markdig.
- **Job Completion Notifications** — Taskbar flash + system sound when jobs finish while the app is unfocused.
- **Search Engine Health** — Live per-engine status cards (Healthy/Degraded/Failed/Skipped) during multi-lane searches.

### Safety & IP System
- **Physical-World Safety** — Automatic labeling of environment requirements (desk / ventilated area / fume hood / pro lab), minimum PPE, hazards, and disposal protocols.
- **IP Awareness** — License signal detection, patent risk flags, and clean-room design-around suggestions for programming topics.
- **Broader Views** — Every research output includes both the most evidence-supported view AND credible alternatives with citations.

---

## Architecture

```
ResearchHive.sln
├── src/
│   ├── ResearchHive/              # WPF UI (.NET 8, CommunityToolkit.Mvvm)
│   │   ├── Views/                 # XAML views (22 workspace tabs)
│   │   ├── ViewModels/            # MVVM ViewModels (decomposed into 12 partial class files)
│   │   ├── Controls/              # Custom controls (MarkdownViewer)
│   │   ├── Converters/            # Value converters
│   │   ├── Services/              # NotificationService (P/Invoke)
│   │   └── Resources/             # Styles, themes
│   └── ResearchHive.Core/         # Business logic & services
│       ├── Data/                   # SessionDb, RegistryDb, GlobalDb (SQLite WAL)
│       ├── Models/                 # Domain models, DTOs
│       ├── Services/               # 37 registered services
│       └── Configuration/          # AppSettings
├── tests/
│   └── ResearchHive.Tests/        # xUnit test suite (341 tests)
├── spec/                           # Full product specifications
├── agents/                         # AI agent prompt definitions
└── prompts/                        # Build automation prompts
```

### Tech Stack

| Layer | Technology |
|-------|-----------|
| **UI Framework** | WPF (.NET 8, `net8.0-windows`) |
| **MVVM** | CommunityToolkit.Mvvm 8.2.2 (source generators) |
| **Markdown** | Markdig 0.34.0 with advanced extensions |
| **Database** | SQLite via Microsoft.Data.Sqlite 8.0.0 (WAL mode, per-session) |
| **PDF Extraction** | PdfPig 0.1.13 (text layer + OCR fallback) |
| **Web Scraping** | Microsoft.Playwright 1.52.0 |
| **Stealth Browsing** | Selenium.UndetectedChromeDriver 1.1.3 |
| **Key Storage** | System.Security.Cryptography.ProtectedData (DPAPI) |
| **DI** | Microsoft.Extensions.DependencyInjection 8.0.0 |
| **Testing** | xUnit + FluentAssertions-style assertions |

### Core Services (37 registered via DI)

- `SessionManager` — Create, list, delete sessions
- `SessionDb` — Per-session SQLite with 20 tables
- `ArtifactStore` — Content-addressed immutable evidence storage
- `SnapshotService` — Playwright-based web snapshots + PDF URL detection
- `OcrService` — Screenshot text extraction
- `PdfIngestionService` — Two-tier PDF extraction (PdfPig + OCR fallback)
- `IndexService` — Chunking + FTS5 indexing + embeddings
- `RetrievalService` — Hybrid keyword + semantic search with source type filtering
- `ResearchJobRunner` — Agentic research loop with per-engine health tracking
- `DiscoveryJobRunner` — Hypothesis generation and scoring
- `FusionJobRunner` — Idea fusion across 4 modes
- `MaterialsJobRunner` — Material candidate search with safety
- `ProgrammingJobRunner` — Approach matrix + IP analysis
- `GlobalDb` — Cross-session SQLite with FTS5 for Hive Mind
- `GlobalMemoryService` — Promote, curate, and query global knowledge
- `CrossSessionSearchService` — Legacy keyword search across all sessions
- `RepoScannerService` — GitHub repo metadata + dependency parsing
- `RepoCloneService` — Shallow git clone with ZIP fallback
- `CodeChunker` — Language-agnostic code splitting at semantic boundaries
- `RepoIndexService` — Clone → chunk → embed → save pipeline
- `CodeBookGenerator` — LLM architecture summary from code chunks
- `RepoIntelligenceJobRunner` — Full repo analysis pipeline
- `ComplementResearchService` — Find projects that fill repo gaps
- `ProjectFusionEngine` — Multi-repo architecture fusion
- `CitationVerificationService` — Quick + deep citation checks
- `ContradictionDetector` — Heuristic + LLM contradiction analysis
- `ResearchComparisonService` — Session comparison engine
- `EmbeddingService` — Vector embeddings (local Ollama or API)
- `LlmService` — Multi-provider LLM routing (Ollama + 8 cloud providers)
- `CodexCliService` — OpenAI Codex CLI integration
- `CourtesyPolicy` — Rate limiting, circuit breaker, per-domain delays
- `GoogleSearchService` — Selenium-based Google search
- `BrowserSearchService` — DuckDuckGo/Brave/Bing HTML scraping
- `ExportService` — ZIP, HTML, and packet export
- `InboxWatcher` — Auto-ingest dropped files
- `SecureKeyStore` — DPAPI-protected API key storage
- `NotificationService` — Taskbar flash + sound on job completion

---

## Getting Started

### Prerequisites

- **Windows 10/11** (WPF requirement)
- **.NET 8 SDK** — [Download](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Ollama** (optional, for local embeddings/LLM) — [Download](https://ollama.ai)

### Build & Run

```bash
# Clone the repository
git clone https://github.com/shellsage-ai/ResearchHive.git
cd ResearchHive

# Restore and build
dotnet restore
dotnet build

# Run the application
dotnet run --project src/ResearchHive

# Run tests
dotnet test
```

### Configuration

On first launch, visit **Settings** to configure:

1. **LLM Provider** — Choose between local Ollama or an API provider (OpenAI, Anthropic, etc.)
2. **API Keys** — Stored securely via Windows DPAPI (never in plaintext)
3. **Search Settings** — Default source count, courtesy delays, search lane preferences
4. **Embedding Model** — Local or remote embedding model selection

---

## Usage

### 1. Create a Session

Click **New Session** in the sidebar. Give it a title, description, select a **Domain Pack**, and add optional tags.

### 2. Run Research

Navigate to the **Research** tab, enter your research prompt, set the target source count (5–8 for quick, 12–15 for comprehensive), and click **▶ Run**. The agent will autonomously:
- Plan search queries
- Execute web searches
- Extract and chunk content
- Index evidence with embeddings
- Synthesize a cited report

### 3. Explore Results

- **Evidence** — Browse, search, filter, and pin collected evidence
- **Reports** — Read generated reports with full citations
- **Q&A** — Ask follow-up questions against your evidence base
- **Notebook** — Capture your own notes alongside the research

### 4. Analyze & Fuse

- **Discovery Studio** — Generate hypotheses from your evidence
- **Fusion** — Choose a template (or write a custom prompt) and fuse insights into novel proposals
- **Materials** — Explore material candidates with property comparisons
- **Verify** — Check that citations actually support their claims
- **Contradictions** — Find conflicting claims across your sources
- **Compare** — Side-by-side session comparison

### 5. Export

Export your work as HTML reports, markdown, or full Research Packets (ZIP archives with evidence + reports + metadata).

---

## Example Sessions

See [EXAMPLES.md](EXAMPLES.md) for ready-to-use research prompts across all domain packs, including a detailed Idea Fusion walkthrough.

---

## Testing

The test suite covers all core services and features:

```bash
dotnet test tests/ResearchHive.Tests/
```

```
Passed!  - Failed: 0, Passed: 339, Skipped: 2, Total: 341
```

---

## Project Structure

| Directory | Contents |
|-----------|----------|
| `src/ResearchHive/` | WPF application — Views, ViewModels, Controls, Converters, Resources |
| `src/ResearchHive.Core/` | Core library — Data access, Models, Services, Configuration |
| `tests/ResearchHive.Tests/` | xUnit test suite |
| `spec/` | Product specifications (architecture, features, safety, domain packs, etc.) |
| `agents/` | AI agent definitions (orchestrator, safety engine, UI engineer, etc.) |
| `prompts/` | Build automation prompts |

---

## License

This project is licensed under the **[PolyForm Noncommercial License 1.0.0](LICENSE.md)**.

**TL;DR:** Free for personal use, research, education, and evaluation. Commercial use (selling products/services that incorporate ResearchHive or its derivatives) requires a **paid commercial license**.

See [COMMERCIAL_LICENSE.md](COMMERCIAL_LICENSE.md) for details on what counts as commercial use and how to obtain a license.

Copyright (c) 2025–2026 ShellSage AI. All rights reserved.

---

## Contributing

This is currently a private project. If you have access, please:
1. Create a feature branch from `main`
2. Make your changes with tests
3. Ensure `dotnet test` passes (341+ tests, 0 failures)
4. Submit a pull request

---

<p align="center">
  <sub>Built with .NET 8, WPF, SQLite, and a lot of agentic ambition.</sub>
</p>
