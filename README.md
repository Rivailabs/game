# Astra Kingdoms + Game Forge

This repository holds the two products described in
[`docs/PLAN.md`](docs/PLAN.md) (*Astra Kingdoms / Game Forge complete plan, baseline 1.0, 5 Oct 2026*):

| Folder | What it is |
| --- | --- |
| [`astra-kingdoms/`](astra-kingdoms/) | The game. Two-player hidden-choice bow duel and finger-drawn land cut on a 51,040-cell circular board. Engine-independent C# rules assembly (ruleset **AK-TR-1**), NUnit acceptance tests, simulator/bots and a Unity 6 client project. |
| [`game-forge/`](game-forge/) | The tool. Python orchestrator that turns approved, bounded tasks into reviewable code, test evidence and builds — task state machine, budget ledger, isolated git worktrees, protected checks, device evidence and a local review UI. Astra Kingdoms is its first proving project. |
| [`docs/`](docs/) | The source plan (`.docx` original and Markdown conversion). The plan's rules chapter is the source of truth for gameplay. |

## Status

See **[STATUS_AND_NEXT_STEPS.md](STATUS_AND_NEXT_STEPS.md)** for what is done, what is pending, what only you can do, and the recommended next steps.

## Current stage

The plan's first commitment is **Minimal Forge (R1) → Astra pilot**. This repository
implements that stage's software work. The plan's gates (physical Android phone runs,
five-tester pilot observation, owner approvals) are human/hardware steps that code alone
cannot satisfy and are tracked as open items in each folder's README.

## Quick start

```bash
# Game rules + tests (.NET 8 SDK)
cd astra-kingdoms && dotnet test

# Forge (Python 3.11+)
cd game-forge && pip install -e . && forge --help
```
