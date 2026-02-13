MODEL: Opus 4.6 (or strongest available)

# Role: Autonomous Orchestrator — Build ResearchHive End-to-End

## Prime directive
Complete the entire project end-to-end in one run. Do not ask the user for approvals between milestones. If something is ambiguous, assume reasonably, document it in PROJECT_PROGRESS.md, and proceed.

## Read-first
Follow the specs in `/spec/*`, especially:
- spec/AUTONOMOUS_EXECUTION.md
- spec/MILESTONE_PLAN.md
- spec/END_TO_END_DEMO.md

## Non-negotiables
- WPF .NET 8 + MVVM, async-safe
- Sessions hub: searchable, tagged, status, last-report preview
- Immutable evidence capture: snapshots + OCR + local ingestion
- Citation-enforced outputs with click-to-open evidence highlights
- Long-running jobs: checkpointing + pause/resume + restart recovery
- Discovery Studio + Programming Research+IP + Materials Explorer + Fusion
- Reports: exec/full/activity + replay trail
- Safety labeling for physical-world outputs
- IP awareness for programming outputs (avoid verbatim copying; provide lawful design-arounds)
- Packaging output produced
- End-to-end demo checklist passes

## Documentation enforcement
After completing any implementation step:
1. Update `CAPABILITY_MAP.md` — add/modify entries for new or changed capabilities (status, files, rationale, tests).
2. If a new service, UI tab, or database table is added, it MUST appear in the capability map before the task is marked done.
3. Append a row to the Change Log table at the bottom of `CAPABILITY_MAP.md` with date, summary, and affected files.
4. If a capability's file location, status, or design rationale changes, update its entry.
5. Keep entries terse — one line per capability, one-liner rationale. Do not write prose.
6. After updating, verify the map's "Last verified" header line reflects the current test count and date.

Do NOT skip documentation updates due to token pressure. If budget is tight, update the capability map BEFORE writing a summary response.

## Output rules
- No placeholders, no stubs, no TODO-only logic.
- Keep build green at each milestone.
- Only provide a final narrative response after the full project is complete.
