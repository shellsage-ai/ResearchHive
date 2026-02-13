# ResearchHive — Example Sessions by Domain Pack

Ready-to-use examples for each domain pack. Copy the **Research Prompt** into the Research tab after creating a session.

---

## 1. General Research

| Field | Value |
|-------|-------|
| **Title** | Impact of Sleep Deprivation on Cognitive Performance |
| **Description** | Investigating how chronic sleep loss affects memory consolidation, decision-making, and reaction time in adults aged 18–65. |
| **Domain Pack** | General Research |
| **Tags** | `neuroscience, sleep, cognition, health` |
| **Research Prompt** | What does current research say about the effects of chronic sleep deprivation (less than 6 hours per night) on cognitive performance? Specifically, how does it impact working memory, executive function, and long-term memory consolidation? Include findings from both laboratory studies and real-world observational data. |

---

## 2. History & Philosophy

| Field | Value |
|-------|-------|
| **Title** | The Stoic Influence on Early American Constitutional Thought |
| **Description** | Tracing how Stoic philosophy — particularly Cicero, Seneca, and Marcus Aurelius — shaped the ideals of the Founding Fathers and the structure of the U.S. Constitution. |
| **Domain Pack** | History & Philosophy |
| **Tags** | `stoicism, constitution, founding fathers, political philosophy` |
| **Research Prompt** | How did Stoic philosophy influence the political thought of the American Founding Fathers? Trace the specific ideas from Cicero, Seneca, and Marcus Aurelius that appear in the Federalist Papers, the Declaration of Independence, and the Constitution. What role did Stoic concepts of natural law, civic duty, and mixed government play in shaping the American republic? |

---

## 3. Math & Formal Methods

| Field | Value |
|-------|-------|
| **Title** | Applications of Topological Data Analysis in Machine Learning |
| **Description** | Exploring how persistent homology and other TDA tools are used to extract shape-based features from high-dimensional data for classification and clustering tasks. |
| **Domain Pack** | Math & Formal Methods |
| **Tags** | `topology, TDA, persistent homology, machine learning` |
| **Research Prompt** | What are the current applications of Topological Data Analysis (TDA) in machine learning? Focus on persistent homology as a feature extraction method. How does it compare to traditional dimensionality reduction techniques like PCA and t-SNE? Include examples of real-world datasets where TDA has provided measurable improvements in classification or clustering accuracy. |

---

## 4. Maker & Materials

| Field | Value |
|-------|-------|
| **Title** | Best Resin Systems for High-Temperature 3D-Printed Tooling |
| **Description** | Evaluating photopolymer and thermosetting resin options for printing jigs, fixtures, and molds that must withstand sustained temperatures above 150°C. |
| **Domain Pack** | Maker & Materials |
| **Tags** | `3d printing, resin, high-temperature, tooling, composites` |
| **Research Prompt** | What are the best resin systems available for 3D-printed tooling that needs to withstand sustained temperatures above 150°C? Compare photopolymer resins (SLA/DLP) with thermosetting options. Include glass transition temperatures, heat deflection temperatures, mechanical properties after post-cure, and any compatibility with composite layup or injection molding processes. What are the tradeoffs between cost, resolution, and thermal performance? |

---

## 5. Chemistry (Safe)

| Field | Value |
|-------|-------|
| **Title** | Green Synthesis Routes for Silver Nanoparticles |
| **Description** | Reviewing plant-extract-mediated and other environmentally friendly methods for synthesizing silver nanoparticles, with attention to yield, particle size control, and safety considerations. |
| **Domain Pack** | Chemistry (Safe Subset) |
| **Tags** | `green chemistry, nanoparticles, silver, biosynthesis, safety` |
| **Research Prompt** | What are the most effective green synthesis methods for producing silver nanoparticles? Focus on plant-extract-mediated synthesis, comparing different plant sources in terms of reduction efficiency, particle size distribution, and stability. What are the safety considerations for handling silver nanoparticles at bench scale? Include information on required PPE, disposal protocols, and any known health hazards. How do green-synthesized nanoparticles compare to chemically reduced ones in antibacterial efficacy? |

---

## 6. Programming Research & IP

| Field | Value |
|-------|-------|
| **Title** | Lock-Free Concurrent Data Structures in Modern C++ |
| **Description** | Surveying lock-free and wait-free algorithms for queues, stacks, and hash maps, with analysis of patent landscape and open-source licensing. |
| **Domain Pack** | Programming Research & IP |
| **Tags** | `concurrency, lock-free, c++, data structures, patents` |
| **Research Prompt** | What is the current state of lock-free concurrent data structures in C++? Survey the leading algorithms for lock-free queues (Michael-Scott, LCRQ), stacks (Treiber), and hash maps (split-ordered lists). For each, describe the algorithm, its ABA-prevention strategy, and benchmark performance vs. mutex-based alternatives. Also investigate the patent landscape — are any of these algorithms or their variations covered by active patents? What open-source implementations exist and under what licenses? |

---

## Tips

- **Target Sources**: Start with 5–8 for a quick survey, use 12–15 for comprehensive coverage.
- After research completes, visit the **Evidence** tab to search and pin key findings.
- Use **Discovery Studio** to generate novel hypotheses from your collected evidence.
- Use **Idea Fusion** to blend insights across multiple research runs.
- Export your session as a **Research Packet** (ZIP) for offline sharing.

---

## 7. Idea Fusion Example

After completing a research session on **Lock-Free Concurrent Data Structures** (Programming Research & IP), use the Fusion tab to create novel proposals from your evidence.

| Field | Value |
|-------|-------|
| **Session** | Lock-Free Concurrent Data Structures in Modern C++ |
| **Fusion Mode** | Cross-Apply |
| **Template Used** | Cross-Domain Transfer |
| **Fusion Prompt** | Take the hazard-pointer memory reclamation technique from lock-free data structures and cross-apply it to GPU compute shader resource management. How could game engines or HPC frameworks use epoch-based reclamation to safely deallocate GPU buffer objects that are still in-flight on the command queue? Identify potential performance wins, safety concerns, and any IP/patent risks. |

**What to expect:** The Fusion Engine will pull evidence from your session's chunks, combine insights across the lock-free and GPU domains, and produce a structured proposal with:
- A novel approach description
- Provenance map linking each claim to its source evidence
- Safety flags for any hazardous or high-risk elements
- IP/patent analysis for referenced techniques

**Other good fusion prompts for this session:**

| Mode | Prompt Idea |
|------|-------------|
| **Blend** | Merge the Michael-Scott queue and LCRQ designs into a hybrid queue that auto-switches between bounded and unbounded modes based on contention level. |
| **Substitute** | Replace the CAS-based approach in Treiber stacks with a hardware transactional memory (HTM) path as primary and CAS as fallback. What are the tradeoffs? |
| **Optimize** | Given the benchmark data collected on lock-free hash maps, propose an optimized configuration that minimizes cache-line contention on 64-core NUMA systems. |

---

## 8. Repo Intelligence & Project Fusion — For-Dummies Guide

### What Is This?

The **Programming Research & IP** and **Repo Intelligence & Fusion** domain packs give you three new tabs:

| Tab | What it does |
|-----|-------------|
| **🔎 Repo Scan** | Analyze GitHub repos (tech stack, deps, strengths, gaps, complements) + **Ask questions about any repo** |
| **🏗️ Project Fusion** | Fuse multiple scanned repos into unified architectures, comparisons, or extension plans |
| **💻 Programming** | The existing programming research tab (approach matrices, IP analysis) — totally separate |

> **Important:** The original **🔗 Fusion** tab ("Idea Fusion Engine") is a **separate feature** that combines research/notes/evidence from your session. Repo Intelligence is about analyzing actual GitHub repos and fusing *projects*, not session data.

---

### Quick Start: Analyze a Single Repo

1. Create a session → pick **Programming Research & IP** or **Repo Intelligence & Fusion** as the domain pack
2. Go to the **🔎 Repo Scan** tab
3. Paste a URL into the **"Ask About a Repo"** section, e.g.:
   ```
   https://github.com/microsoft/semantic-kernel
   ```
4. Type a question and click **💬 Ask**

That's it. If the repo hasn't been scanned yet, it auto-scans first (fetches metadata, README, dependencies via GitHub API, then runs LLM analysis for strengths/gaps/frameworks), then answers your question using the full profile as context.

---

### Repo Q&A — Example Questions

These are the kinds of things you can ask about **any** GitHub repo:

| Question | What you get |
|----------|-------------|
| `What will this project benefit from?` | Specific libraries, patterns, and tools that would improve it, based on its actual gaps and tech stack |
| `What's the biggest weakness of this project?` | Honest assessment based on dependency analysis, missing tests, documentation gaps, etc. |
| `How should I extend this to add real-time features?` | Concrete architecture suggestions using the project's actual frameworks |
| `What similar projects exist that complement this?` | Named projects with URLs and explanations of what each adds |
| `Is this project production-ready? What's missing?` | Maturity assessment based on stars, issues, CI, testing, docs |
| `What would a plugin architecture look like for this?` | Extension point design based on the actual codebase structure |
| `Compare this to [other repo URL]` | If both are scanned, compares architecture, tech stack, community health |
| `What license risks should I worry about?` | Dependency license analysis based on parsed manifests |

**Pro tip:** You can keep asking follow-up questions about the same repo — it uses the cached profile so follow-ups are fast.

---

### Scanning Repos (Building Your Profile Library)

Beyond Q&A, you can scan repos to build a library of profiles for later fusion:

**Single scan:**
1. Under "Single Repo Scan", paste a URL → click **🔎 Scan**
2. The system fetches: metadata, README, all languages, dependency files (package.json, requirements.txt, .csproj, Cargo.toml, go.mod), parses them, then uses LLM to identify strengths, gaps, and frameworks
3. A profile card appears showing everything discovered

**Batch scan:**
1. Under "Batch Scan", paste multiple URLs (one per line):
   ```
   https://github.com/langchain-ai/langchain
   https://github.com/chroma-core/chroma
   https://github.com/microsoft/autogen
   ```
2. Click **🔎 Scan All** — scans each in sequence
3. All profiles appear as cards

---

### Project Fusion — The 4 Goals

Once you have 2+ scanned repos, go to the **🏗️ Project Fusion** tab. This is where you fuse *projects* together (not session research — that's the other Fusion tab).

| Goal | When to use it | Example |
|------|---------------|---------|
| **🔀 Merge** | You want to combine 2+ repos into one unified project | "Merge LangChain and AutoGen into a single agent framework" |
| **🔌 Extend** | One repo is your base, others add capabilities to it | "Extend my app with Chroma's vector search capabilities" |
| **📊 Compare** | You're choosing between repos and need a side-by-side | "Which is better for my use case — LangChain or LlamaIndex?" |
| **🏗️ Architect** | You want a new design inspired by the best parts of each | "Design a new system taking the best ideas from all three" |

---

### Project Fusion — Step by Step

1. **Scan your repos first** (Repo Scan tab)
2. Switch to the **🏗️ Project Fusion** tab
3. **Check the inputs** you want to fuse (checkboxes list all scanned profiles + prior fusion artifacts)
4. **Pick a template** from Quick Templates (or choose Custom):

| Template | Goal | What it does |
|----------|------|-------------|
| **Full Merge** | Merge | Combines all repos into one unified project, resolves duplicates, unifies tech stack |
| **Plugin Architecture** | Extend | First repo = core platform, others become plugins with defined extension points |
| **Best of Each** | Architect | Cherry-picks the strongest feature from each repo into a new design |
| **Gap Filler** | Extend | Uses the other repos specifically to fill gaps in the first repo |
| **Side-by-Side Comparison** | Compare | Detailed comparison of architecture, tech stack, testing, docs, community |
| **Ecosystem Blueprint** | Architect | Designs a microservices system where each repo becomes a service |
| **Custom** | Any | Write your own prompt |

5. **Write a focus prompt** (optional, but makes results much better):
   ```
   Focus on developer experience and API ergonomics. 
   I care most about TypeScript support and streaming responses.
   ```
6. Click **🏗️ Fuse Projects**

**Output:** A full architecture document with:
- Unified vision (2-3 paragraphs)
- Architecture proposal (components, layers, data flow)
- Tech stack decisions with rationale referencing which repo each came from
- Feature matrix (feature → which repo it came from)
- Gaps closed by the fusion
- New gaps/challenges introduced
- IP/licensing notes
- Provenance map (every decision traced to its source repo)

---

### Real-World Workflow: "Analyze a Repo + Find Ideas to Add"

This is the workflow for: *"I have a repo and I want to find great ideas/concepts to add to it."*

#### Method 1: Just Ask (Fastest)

1. Go to **🔎 Repo Scan** tab
2. Paste your repo URL
3. Ask: `What features, libraries, and architectural patterns would most improve this project?`
4. The system scans the repo, identifies its gaps, and gives specific recommendations
5. Follow up: `What open-source projects could I integrate to add those capabilities?`

#### Method 2: Scan + Research (More Thorough)

1. **Scan your repo** on the Repo Scan tab — note the **Gaps** section in the profile card
2. Switch to the **🔬 Research** tab
3. Use the gaps as your research prompt:
   ```
   What are the best approaches for adding [gap from scan] to a [language] project 
   using [framework]? Find open-source libraries, design patterns, and real-world 
   examples.
   ```
4. Set **Time Range** to "Past year" for the freshest ideas
5. Turn on **Source Quality Ranking** for higher-quality results
6. Set **Target Sources** to 10-15 for comprehensive coverage
7. After research completes, check **Evidence** tab for the best findings

#### Method 3: Scan + Complement + Fuse (Most Powerful)

1. **Scan your repo** — the system automatically finds **complementary projects** for each gap
2. **Scan the best complement repos** too (the URLs are in the profile card)
3. Go to **🏗️ Project Fusion**, select all scanned profiles
4. Use the **Gap Filler** template with focus prompt:
   ```
   My primary project is [your repo]. Use the other scanned repos to fill its gaps.
   Focus on practical integration — what would the combined API look like?
   ```
5. You get a full architecture doc showing exactly how to extend your project

#### Method 4: Keyword-Driven Research → Scan → Fuse

For when you already know what you want to add:

1. Go to **🔬 Research** tab
2. Research with specific keywords:
   ```
   Best real-time collaboration libraries for React applications 2025. 
   Compare CRDT-based approaches (Yjs, Automerge) vs OT-based (ShareDB).
   ```
3. Set **Time Range** to "Past year" or "Past month" for recent options
4. From the research results, identify promising projects
5. **Scan those repos** on the Repo Scan tab
6. **Fuse** your repo + the discovered repos using **Extend** goal

---

### Using Time Range and Source Quality

On the **🔬 Research** tab:

| Setting | Options | When to use |
|---------|---------|-------------|
| **Time Range** | Any time, Past year, Past month, Past week, Past day | Use "Past year" for fresh libraries/tools. Use "Any time" for established CS concepts. |
| **Source Quality** | On/Off toggle | Turn ON to rank sources by reliability (filters out low-quality blogs, prioritizes docs, papers, official repos) |
| **Target Sources** | 3–20 | Use 5 for quick survey, 12-15 for comprehensive. More = slower but wider coverage. |

---

### Example: Full Walkthrough

**Goal:** "I have a CLI tool written in Rust and I want to find ways to make it better."

1. **Create session** → Domain Pack: "Programming Research & IP" → Tags: `rust, cli, improvements`

2. **Scan your repo:**
   - Repo Scan tab → paste `https://github.com/yourname/my-cli-tool` → 🔎 Scan
   - Profile appears: Rust, 12 deps, strengths: "Fast parsing, good error handling", gaps: "No shell completions, no config file support, no plugin system"

3. **Ask questions:**
   - `What will this project benefit from the most?`
   - → "Based on its gaps: (1) clap's derive API for shell completions, (2) toml/serde for config files, (3) a trait-based plugin system similar to what cargo uses..."
   - `What's the best plugin architecture for a Rust CLI tool?`
   - → Detailed answer referencing your actual dependencies and framework

4. **Research the gaps:**
   - Research tab → `Best practices for plugin systems in Rust CLI tools 2024-2025. Compare trait objects, dynamic loading (libloading), and WASM-based plugins.`
   - Time Range: "Past year" | Source Quality: ON | Sources: 10

5. **Scan discovered projects:**
   - Scan `https://github.com/extism/extism` (WASM plugin system)
   - Scan `https://github.com/clap-rs/clap` (CLI framework)

6. **Fuse:**
   - Project Fusion tab → check all 3 repos → Goal: **Extend** → Focus: "Design a plugin architecture for my CLI tool using WASM for sandboxed extensions and clap for the command interface"
   - → Full architecture doc with unified vision, tech stack decisions, feature matrix, and provenance

---

### Tips & Tricks

- **Re-fuse fusions:** Prior fusion artifacts appear as inputs in Project Fusion. You can fuse a fusion with new repos for iterative refinement.
- **Cross-session:** Use **🧠 Hive Mind** to search across all sessions, ask questions against your global knowledge base, and promote key findings.
- **The two Fusions are different:** 🔗 Fusion = combines your *research evidence* into novel proposals. 🏗️ Project Fusion = fuses actual *GitHub repos* into architecture documents. Use both!
- **GitHub PAT:** For private repos or to avoid rate limits, add your GitHub Personal Access Token in Settings → `GitHubPat`.
