# ResearchHive — Session Context Transfer

> **Purpose**: Read this file at the start of a new Copilot session to restore full project context.
> **Last updated**: After batch scan fix + Phase 31 completion.

---

## 1. Project Identity

**ResearchHive** is a **Windows WPF .NET 8 desktop application** for agentic research, discovery, and idea fusion. It runs autonomous research workflows: Plan → Search → Extract → Chunk → Index → Synthesize → Report. Features include hybrid search (FTS5 + semantic embeddings), citation-enforced reports, 7 domain capability packs, immutable web snapshots, PDF ingestion, OCR, and repo intelligence with project fusion.

- **Framework**: WPF .NET 8, CommunityToolkit.Mvvm (source generators for `[ObservableProperty]`, `[RelayCommand]`)
- **Database**: Per-session SQLite via `SessionDb`, plus a global `GlobalDb` for Hive Mind cross-session search
- **LLM**: Supports Ollama (local, llama3.1:8b default), OpenAI, Anthropic, Google, Codex CLI — all via `ILlmService`
- **Solution**: `ResearchHive.sln` with 3 projects: `ResearchHive` (WPF UI), `ResearchHive.Core` (services/models/data), `ResearchHive.Tests` (xUnit)

---

## 2. Current Stats

| Metric | Value |
|--------|-------|
| Phases complete | 31 |
| DI registrations | 45 (43 in `ServiceRegistration.cs` + 2 in `App.xaml.cs`) |
| Service files | 47 (in `src/ResearchHive.Core/Services/`) |
| ViewModel files | 17 (in `src/ResearchHive/ViewModels/`) |
| Test files | 25 (in `tests/ResearchHive.Tests/`) |
| Total tests | 651 (all passing, 0 failures) |
| Build errors | 0 |
| Domain packs | 7 |
| Last commit | Batch scan fix (pending commit) |
| Previous commit | `96a2f90` — Docs catch-up through Phase 31 |

---

## 3. Architecture Overview

### Layer Structure
```
Views (XAML)  →  ViewModels (CommunityToolkit.Mvvm)  →  Services (Core)  →  Data (SessionDb/GlobalDb)
```

### Key Components
- **SessionManager**: Creates/manages per-session workspace folders + SQLite DBs
- **SessionDb**: Per-session SQLite with tables for jobs, reports, artifacts, evidence, repo_profiles, project_fusions, etc.
- **5 Job Runners**: `ResearchJobRunner`, `DiscoveryJobRunner`, `ProgrammingJobRunner`, `MaterialsJobRunner`, `FusionJobRunner`
- **Repo Intelligence**: `RepoIntelligenceJobRunner` (scan pipeline: metadata → clone → index → identity → fact sheet → analysis → gap verify → complement → post-verify → self-validate → save)
- **Project Fusion**: `ProjectFusionEngine` (multi-repo synthesis: outline → expand 10 sections in parallel batches of 4 → post-verify with `FusionPostVerifier`)
- **LlmService**: Unified LLM interface with `GenerateAsync(prompt, systemPrompt, maxTokens, tier, ct)` — `ModelTier.Mini` for validation, `ModelTier.Default` for analysis
- **ExportService**: Markdown → HTML/PDF/Word export with `SafeMarkdownToHtml()` (Markdig 0.34.0)
- **Anti-hallucination pipeline**: `RepoFactSheetBuilder` → `PostScanVerifier` → `SelfValidateStrengthsAsync` → `FusionPostVerifier`

### ViewModel Decomposition
`SessionWorkspaceViewModel` is a partial class split across 11 files:
- `.cs` — Core fields, constructor (25 DI params), `LoadSessionData()`, properties
- `.Research.cs` — Research job commands
- `.RepoIntelligence.cs` — Single scan, batch scan, Q&A, fusion commands
- `.ProjectDiscovery.cs` — GitHub discovery search + scan selected
- `.Evidence.cs` — Evidence management
- `.Export.cs` — Export commands
- `.Verification.cs` — Citation verification, contradiction detection
- `.HiveMind.cs` — Cross-session search
- `.NotebookQa.cs` — Research notebook Q&A
- `.DomainRunners.cs` — Domain-specific runners
- `.Crud.cs` — CRUD operations

---

## 4. Recent Phase History

| Phase | Commit | Description |
|-------|--------|-------------|
| **31** | `dd13b81` | Anti-hallucination & factual accuracy hardening (6 steps: prompt tightening, strength grounding, self-validation LLM pass, FusionPostVerifier, cross-section consistency, 35 tests) |
| **30** | `6f3ca51` | Scan identity isolation & report quality (8-step fix for cross-contamination, identity confusion between repos) |
| **29** | `0ad78df` | Identity Scan — dedicated pipeline phase for product-level identity |
| **28** | `3d7c4db` | Fusion identity grounding + scan/fusion cancellation |
| **27** | `6bbb72e` | Scan & Fusion quality overhaul — ProjectSummary, ProjectedCapabilities, anti-hallucination |
| **26** | `71587bb` | Project Fusion quality overhaul — grounded prompts, goal-specific sections, source references |
| **25** | `fadf193` | Research report quality + readability overhaul |
| Export fix | `106914f` | Safe markdown-to-HTML with nesting flattener |
| Docs | `96a2f90` | Catch up all documentation through Phase 31 |
| Batch fix | (pending) | Batch scan robustness — continue on individual failure, feedback on empty input, per-scan notifications, 12 new tests |

---

## 5. Documentation Files — MUST Update After Every Phase

These files **must be updated** after every implementation phase or significant fix:

| File | Purpose |
|------|---------|
| `CHANGELOG.md` | Chronological log of all changes, phases, fixes |
| `CAPABILITY_MAP.md` | Complete capability inventory with test counts, DI counts, change log |
| `README.md` | Project overview, stats (test count, service count), feature list |
| `PROJECT_PROGRESS.md` | Phase-by-phase progress tracker, milestone status |
| `PROJECT_CONTEXT.md` | Quick-reference technical context (DI count, tests, active phase) |
| `EXAMPLES.md` | Usage examples (update when UI-facing features are added) |

**Rule**: After committing code changes, ALWAYS update all applicable doc files in the same or immediately following commit. The user is strict about this.

---

## 6. Key File Locations

### Core Services (most frequently modified)
- `src/ResearchHive.Core/Services/RepoIntelligenceJobRunner.cs` — Scan pipeline (1259 lines)
- `src/ResearchHive.Core/Services/RepoScannerService.cs` — GitHub/local scanning + LLM prompts (1676 lines)
- `src/ResearchHive.Core/Services/PostScanVerifier.cs` — Ground-truth verification (~1144 lines)
- `src/ResearchHive.Core/Services/ProjectFusionEngine.cs` — Multi-repo fusion
- `src/ResearchHive.Core/Services/FusionPostVerifier.cs` — Fusion output fact-checking (~505 lines)
- `src/ResearchHive.Core/Services/RepoFactSheetBuilder.cs` — Zero-LLM deterministic analysis
- `src/ResearchHive.Core/Services/ServiceRegistration.cs` — All 43 DI registrations
- `src/ResearchHive.Core/Services/LlmService.cs` — Multi-provider LLM routing
- `src/ResearchHive.Core/Services/ExportService.cs` — Markdown/HTML/PDF/Word export

### ViewModels
- `src/ResearchHive/ViewModels/SessionWorkspaceViewModel.cs` — Main VM (565 lines, 25 DI params)
- `src/ResearchHive/ViewModels/SessionWorkspaceViewModel.RepoIntelligence.cs` — Batch scan, single scan, Q&A

### Views
- `src/ResearchHive/Views/SessionWorkspaceView.xaml` — Main workspace UI (2283 lines)
- `src/ResearchHive/Views/SettingsView.xaml` — Settings page

### Models
- `src/ResearchHive.Core/Models/` — RepoProfile, ProjectFusionArtifact, ResearchJob, Report, etc.

### Data
- `src/ResearchHive.Core/Data/SessionDb.cs` — Per-session SQLite
- `src/ResearchHive.Core/Data/GlobalDb.cs` — Cross-session Hive Mind

### Configuration
- `src/ResearchHive.Core/Configuration/AppSettings.cs` — All app settings
- `publish/ResearchHive_Package/appsettings.json` — Default settings file

---

## 7. Scan Pipeline Flow

```
User enters URL/path
    → RepoScannerService.ScanAsync (metadata, README, deps via GitHub API or local FS)
    → RepoCloneService.CloneAsync (clone to temp dir)
    → RepoIndexService.IndexRepoAsync (chunk all source files → embeddings → FTS index)
    → RepoScannerService.RunIdentityScanAsync (LLM: what IS this project?)
    → RepoFactSheetBuilder.Build (zero-LLM: package usage, capabilities, test count)
    → [Agentic | Consolidated | Separate] Analysis Path:
        → CodeBookGenerator (architecture summary from RAG chunks)
        → RAG-Grounded Analysis (12-query retrieval + LLM strengths/gaps)
        → Gap Verification (per-gap RAG check → prune false positives)
    → ComplementResearchService.ResearchAsync (web search for gap-filling projects)
    → PostScanVerifier.VerifyAsync (fact-check LLM output vs. fact sheet)
    → SelfValidateStrengthsAsync (LLM cross-checks its own strength descriptions)
    → Save RepoProfile + Report to SessionDb
```

---

## 8. Testing Patterns

- **Framework**: xUnit + FluentAssertions
- **Pattern**: Most tests use real `SessionDb` (temp file), mock only LLM via simple stubs
- **Naming**: `ClassName_Method_ExpectedBehavior` or `Feature_Scenario_Expectation`
- **Key test files**:
  - `RepoIntelligenceTests.cs` — Repo profile CRUD, URL parsing, batch scan parsing
  - `Phase31VerifierTests.cs` — FusionPostVerifier, strength grounding, overstatement patterns
  - `FactSheetAndVerifierTests.cs` — PostScanVerifier, fact sheet builder
  - `RagGroundedAnalysisTests.cs` — RAG analysis prompt/parse tests
  - `ExportServiceTests.cs` — Export pipeline tests
  - `FeatureTests.cs` / `NewFeatureTests.cs` — General feature regression tests

---

## 9. Build & Run

```powershell
# Build
dotnet build ResearchHive.sln

# Run tests
dotnet test tests/ResearchHive.Tests/ResearchHive.Tests.csproj -v q

# Run specific tests
dotnet test tests/ResearchHive.Tests/ResearchHive.Tests.csproj --filter "ClassName"

# Run the app (requires Windows — WPF)
dotnet run --project src/ResearchHive/ResearchHive.csproj
```

---

## 10. Common Patterns & Conventions

- **LLM calls**: Always use `ILlmService.GenerateAsync(prompt, systemPrompt, maxTokens, tier, ct)` — specify `ModelTier.Mini` for validation/verification, `ModelTier.Default` for primary analysis
- **DI registration**: Add to `ServiceRegistration.cs` as `AddSingleton<T>()`. Factory lambdas for services with complex dependencies.
- **Error handling**: Services use try-catch with `_logger?.LogWarning` for non-fatal errors. ViewModels catch exceptions and set status text properties.
- **XAML bindings**: `[ObservableProperty]` generates properties, `[RelayCommand]` generates commands. Async commands auto-disable via `AsyncRelayCommand`.
- **Testing**: Avoid mocking — use real `SessionDb` with temp files, test parsing/logic directly.
- **Anti-hallucination**: Every LLM output goes through deterministic verification. Strength descriptions are grounded against `FactSheet.ProvenCapabilities`. Fusion output validated by `FusionPostVerifier`.

---

## 11. Active Issues / Known Quirks

- Build may show file lock errors (MSB3027) if the app or Visual Studio has the DLL loaded — close the running app first.
- `SecureKeyStore` warnings (CA1416) in tests — Windows-only API, safe to ignore.
- CommunityToolkit.Mvvm source generators require partial classes — all ViewModel files must be `partial class SessionWorkspaceViewModel`.
- The `ModernTextBox` style (Styles.xaml) doesn't render placeholder text from the `Tag` property — it's metadata only.
- Markdig 0.34.0 can stack-overflow on deeply nested markdown — `ExportService.SafeMarkdownToHtml()` handles this.

---

## 12. Spec Documents

The `spec/` directory contains detailed specifications:
- `ARCHITECTURE.md` — System architecture
- `PRODUCT_BRIEF.md` — Product vision
- `UI_WPF_SPEC.md` — UI specification
- `DATA_MODEL.md` — Database schema
- `RESEARCH_AGENT_LOOP.md` — Research pipeline spec
- `SAFETY_SYSTEM.md` — Safety engine spec
- `IDEA_FUSION_ENGINE.md` — Fusion engine spec
- `REPORTING_AND_LEARNING.md` — Report generation spec
- `SEARCH_LANES_AND_COURTESY.md` — Rate limiting / courtesy
- `MODEL_STRATEGY.md` — LLM provider strategy
- `PROGRAMMING_RESEARCH_AND_IP.md` — Repo intelligence spec
- `MILESTONE_PLAN.md` — Development milestones

---

*End of context transfer. Start new sessions by asking Copilot to read this file.*
