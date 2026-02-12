# Session Workspace Specification

## Directory layout
`Sessions/<ISO_DATE>_<safe_title>_<short_id>/`
- Inbox/
- Artifacts/
- Snapshots/
- Captures/
- Index/
- Notes/
- Exports/
- Logs/
- session.json
- session.db

## Snapshot bundle format
`Snapshots/<snapshot_id>/`
- meta.json
- page.html
- page.txt
- screenshot.png
- extraction.json (optional)

## Immutability
- Artifacts written once and never modified.
- Citations reference immutable ids + spans/bounding boxes.
