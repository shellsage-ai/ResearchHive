# Research Agent Loop (Persisted, Agentic Browsing)

State machine:
1) Intake
2) Plan
   - generate research questions + search lanes
   - define stopping criteria (coverage, contradictions, budget)
3) Search (multi-lane, diverse viewpoints)
   - execute lanes per `spec/SEARCH_LANES_AND_COURTESY.md`
   - enforce polite browsing rules (rate limits, delays, backoff, caching)
4) Acquire & Snapshot
   - capture immutable snapshot bundle for every candidate source used
5) Extract & Index
   - chunking + FTS + embeddings
6) Evaluate Coverage
   - identify gaps and contradictions
   - update plan and queries
7) Evidence Pack
   - top passages + citations + source-quality labels
8) Draft Answer (citation-first)
   - “Most Supported View” section
   - “Credible Alternatives / Broader Views” section
   - limitations and what would change the conclusion
9) Validate
   - no unsupported claims; label hypotheses explicitly
10) Produce Reports + Replay entries
    - executive summary, full report, activity report, replay timeline

Checkpoint after each acquired source and after each plan update.
