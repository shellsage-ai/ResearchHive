# Citations and Snapshot Provenance

## Requirement
Every substantive claim must be:
- backed by one or more citations, OR
- labeled hypothesis/speculation/assumption

## Citation types
- WebSnapshotCitation: snapshot_id + text offsets or section pointers (+ optional screenshot box)
- PdfCitation: artifact_id + page + span
- OCRImageCitation: capture_id + span id + box
- FileCitation: artifact_id + chunk/span

## UI behavior
Clicking a citation opens the exact location and highlights it.
