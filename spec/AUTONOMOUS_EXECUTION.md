# Autonomous Execution Contract (Critical)

## Goal
The implementation agent must deliver the FULL working application in one run, without asking the user for approvals between milestones.

## Rules
1) The agent MUST run all milestones in `spec/MILESTONE_PLAN.md` sequentially without stopping.
2) The agent MUST NOT pause to ask for approval; it must proceed automatically.
3) If blocked by missing info, the agent must:
   - pick a reasonable default
   - record the assumption in `PROJECT_PROGRESS.md` under Assumptions Log
   - proceed
4) Keep the project buildable at all times.
5) Finish criteria:
   - solution builds and runs
   - end-to-end flows verified per `spec/END_TO_END_DEMO.md`
   - reports and replay open from session hub
   - packaging output produced (portable zip or installer)

## Final output (single response at the end)
- Build/run commands
- Demo checklist results
- Notable assumptions
- Where to change settings (models, paths, etc.)
