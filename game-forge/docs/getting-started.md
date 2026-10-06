# Getting started with Game Forge (outside users)

This guide is for someone who has never used Forge, on the **declared supported profile**:

| Item | Supported profile |
|---|---|
| OS | Linux x86-64, Ubuntu LTS (the exact release is recorded by `forge preflight`) |
| Engine | Unity 6 LTS, the patch pinned in `toolchain-manifest.json`, with Android Build Support |
| Rules toolchain | .NET 8 SDK |
| Other | Git, Python 3.11+, a physical Android phone with USB debugging |
| Template | `turn-duel-2p`: a turn-resolved two-player (or player-versus-bot) game |

Windows and macOS are **not** supported yet. Forge may run there, but nothing on those systems has been
validated, and a failure there is outside the support boundary (see `support-boundaries.md`).

Forge does not make design decisions for you. You approve the specification, every milestone, every visual
change and the release. Forge never invents a missing rule, never raises a spending limit and never signs or
publishes on its own.

## 1. Install and run the setup assistant

```bash
git clone <your copy of this repository>
cd game-forge
pip install -e '.[test,intake,release]'      # python-docx for .docx briefs, cryptography for signed approvals
forge setup
```

`forge setup` does three things and hides none of the results:

1. **Prerequisites.** Every item is OK, MISSING, MANUAL (you must act: an account, a licence decision or a phone
   prompt), NOT_VERIFIED (present but not provable from here, e.g. Unity activation) or OPTIONAL.
2. **Manual steps and terms.** Unity needs a Unity account and a plan that fits your revenue or funding. The
   Android SDK has its own terms. Forge lists them; it does not accept terms for you.
3. **A sample minimal build.** Forge generates the template's sample project in its data folder and runs its rules
   tests with your .NET SDK. Do not import your own project until this passes. The sample build is **not** a
   playable Android build; it proves the rules toolchain works.

The report is saved to `.forge/setup-report.json`. If something is MISSING, fix it and run `forge setup` again.

## 2. Create your project from the template

```bash
forge scaffold ~/games/rune-duel --name "Rune Duel"
cd ~/games/rune-duel && git init -b main && git add -A && git commit -m "scaffold"
export FORGE_PROJECT=~/games/rune-duel/forge/project.toml
forge init
```

`forge/project.toml` is yours. Set the spending caps (they start at 0, so nothing can be dispatched) and the
allowed providers before running any agent work. Use your own provider account or API key (BYOK); the provider
bills you directly.

## 3. Describe the game (document intake)

Write a brief in Markdown, plain text or Word (.docx). `templates/turn-duel-2p/examples/rune-duel-brief.md` is a
complete example. Use one heading per topic (Players, Match structure, Starting state, Actions, Turn timer,
Randomness, Winning and draws, Rematch, Screens, Audio, Languages, Balance, Release) and one bullet per rule.

```bash
forge spec ingest my-brief.md
forge spec status
```

Forge turns the brief into a versioned specification and asks questions instead of guessing:

- **missing rules** (for example, "What happens when both players reach 0 health together?");
- **contradictions** (two different starting-health values, "after round 12" when a match has at most 8 rounds,
  "four types" listing three, "can skip" vs "cannot skip");
- **vague wording** ("TBD", "etc.", "maybe");
- **features outside the template** (real-time play, more than two players, iOS, online multiplayer).

Answer in your own words; your answer becomes an owner requirement. Dismiss a false finding with a reason.

```bash
forge spec answer Q-miss-a38fd196 "If both players reach 0 in the same round, the match is a draw."
forge spec answer Q-cont-f1ec10a0 "Each player starts with 20 health." --overrides R6,R7
forge spec dismiss Q-ambi-aab46b39 --reason "Bonus damage is cut from this game."
forge spec approve                  # needs every blocking question resolved and the release countries
```

An optional model review (`forge spec ingest my-brief.md --model-pass <route>`) sends the brief to that provider;
it can add questions but cannot answer them.

When you edit the brief later, run `forge spec ingest` again. You get a change report. A change to something you
already approved is only *proposed*: the approved text stays in force until you `forge spec accept-change <id>`
(or `reject-change`). Approved acceptance cases are never edited; a changed rule gets a new case.

## 4. Approve a milestone backlog

```bash
forge plan --max-parallel 2        # backlog, estimate ranges, human checkpoints, a bounded parallel schedule
forge import .forge/plans/backlog-v1.json
forge trace                        # requirement -> task -> case -> check; exits 1 if anything is untraced
forge approve M1-rules             # each task still needs your approval before it runs
forge run
```

Estimates are ranges, not promises. Each task's reservation ceiling is the high end of its cost range, and your
caps still apply.

## 5. Review, play and package

- `forge review-server` opens the local review page (127.0.0.1 only).
- The lanes run as protected checks: `python -m forge.production ui|localization|audio-verify|balance-verify ...`.
  They produce evidence, never approvals: visual fit, translations and audio rights still need you.
- Android builds and phone evidence need Unity, its Android module and your phone. Without them those checks are
  BLOCKED or INCOMPLETE, never passed.
- Packaging, approval, signing and store submission are described in `release-and-signing.md`.

## 6. Keep your work yours

- `forge export --out ~/rune-duel-export` writes a git bundle, all records as JSON and the evidence files. It
  needs no Forge account or subscription to use.
- `forge telemetry status` shows that telemetry is off. It stays off unless you turn it on, and even then it sends
  only operational counts and timings.
- `forge diag preview` shows exactly what a diagnostic bundle would contain (redacted, no prompts, code, logs or
  media). Nothing is sent; `forge diag export --confirm <digest>` writes the file for you to share yourself.
- `forge uninstall` (a dry run unless you pass `--yes`) removes the application and keeps projects and data.

## What to record if something goes wrong

Independent-user trials are an R4 exit-gate measurement. Record where you got stuck, which step needed help and how
long the help took (`forge ledger hours ...`), and attach `forge diag preview` output if you choose to share it.
