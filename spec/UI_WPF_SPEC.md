# UI & WPF Spec — ResearchHive

> Comprehensive specification for every view, control, style, converter, ViewModel, and interaction in the ResearchHive WPF desktop application.

---

## 1  Technology Stack

| Layer | Technology |
|-------|-----------|
| Framework | WPF on .NET 8 (`net8.0-windows`) |
| MVVM toolkit | CommunityToolkit.Mvvm 8.2.2 (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`) |
| Markdown rendering | Custom `MarkdownViewer` control using **Markdig** → WPF `FlowDocument` |
| Dependency injection | `Microsoft.Extensions.DependencyInjection` — all ViewModels resolved via `ViewModelFactory` |
| Packaging | Single-file publish, self-contained |

---

## 2  Application Shell — `MainWindow.xaml`

The shell is a three-column `Grid`:

```
┌──────────────┬──┬──────────────────────────────────────────┐
│  Sidebar     │  │  Content Area                            │
│  (280px,     │GS│  ┌─ Warning Banner (Ollama offline) ──┐  │
│   200–400)   │  │  │                                    │  │
│              │  │  ├─ ContentControl (CurrentView) ─────┤  │
│              │  │  │  • WelcomeViewModel → WelcomeView  │  │
│              │  │  │  • SessionWorkspaceViewModel → ...  │  │
│              │  │  │  • SettingsViewModel → SettingsView │  │
│              │  │  ├────────────────────────────────────┤  │
│              │  │  │ Status Bar (Ollama dot + text)      │  │
│  ┌──────┐   │  │  └────────────────────────────────────┘  │
│  │Home  │   │  │                                          │
│  │Settng│   │  │                                          │
│  └──────┘   │  │                                          │
└──────────────┴──┴──────────────────────────────────────────┘
             GridSplitter
```

### 2.1  Layout Details

| Region | Width | Background | Notes |
|--------|-------|------------|-------|
| Sidebar | 280px (min 200, max 400) | `SidebarBrush` (#263238) | Resizable via `GridSplitter` |
| Content | `*` (fills remaining) | `BackgroundBrush` (#F5F5F5) | Three rows: banner / view / status bar |

### 2.2  Global Keyboard Shortcuts

| Shortcut | Action | Command |
|----------|--------|---------|
| `Ctrl+N` | Create new session | `Sidebar.StartCreateSessionCommand` |
| `Ctrl+H` | Navigate home | `ShowHomeCommand` |
| `Ctrl+,` | Open settings | `ShowSettingsCommand` |
| `Escape` | Navigate home | `ShowHomeCommand` |

### 2.3  Ollama Status

- **Warning banner** (top of content area): Visible when `IsOllamaConnected` is false. Amber background (`#FFF3E0`), ⚠ icon, displays `OllamaStatus` text.
- **Status bar** (bottom): Green/red dot reflecting `IsOllamaConnected`, text from `StatusText`, plus a `BusyIndicator` progress bar when `IsBusy` is true.
- Ollama health is polled every **30 seconds** via `DispatcherTimer`. When routing is `CloudOnly`, the banner is suppressed and status shows the active cloud provider.

### 2.4  View Routing (DataTemplates)

The `CurrentView` property on `MainViewModel` drives which view is shown:

| ViewModel | View | When |
|-----------|------|------|
| `WelcomeViewModel` | `WelcomeView` | App launch, `ShowHomeCommand` |
| `SessionWorkspaceViewModel` | `SessionWorkspaceView` | Session selected in sidebar |
| `SettingsViewModel` | `SettingsView` | `ShowSettingsCommand` |

---

## 3  Sessions Sidebar — `SessionsSidebarView.xaml`

**DataContext:** `SessionsSidebarViewModel`

### 3.1  Layout (4-row Grid)

| Row | Content |
|-----|---------|
| 0 — Search | TextBox with placeholder "Search sessions…", refresh button (🔄) |
| 1 — New Session Panel | Expandable form (visible when `IsCreatingSession` is true) |
| 2 — Sessions List | Scrollable `ListBox` bound to `Sessions` collection |
| 3 — New Session Button | `+ New Session` button (hidden during creation) |

### 3.2  New Session Form Fields

| Field | Binding | Control | Placeholder |
|-------|---------|---------|-------------|
| Title | `NewTitle` | TextBox | "e.g. Climate change effects on coral reefs" |
| Description | `NewDescription` | TextBox (multiline, 50px) | "Describe what you want to research…" |
| Domain Pack | `NewPack` | ComboBox (enum via `DomainPackDisplayConverter`) | — |
| Tags | `NewTags` | TextBox | "Comma-separated, e.g. biology, marine" |

Buttons: **Cancel** (`CancelCreateSessionCommand`) and **Create** (`ConfirmCreateSessionCommand`).

### 3.3  Session List Item

Each session is a `Border` with:
- **Status indicator**: 4px vertical bar, color from `StatusColor` via `StringToColorBrushConverter`
- **Title**: White, 13px, SemiBold, character-ellipsis trimming
- **Subtitle**: `Pack · Status` in muted text (#B0BEC5)
- **Timestamp**: `Updated` in #78909C, 10px
- **Delete button**: ✕ on top-right corner → `DeleteSessionCommand`

Hover state: subtle white overlay (`#10FFFFFF`).

### 3.4  Key ViewModel Properties

| Property | Type | Purpose |
|----------|------|---------|
| `SearchText` | `string` | Filters sessions list in real-time |
| `Sessions` | `ObservableCollection<SessionItemViewModel>` | All loaded sessions |
| `SelectedSession` | `SessionItemViewModel?` | Triggers `OnSessionSelected` callback |
| `IsCreatingSession` | `bool` | Toggles creation panel visibility |
| `DomainPacks` | `DomainPack[]` | Enum values for ComboBox |

---

## 4  Welcome View — `WelcomeView.xaml`

**DataContext:** `WelcomeViewModel`

A centered splash with:
- 🔬 emoji (48px)
- "Welcome to ResearchHive" title (28px Bold)
- Dynamic `WelcomeText` subtitle
- **Quick Start** card: 4-step numbered guide
- **Domain Packs** card: lists all 6 domain packs with one-line descriptions

| Pack | Description |
|------|-------------|
| General Research | Broad topic exploration |
| History / Philosophy | Historical & philosophical inquiry |
| Math | Mathematical research and proofs |
| Maker / Materials | Materials science with safety |
| Chemistry (Safe) | Chemistry with safety labels |
| Programming / IP | Software research with license analysis |

---

## 5  Settings View — `SettingsView.xaml`

**DataContext:** `SettingsViewModel`

A vertical `ScrollViewer` (max width 720px) containing 7 card sections:

### 5.1  Section Breakdown

| # | Section | Key Controls |
|---|---------|-------------|
| 1 | ⚡ AI Routing Strategy | ComboBox: `LocalOnly`, `LocalWithCloudFallback`, `CloudPrimary`, `CloudOnly`. Explanation box below. |
| 2 | 🖥 Local AI (Ollama) | Base URL text field. Two ComboBoxes for Embedding Model and Synthesis Model (editable, auto-populated from `AvailableModels`). Refresh Models button. |
| 3 | ☁ Cloud AI Provider | Enable checkbox. Provider ComboBox, Model ComboBox (editable). Provider-specific sub-panels: **ChatGPT Plus** auth mode selector (Codex OAuth vs API Key), **Codex OAuth** status/setup instructions, **API Key** input (DPAPI encrypted), Key Source selector, Environment Variable field, Endpoint URL. Validate button. |
| 4 | ⬡ GitHub Models (Free) | PAT text field (`ghp_...`), Validate button, rate limit info (15 req/min, 150 req/day). |
| 5 | 🔧 Tool Calling | Enable checkbox. Max tool calls per phase. Available tools list: `search_evidence`, `search_web`, `get_source`, `verify_claim`. |
| 6 | 📁 Data Storage | Root data path TextBox. |
| 7 | 🌐 Polite Browsing | Three fields: Max Concurrent Fetches, Min Domain Delay (s), Max Domain Delay (s). |

**Footer:** Save button + success status message.

### 5.2  Security Note

API keys are encrypted with **Windows DPAPI** before storage — never stored in plaintext. The UI text field is standard (not password-masked) for visibility during entry, with a placeholder hint.

---

## 6  Session Workspace — `SessionWorkspaceView.xaml` (2,095 lines)

**DataContext:** `SessionWorkspaceViewModel` (partial class across 10 files)

The main working area for a session. Two-column layout:

```
┌────────────────┬──────────────────────────────────────────┐
│ Tab Navigation │  Scrollable Tab Content                  │
│  (160px)       │  (fills remaining width)                 │
│                │                                          │
│ ┌────────────┐ │                                          │
│ │ 📋 Overview│ │  ← active tab content visible,           │
│ │ 🔬 Research│ │    all others collapsed via               │
│ │ 🌐 Snapshts│ │    EqualityToVisibilityConverter          │
│ │ 🔍 Evidence│ │                                          │
│ │ 📓 Notebook│ │                                          │
│ │ 📊 Reports │ │                                          │
│ │ 💬 Q&A     │ │                                          │
│ │ ⏪ Replay  │ │                                          │
│ │ 💡 Discvry │ │                                          │
│ │ 🔗 Fusion  │ │                                          │
│ │ 📦 Artifact│ │                                          │
│ │ 📜 Logs    │ │                                          │
│ │ 📤 Export  │ │                                          │
│ │ ✅ Verify  │ │                                          │
│ │ ⚡ Contrdct│ │                                          │
│ │ 📈 Compare │ │                                          │
│ │ 🧠 HiveMnd│ │                                          │
│ │ 🔎 RepoScn│ │                                          │
│ │ 🏗️ PrjFusn│ │                                          │
│ └────────────┘ │                                          │
└────────────────┴──────────────────────────────────────────┘
```

### 6.1  Tab Navigation

- Left strip: `ListBox` bound to `VisibleTabs` (filtered by domain pack)
- Each tab is a `TabItemViewModel(emoji, name, tag, group)`
- Selected tab styling: left blue border (`PrimaryBrush`), light blue background (`PrimaryLightBrush`), SemiBold text
- Hover: subtle background `#08000000`
- Tab selection fires `TabList_SelectionChanged` → sets `ActiveTab` string

### 6.2  Tab Visibility by Domain Pack

Tabs are organized into 4 groups with domain-specific filtering:

| Group | Tabs | Visibility |
|-------|------|-----------|
| **Core** | Overview, Research, Snapshots, Evidence, Notebook, Reports, Q&A, Replay | Always visible |
| **Tools** | OCR, Materials, Programming, Repo Scan | Filtered per pack |
| **Analysis** | Discovery, Fusion, Artifacts, Verify, Contradictions, Compare, Project Fusion | Always visible |
| **Meta** | Logs, Export, Hive Mind | Always visible |

**Hidden tabs by pack:**

| Domain Pack | Hidden Tabs |
|-------------|-------------|
| General Research | Materials, Programming, RepoScan, ProjectFusion |
| History & Philosophy | Materials, Programming, RepoScan, ProjectFusion |
| Math | Materials, Programming, OCR, RepoScan, ProjectFusion |
| Maker / Materials | Programming, RepoScan, ProjectFusion |
| Chemistry (Safe) | Programming, RepoScan, ProjectFusion |
| Programming / IP | Materials, OCR |
| Repo Intelligence | Materials, OCR |

---

## 7  Tab Specifications

### 7.1  📋 Overview Tab

Displays session summary with:
- **Title** (22px Bold) + subtitle line (`DomainPack · Status · Tags`)
- **Stats grid** (3×2 `UniformGrid`): Sources, Evidence Chunks, Reports, Jobs, Pinned, Notes — each in a `Card` with large number (28px Bold, PrimaryBrush) and label
- **Latest Summary** card: read-only text from `OverviewText`
- **Session info** card: Created date, Last activity, Workspace path, "📂 Open Folder" button
- **Post-research tip** banner: blue background (`#E3F2FD`), conditional on `HasPostResearchTip`

### 7.2  🔬 Research Tab

The primary research workflow:

**Input section (Card):**
- Research Prompt: multiline TextBox (80px height)
- Target Sources: number field
- ⚡ Streamlined Codex: checkbox (visible when Codex is available)
- 🏅 Quality Rank: checkbox (boost academic sources)
- Time range: ComboBox filter
- Buttons: ▶ Run, ⏸ Pause, ▶ Resume

**Live Progress panel** (visible during `IsResearchRunning`):
- Step description + iteration counter + coverage badge
- Source counters: ✅ Found / ❌ Failed / 🎯 Target
- Quality metrics: 📋 Sub-Qs / 📌 Grounding / 🌐 Browser Pool
- Activity Log: `ListBox` (Consolas 11px, 120px height, supports Ctrl+C copy)
- ✕ Cancel / ✕ Dismiss buttons

**Post-research tip** banner (green, `#E8F5E9`)

**Continue Research section (Card):**
- Optional refinement prompt + extra sources count + 📈 Continue button
- Targets an existing job for incremental research

**Jobs list**: `ItemsControl` showing all research jobs with:
- Status color bar, title, created date, source count, delete button
- Clickable (populates Replay tab)

### 7.3  🌐 Snapshots Tab

Web page capture and browsing:
- **URL capture**: TextBox + 📥 Capture button
- **Empty state**: italic message when no snapshots
- **Sort controls**: Newest / Oldest / A-Z buttons
- **Master-detail layout**: Left list (300px) ↔ Right content viewer (Consolas monospace)
- Each snapshot shows: Title, URL, Status, delete button
- Selected snapshot's extracted text displayed in right panel

### 7.4  📷 OCR Tab

Image text extraction:
- **Input**: Image path TextBox + 📷 OCR button
- **Results list**: Each capture shows description, timestamp, extracted OCR text (Consolas, max 200px), box count, delete button

### 7.5  🔍 Evidence Tab

Hybrid search and evidence management:
- **Search bar**: TextBox + 🔍 Search button
- **📌 Pinned Evidence**: Cards with accent border showing source type, relevance score, text, URL, unpin button
- **Search Results**: Cards with score, text, URL, pin button (📌)
  - Sort controls: Score / A-Z / Source
- **🔧 Search Engine Health**: Wrap panel of engine status tiles (status icon, engine name, status, rate, total results)
- **🩺 Source Health**: `DataGrid` with columns: Status icon, Status, HTTP code, URL, Reason, Timestamp (max 300px height)

### 7.6  📓 Notebook Tab

Personal research notes:
- **Add Note form**: Title TextBox + Content TextBox (multiline 80px) + "Add Note" button
- **Notes list**: Each note card has:
  - Read-only mode: title (SemiBold 14px), updated timestamp, content
  - Edit mode: editable title + content fields
  - Toggle button: ✎ (edit) ↔ ✓ (save)
  - Delete button (✕)
- Notes auto-save every 30 seconds

### 7.7  📊 Reports Tab

Report viewer with master-detail:
- **Empty state** message when no reports
- **Left panel** (250px): `ListBox` of reports showing title, type badge, created date, delete button
- **Right panel**: `MarkdownViewer` control rendering `ReportContent`
- Report types: Executive Summary, Full Report (inline citations), Activity Report, Replay Timeline

### 7.8  💬 Q&A Tab

Follow-up question interface:
- **Scope selector** card: ComboBox choosing between session-wide evidence or a specific report
- **Chat history**: scrollable card (max 500px) with Q/A pairs — question in AccentBrush, answer via `MarkdownViewer`
- **Ask input**: TextBox + "Ask" button, disabled during `IsQaRunning`
- Each answer includes `ModelUsed` attribution (Phase 13)

### 7.9  ⏪ Replay Tab

Chronological research timeline:
- **Step cards**: Each entry shows:
  - Order number (20px Bold, PrimaryBrush) + timestamp (9px)
  - Type label (PrimaryBrush, Medium)
  - Title (SemiBold 13px)
  - Description (read-only selectable text)

### 7.10  💡 Discovery Tab

AI idea generation:
- **Input card**: Problem Statement (multiline 60px), Constraints (optional), "💡 Generate Ideas" button
- **Idea Cards**: Each shows:
  - Title (15px SemiBold)
  - Hypothesis (selectable text)
  - Two-column grid: Mechanism | Minimal Test Plan
  - Falsification criteria
  - Novelty check + composite Score badge
  - Delete button

### 7.11  🧪 Materials Tab (domain-filtered)

Materials explorer:
- **Input card**: Desired Properties (multiline key:value), Filters (multiline key:value), Avoid (comma-separated), Include (comma-separated), "🧪 Search Materials" button
- **Material Cards**: Each shows:
  - Rank (#N), Name, Category, Fit Score badge
  - Fit Rationale
  - ⚠ Safety section (amber `#FFF3E0`): Level, Environment, PPE, Hazards
  - Properties (Consolas monospace)
  - Test Checklist
  - Delete button
- **Property Comparison Table**: `MarkdownViewer` rendering comparison matrix

### 7.12  💻 Programming Tab (domain-filtered)

Software approach research:
- **Input card**: Problem Description (multiline 60px) + "💻 Research" button
- **Approach Matrix**: Cards for each approach:
  - Name + ★ Recommended badge (green highlight `#E8F5E9` via `BoolToHighlightBrushConverter`)
  - Description, Evaluation (Consolas), IP Analysis
- **Full Report**: `MarkdownViewer` section

### 7.13  🔗 Idea Fusion Tab

Combines session knowledge into novel proposals:
- **Template picker**: ComboBox of `FusionPromptTemplates` with `DisplayName` + description
- **Fusion Prompt** (multiline 80px) + Mode selector (Blend/Cross-Apply/Substitute/Optimize) + "🔗 Fuse" button
- **Fusion Results**: Cards showing:
  - Mode badge + created timestamp
  - Proposal text
  - Provenance map (trace to sources)
  - ⚠️ Safety Notes (amber, conditional)
  - 📜 IP/License Notes (indigo `#E8EAF6`, conditional)
  - Delete button

### 7.14  📦 Artifacts Tab

Ingested file management:
- `DataGrid` with columns: Name, Type, Size, Ingested timestamp, delete button
- Supports drag & drop file ingestion
- Files are content-hashed for deduplication

### 7.15  📜 Logs Tab

Structured audit trail:
- Title + "🔄 Refresh" button (`ViewLogsCommand`)
- Log content in Consolas 11px monospace, read-only, max 600px scroll

### 7.16  📤 Export Tab

Four export options, each in its own card:
- **📦 Session Archive**: Full ZIP export (`ExportSessionCommand`)
- **📄 Export Report as HTML**: Selected report → standalone HTML with embedded styling (`ExportReportAsHtmlCommand`)
- **📋 Research Packet**: Self-contained packet with all reports as HTML, sources.csv, notebook.html, index.html (`ExportResearchPacketCommand`)
- **📂 Workspace**: Open workspace folder button

### 7.17  ✅ Citation Verification Tab

Verifies report citations against source material:
- "🔍 Quick Verify" + "🔬 Deep Verify (LLM)" buttons
- Summary text (e.g., "12/15 verified")
- Results `ListView`: Status icon, citation label, overlap percentage, claim text

### 7.18  ⚡ Contradiction Detection Tab

Finds conflicting claims across sources:
- "⚡ Quick Detect" + "🔬 Deep Detect (Embeddings + LLM)" buttons + status text
- Results `ListView`: Type icon, type label, score (#FF9800), claim Text A, "vs:" Text B

### 7.19  📈 Research Comparison Tab

Side-by-side job comparison:
- Two ComboBoxes (Job A, Job B) + "📊 Compare" button
- Results in `MarkdownViewer` (max 600px)

### 7.20  🧠 Hive Mind Tab

Cross-session RAG knowledge base:

**Sub-sections:**
1. **Ask the Hive Mind**: Question TextBox + "🧠 Ask" button + "📊 Stats" button. Answer displayed as selectable text. Shows stats text when loaded.
2. **Promote This Session**: "⬆️ Promote" button pushes session knowledge to global memory
3. **📦 Knowledge Curation**: Browse/delete individual chunks with source type filter, paginated `ListView` (Prev/Next). Each chunk shows type icon, text preview, source type, domain pack, promoted date, delete (🗑️) button.
4. **🔍 Cross-Session Evidence Search**: Query TextBox + "Search Reports" checkbox + "🔍 Search" button + "📊 Stats" button. Results list shows session title, domain pack, source URL, chunk text. Report matches sub-section with session title, report type, title, snippet, date.
5. **📊 Global Statistics**: Stats card with aggregate numbers

### 7.21  🔎 Repo Scan Tab (domain-filtered)

Repository intelligence:

**Sub-sections:**
1. **🧠 Ask About a Repo**: URL field + question TextBox + "💬 Ask" button. Answer card with copy button. Expandable Q&A history with repo label.
2. **Single Repo Scan**: URL TextBox + "🔎 Scan" button
3. **Batch Scan**: Multiline TextBox (one URL per line) + "🔎 Scan All" button
4. **Scanned Profiles**: Cards showing:
   - Scan Proof verification banner (green, Consolas, conditional on `HasProof`)
   - Full name, primary language badge, stars (⭐), forks (🍴)
   - Description, frameworks, dependency count
   - Strengths section, Gaps section
   - Complementary projects count + individual items with clickable hyperlinks
   - Created timestamp, delete button, copy button

### 7.22  🏗️ Project Fusion Tab

Multi-repo architecture fusion:
- **Template picker** ComboBox
- **Input Selection**: Checkbox list of repo profiles + prior fusions
- **Focus Prompt** (multiline 60px) + Goal selector ComboBox + "🏗️ Fuse Projects" button
- **Fusion Artifacts**: Cards showing:
  - Title + Goal badge, input summary
  - Unified Vision, Architecture (selectable text)
  - Feature count + Feature Matrix
  - Gaps Closed, New Gaps
  - IP Notes (indigo, conditional)
  - Provenance map
  - Created timestamp, delete + copy buttons

---

## 8  Custom Controls

### 8.1  `MarkdownViewer` (Controls/MarkdownViewer.cs)

A `FlowDocumentScrollViewer` subclass that renders Markdown to WPF `FlowDocument`:

| Property | Type | Purpose |
|----------|------|---------|
| `Markdown` | `string` (DependencyProperty) | Input Markdown text, triggers re-render on change |

**Rendering pipeline:**
1. Parse Markdown via `Markdig` (`MarkdownPipelineBuilder().UseAdvancedExtensions()`)
2. Walk the Markdig AST
3. Convert each node to WPF `Block`/`Inline` elements (`Paragraph`, `Section`, `Table`, `Run`, `Bold`, `Italic`, `Hyperlink`, `List`)
4. Apply styling (Segoe UI, 13px, 16px padding)

**Used in:** Reports tab, Q&A tab, Materials comparison, Programming report, Fusion results, Comparison results, Project Fusion artifacts.

---

## 9  Value Converters

All converters live in `Converters/ValueConverters.cs`:

| Converter | Input → Output | Usage |
|-----------|---------------|-------|
| `BoolToVisibilityConverter` | `bool` → `Visibility`. Param `"Invert"` reverses. | Show/hide panels based on boolean state |
| `InverseBoolConverter` | `bool` → `!bool` | Disable buttons when running (singleton `Instance`) |
| `BoolToHighlightBrushConverter` | `bool` → `Brush` (`#E8F5E9` or Transparent) | Highlight recommended approaches (singleton `Instance`) |
| `StringToColorBrushConverter` | Hex string → `SolidColorBrush` | Status color bars from ViewModel hex strings |
| `NullToVisibilityConverter` | `object?` → `Visible`/`Collapsed` | Show content only when data exists |
| `StringNotEmptyToVisibilityConverter` | `string` → `Visible`/`Collapsed` | Show status messages, URLs |
| `EqualityConverter` | `value == parameter` → `bool` | General equality check |
| `EqualityToVisibilityConverter` | `value == parameter` → `Visibility` | **Tab switching**: `ActiveTab == "Research"` etc. |
| `DomainPackDisplayConverter` | `DomainPack` enum → display string | ComboBox labels for domain pack selection |
| `CountToVisibilityConverter` | `int` → `Visibility` (Visible when 0). Param `"Invert"` reverses. | Empty-state messages |

---

## 10  Design System — `Resources/Styles.xaml`

### 10.1  Color Palette

| Token | Hex | Role |
|-------|-----|------|
| `PrimaryColor` | `#1976D2` | Main brand blue |
| `PrimaryDarkColor` | `#0D47A1` | Hover state for primary buttons |
| `PrimaryLightColor` | `#BBDEFB` | Selected tab backgrounds, badges |
| `AccentColor` | `#FF6F00` | Accent orange — CTA buttons, accent border |
| `SuccessColor` | `#4CAF50` | Green — success states, recommended badge |
| `WarningColor` | `#FF9800` | Amber — warnings, contradiction scores |
| `ErrorColor` | `#F44336` | Red — errors, danger buttons, failed sources |
| `SurfaceColor` | `#FFFFFF` | Card backgrounds |
| `BackgroundColor` | `#F5F5F5` | Main content area background |
| `SidebarColor` | `#263238` | Dark sidebar |
| `SidebarTextColor` | `#ECEFF1` | Light text on sidebar |
| `TextPrimaryColor` | `#212121` | Primary body text |
| `TextSecondaryColor` | `#757575` | Secondary/muted text |
| `BorderColor` | `#E0E0E0` | Card borders, dividers |

### 10.2  Named Styles

| Style Key | Target | Description |
|-----------|--------|-------------|
| `PrimaryButton` | `Button` | Blue filled, white text, 16×8 padding, 4px corner radius, hover → dark blue, disabled → 50% opacity |
| `AccentButton` | `Button` | Inherits `PrimaryButton`, orange background |
| `DangerButton` | `Button` | Inherits `PrimaryButton`, red background |
| `SubtleButton` | `Button` | Transparent background, secondary text color, hover → 10% black overlay |
| `CopyButton` | `Button` | Inherits `SubtleButton`, 📋 content, 11px, "Copy to clipboard" tooltip |
| `ModernTextBox` | `TextBox` | 8×6 padding, 13px, white background, border color, 4px corners, blue border on focus |
| `SelectableText` | `TextBox` | Read-only, transparent, no border, wrapping, IBeam cursor — looks like TextBlock but allows select/copy |
| `Card` | `Border` | White background, 8px corners, 16px padding, 8px bottom margin, 1px border, subtle drop shadow (blur 4, depth 1, 10% opacity) |
| `SectionHeader` | `TextBlock` | 16px SemiBold, primary text color, 8px vertical margin |
| `SubHeader` | `TextBlock` | 13px Medium, secondary text color, 4px vertical margin |
| `SidebarTabItem` | `ListBoxItem` | 13px, 12×8 padding, hover overlay, selected → 20% white bg + blue left border |
| `Badge` | `Border` | 10px corner radius, 8×2 padding, `PrimaryLightBrush` background |
| `BusyIndicator` | `ProgressBar` | Indeterminate, 3px height, primary foreground, transparent background |
| `ModernDataGrid` | `DataGrid` | Read-only, single select, horizontal gridlines, no row headers |

---

## 11  ViewModel Architecture

### 11.1  `MainViewModel`

| Property | Type | Purpose |
|----------|------|---------|
| `CurrentView` | `ObservableObject?` | Active view (Welcome/Session/Settings) |
| `Sidebar` | `SessionsSidebarViewModel` | Sidebar state |
| `StatusText` | `string` | Status bar text |
| `IsBusy` | `bool` | Global busy indicator |
| `IsOllamaConnected` | `bool` | Ollama health state |
| `OllamaStatus` | `string` | Ollama status description |

| Command | Action |
|---------|--------|
| `ShowHomeCommand` | Sets `CurrentView` to `WelcomeViewModel` |
| `ShowSettingsCommand` | Sets `CurrentView` to `SettingsViewModel` |

### 11.2  `SessionWorkspaceViewModel` (partial class, 10 files)

The largest ViewModel, split across:

| File | Responsibility |
|------|---------------|
| `SessionWorkspaceViewModel.cs` | Core properties, tab management, notebook auto-save |
| `SessionWorkspaceViewModel.Research.cs` | Research run/pause/resume/cancel, live progress, continue research |
| `SessionWorkspaceViewModel.Evidence.cs` | Evidence search, pinning, sorting, source health |
| `SessionWorkspaceViewModel.NotebookQa.cs` | Q&A follow-up questions |
| `SessionWorkspaceViewModel.Crud.cs` | CRUD operations for notes, snapshots, reports, jobs, artifacts |
| `SessionWorkspaceViewModel.Export.cs` | ZIP export, HTML export, research packet |
| `SessionWorkspaceViewModel.DomainRunners.cs` | Discovery, Materials, Programming, Fusion domain runners |
| `SessionWorkspaceViewModel.Verification.cs` | Citation verification, contradiction detection |
| `SessionWorkspaceViewModel.HiveMind.cs` | Cross-session search, Hive Mind Q&A, curation, promotion |
| `SessionWorkspaceViewModel.RepoIntelligence.cs` | Repo scanning, Q&A, project fusion |

### 11.3  Sub-ViewModels (`SessionWorkspaceSubViewModels.cs`)

24 lightweight ViewModels for data binding:

| ViewModel | Purpose |
|-----------|---------|
| `TabItemViewModel` | Tab definition (emoji, name, tag, group) |
| `JobViewModel` | Research job display |
| `SnapshotViewModel` | Web snapshot display |
| `NotebookEntryViewModel` | Note with edit toggle (`IsEditing`) |
| `EvidenceItemViewModel` | Search result with score, pin state |
| `ReportViewModel` | Report metadata + type |
| `ReplayEntryViewModel` | Timeline step |
| `IdeaCardViewModel` | Discovery idea with scores |
| `MaterialCandidateViewModel` | Material with safety, properties, test checklist |
| `ApproachViewModel` | Programming approach with IP info, recommended flag |
| `ArtifactViewModel` | Ingested file metadata |
| `CaptureViewModel` | OCR capture |
| `FusionResultViewModel` | Fusion output with provenance, safety, IP notes |
| `RepoProfileViewModel` | Full repo profile (strengths, gaps, complements, proof banner) |
| `QaMessageViewModel` | Q&A pair with `ModelUsed` attribution |
| `GlobalChunkViewModel` | Hive Mind chunk for curation |
| `SourceHealthViewModel` | URL fetch status |
| `SearchEngineHealthViewModel` | Search engine status tile |
| `CrossSessionResultViewModel` | Cross-session evidence search result |
| `CrossSessionReportResultViewModel` | Cross-session report search result |
| `CitationVerificationViewModel` | Citation verification result |
| `ContradictionViewModel` | Contradiction detection result |
| `ProjectFusionViewModel` | Project fusion artifact |
| `SessionItemViewModel` | Sidebar session item |

---

## 12  UX Patterns

### 12.1  Async Safety
- All commands are `[RelayCommand]` (auto-generates `ICommand` with `CanExecute` support)
- Long-running operations use `async Task` — no UI thread blocking
- `IsBusy` / `IsResearchRunning` / `IsMaterialsRunning` etc. disable buttons during execution
- Live progress updates dispatched to UI thread

### 12.2  Progressive Disclosure
- Tabs filtered by domain pack (unnecessary tools hidden)
- New session panel initially collapsed
- Live progress panel appears only during research
- Post-research tips dismissed after acknowledgment
- Expandable Q&A history items
- Empty-state messages guide users when lists are empty

### 12.3  Data Binding Patterns
- **Tab switching**: `EqualityToVisibilityConverter` with `ConverterParameter` matching `ActiveTab` string to each tab's tag
- **Conditional visibility**: `BoolToVisibilityConverter` for boolean states, `NullToVisibilityConverter` for optional data, `CountToVisibilityConverter` for empty collections
- **Status colors**: Hex strings from ViewModel → `StringToColorBrushConverter` → `SolidColorBrush`
- **Inline placeholders**: Watermark text via `DataTrigger` on TextBox emptiness (custom pattern, not adorners)

### 12.4  Clipboard & Selection
- `SelectableText` style enables text selection on all read-only content (looks like TextBlock, behaves like TextBox)
- 📋 Copy buttons on repo profiles, fusion results, Q&A answers
- Activity log supports `Ctrl+C` via `KeyDown` handler
- Hyperlinks in repo complements are clickable (`RequestNavigate` handler opens in browser)

### 12.5  Model Attribution (Phase 13)
- All AI-generated outputs carry `ModelUsed` metadata
- `QaMessageViewModel.ModelUsed` displayed in Q&A tab
- `RepoProfileViewModel.AnalysisModel` displayed in profile exports
- Reports, jobs, and Q&A messages persist model name to SQLite

### 12.6  Sorting & Filtering
- Snapshots: Newest / Oldest / A-Z
- Evidence: Score / A-Z / Source
- Sessions: real-time search via `SearchText`
- Hive Mind chunks: source type filter + pagination

---

## 13  File Structure

```
src/ResearchHive/
├── App.xaml                          # Application resources, startup
├── MainWindow.xaml                   # Shell layout (130 lines)
├── Controls/
│   └── MarkdownViewer.cs            # Markdig → FlowDocument renderer (402 lines)
├── Converters/
│   └── ValueConverters.cs           # 10 IValueConverter implementations (136 lines)
├── Resources/
│   └── Styles.xaml                  # Design system: colors, brushes, styles (240 lines)
├── Views/
│   ├── WelcomeView.xaml             # Landing page
│   ├── SessionsSidebarView.xaml     # Sidebar panel (212 lines)
│   ├── SessionWorkspaceView.xaml    # Main workspace — all 20 tabs (2,095 lines)
│   └── SettingsView.xaml            # Settings panel (452 lines)
└── ViewModels/
    ├── MainViewModel.cs             # Shell ViewModel (105 lines)
    ├── ViewModelFactory.cs          # DI-based VM creation
    ├── WelcomeViewModel.cs          # Welcome screen
    ├── SettingsViewModel.cs         # Settings persistence
    ├── SessionsSidebarViewModel.cs  # Session list + creation
    ├── SessionWorkspaceViewModel.cs # Core + 9 partial files
    │   ├── .Research.cs             # Research pipeline
    │   ├── .Evidence.cs             # Evidence search
    │   ├── .NotebookQa.cs           # Follow-up Q&A
    │   ├── .Crud.cs                 # CRUD operations
    │   ├── .Export.cs               # Export functions
    │   ├── .DomainRunners.cs        # Domain-specific runners
    │   ├── .Verification.cs         # Citations + contradictions
    │   ├── .HiveMind.cs             # Cross-session intelligence
    │   └── .RepoIntelligence.cs     # Repo scanning + project fusion
    └── SessionWorkspaceSubViewModels.cs  # 24 sub-ViewModels
```

---

## 14  Accessibility & Responsive Design

- **Minimum window size**: 900×600 pixels
- **Default window size**: 1400×800, centered on screen
- **Resizable sidebar**: 200–400px via `GridSplitter`
- **Scrollable content**: All tab content wrapped in `ScrollViewer`
- **Keyboard navigation**: Global shortcuts (Ctrl+N/H/,/Esc), standard Tab key navigation
- **Text selection**: All content uses `SelectableText` style for universal copy support
- **Font hierarchy**: Segoe UI — 28px titles, 22px session title, 20px tab headers, 16px section headers, 13px body, 12px secondary, 11px captions, 10px timestamps, 9px micro
