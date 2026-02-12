# Search Lanes + Polite Web Crawling / Browsing Rules (MVP)

## Goals
- Ensure the agent looks in the *right places* (credible sources, diverse viewpoints).
- Ensure browsing is polite and does not hammer sites.
- Ensure captured sources are reproducible and properly attributed.

## Search Lanes (by domain pack)

### General Research
- Encyclopedic/background: reputable reference works (used only for framing, not sole evidence).
- Primary/authoritative: official org sites, government, standards bodies, major institutions.
- Secondary: reputable journalism, expert analyses, textbooks (where available).
- Academic: peer-reviewed papers + preprints (clearly labeled).
- Contrarian lane: credible dissenting interpretations (clearly labeled and evaluated).

### History/Philosophy
- Primary sources (where possible) + credible editions/translations.
- Academic history/philosophy sources (journals, university presses).
- Historiography lane: competing interpretations/schools of thought.
- Timeline lane: dates/events with multiple confirmations.

### Math
- Textbooks, lecture notes from credible institutions, peer-reviewed papers.
- Proof verification lane: attempt counterexamples / sanity checks.
- Computation lane: numeric validation (where applicable).

### Maker/Materials & Chemistry-Safe
- Standards and datasheets (SDS, material datasheets, ASTM/ISO references if available).
- Peer-reviewed materials research and reputable engineering references.
- Maker lane: reputable practitioner writeups (clearly tagged).
- Safety lane: SDS and official safety guidance.
- No procedural enablement for hazardous steps.

### Programming Research + IP
- Official docs + specs (preferred).
- Standards/RFCs (often safest).
- Benchmarks + performance writeups.
- Reputable engineering blogs + conference talks.
- OSS conceptual lane: repos for *conceptual understanding*; avoid copying verbatim code.
- License lane: LICENSE files / headers / notices when present.

## Diversity requirement (for “broader views”)
For any non-trivial conclusion, the agent must:
- include at least 2 independent high-quality sources, and
- intentionally search for credible alternative explanations or dissenting views, and
- explain why the “most supported” view is favored (evidence weighting).

## Polite browsing / crawling rules (mandatory)
- Concurrency limits:
  - max 2 simultaneous fetches overall by default
  - max 1 concurrent fetch per domain
- Delay/jitter:
  - wait 1.5–3.0 seconds between requests to the same domain
  - add jitter and exponential backoff on failures (429/503/timeouts)
- Respect access boundaries:
  - do not bypass paywalls, logins, or anti-bot protections
  - do not scrape private data or attempt credential stuffing
  - do not circumvent robots or terms-of-service; if blocked, record it and move on
- Caching:
  - never refetch the same URL within a job if a snapshot already exists (unless user forces refresh)
  - dedupe by canonicalized URL + content hash
- Rate limiting and retries:
  - retry with exponential backoff (e.g., 2s, 4s, 8s, up to a cap)
  - circuit-break a domain after repeated failures; continue with other lanes
- Identification:
  - use a consistent, honest user agent string (no impersonation)
- Logging:
  - log each request with domain, timestamp, status, and delay used
  - record “blocked” or “paywalled” and suggest alternative sources

## Snapshot requirement
For any source used in a report:
- capture an immutable snapshot bundle
- cite using snapshot_id + span/offset, not just the live URL
