# ResearchHive (Windows) — Agentic Research + Discovery Studio (Autonomous Build Pack)

This repo is a **project-generation pack** (specs + agent prompts) intended for an implementation agent (e.g., GitHub Copilot / Claude Code / etc.) to generate the full Windows application.

## How to use (Copilot / VSCode)
1. Create an empty repo folder and unzip this pack into it.
2. In VSCode, open the repo root.
3. Open `prompts/COPILOT_AUTONOMOUS_BUILD_PROMPT.txt` and paste the entire contents into Copilot Chat (Agent mode if available).
4. The agent must implement everything **end-to-end** without asking you for approvals between milestones.

## Autonomous delivery rules (non-negotiable)
- The agent MUST implement the full project to completion before stopping.
- The agent MAY implement internally in checkpoints/milestones, but it MUST NOT ask you to approve each one.
- If an ambiguity exists, the agent must make reasonable assumptions, document them in `PROJECT_PROGRESS.md`, and proceed.
- No placeholders, no stubs, no TODO-only features.
- Every milestone must keep the solution buildable.
- Outputs must be auditable: citations, logs, replay, readable reports.

## Repo goals
- Windows-native WPF .NET 8 + MVVM
- Free-first: local Ollama + deterministic automation tools
- Optional paid model usage via routing policy (off by default)
