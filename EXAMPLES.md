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
