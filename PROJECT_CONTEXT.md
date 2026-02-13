# Project Context: ResearchHive

## Purpose
Build a Windows-native, agentic research assistant that:
- Runs long-duration online research jobs from a prompt (plan → browse → snapshot → extract → index → synthesize → iterate)
- Stores everything in **session-scoped workspaces** (subjects) with reproducible citations
- Supports a **Discovery Studio** that generates and refines novel ideas (materials/maker, chemistry-safe, history, philosophy, math, programming research, etc.)
- Provides domain-aware tools (capability packs) so each subject feels “pro” for its domain
- Produces clean reports and “learning trails” users can revisit
- Analyzes GitHub repos via **Repo Intelligence** (clone, chunk, index, CodeBook, RAG Q&A) and fuses multiple repos into architecture documents
- Maintains a **Hive Mind** (global memory) that accumulates knowledge and strategies across all sessions for cross-session RAG

## Core promise
"Give it a research goal, let it run, then open a readable report with receipts you can click."

## Scale
- **35 registered services** (DI singletons) across LLM routing, web search, evidence capture, indexing, retrieval, domain runners, repo intelligence, and global memory
- **22 UI tabs** in the session workspace, visibility filtered by 7 domain packs
- **3 SQLite databases**: per-session (20 tables), global registry, global Hive Mind (FTS5-indexed)
- **8 cloud LLM providers** + local Ollama with configurable routing strategies
- **327 tests** (xUnit + FluentAssertions + Moq)

## Principles
- Evidence over vibes
- Capture once, cite forever
- Session isolation by default (with opt-in global promotion via Hive Mind)
- Transparent logs: what happened and why
- Safety/IP marking for relevant domains
- Learn from every run (strategy extraction)

## Key Documentation
- `CAPABILITY_MAP.md` — Authoritative feature index: every capability → files, status, rationale, tests
- `PROJECT_PROGRESS.md` — Milestone completion history and assumptions log
- `EXAMPLES.md` — User-facing examples and walkthroughs for all domain packs
- `agents/orchestrator.agent.md` — Enforcement rules for keeping documentation in sync
