# Automation and Tools

## Web capture
- Playwright .NET (primary)
- Snapshot bundles must include: html, extracted text, screenshot, meta.json
- Source viewer must work offline from the snapshot bundle

## Polite browsing (mandatory)
Implement the courtesy policy in `spec/SEARCH_LANES_AND_COURTESY.md`:
- per-domain concurrency limits
- per-domain delays with jitter
- exponential backoff + circuit breaker
- caching/dedupe to avoid repeated hits
- do not bypass paywalls/logins/anti-bot protections
- structured request logging

## OCR
- Must work on Windows 11 and include bounding boxes.
- Choose a practical MVP approach and implement end-to-end.

## Parsing
- HTML readability extraction
- PDF extraction with OCR fallback

## Logging
- Structured logs to SQLite + JSONL
