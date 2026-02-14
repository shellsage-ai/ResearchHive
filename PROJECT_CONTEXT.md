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
- **45 DI registrations** (39 unique concrete services) across LLM routing, web search, evidence capture, indexing, retrieval, domain runners, repo intelligence, global memory, deterministic verification, fusion post-verification, and GitHub discovery
- **22 UI tabs** in the session workspace, visibility filtered by 7 domain packs
- **3 SQLite databases**: per-session (20 tables), global registry, global Hive Mind (FTS5-indexed)
- **8 cloud LLM providers** + local Ollama with configurable routing strategies + model tiering (Default/Mini/Full) + LLM circuit breaker
- **7-layer deterministic fact sheet pipeline** for zero-hallucination repo scans
- **4-layer anti-hallucination pipeline** for complement research filtering
- **6-step factual accuracy hardening** for scan outputs (strength grounding, self-validation, prompt precision) and fusion outputs (FusionPostVerifier with 5 validators, cross-section consistency)
- **651 tests** (xUnit + FluentAssertions + Moq) — 651 passed, 0 failures

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
