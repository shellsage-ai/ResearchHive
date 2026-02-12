# Data Model (SQLite per Session)

Core tables (MVP):
- artifacts, snapshots, captures, chunks, fts_chunks, embeddings, citations
- jobs, job_steps, claim_ledger, reports
- safety_assessments, ip_assessments

Rules:
- Everything searchable (FTS).
- Citations reference immutable ids + spans/boxes.
- Job state is persisted and resumable.
