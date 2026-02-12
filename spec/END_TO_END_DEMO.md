# End-to-End Demo Checklist (Must pass before stopping)

1) Sessions hub: create 2 sessions (Programming + Materials), persist after restart, search by tag/title.
2) Evidence: snapshot a URL (offline view), OCR a screenshot (searchable + highlight).
3) Retrieval: keyword + semantic search; evidence panel pin/remove.
4) Polite browsing:
   - confirm per-domain concurrency and delays are enforced
   - confirm 429/503 triggers backoff
   - confirm cache prevents refetching same URL within a job
5) Research job:
   - run 3–5 sources, pause/resume, restart+resume
   - reports: exec/full/activity + replay
   - output includes “Most Supported View” and “Credible Alternatives / Broader Views” with citations
6) Discovery Studio: 3 idea cards + novelty check + scoring + export.
7) Programming: approach matrix + IP summary + design-around; avoid verbatim copying; provenance present.
8) Materials: property+filters -> ranked candidates with safety labels + citations; export and reopen.
9) Fusion: combine outputs into fused proposal with provenance; export.
10) Packaging: produce portable zip or installer with run instructions.
