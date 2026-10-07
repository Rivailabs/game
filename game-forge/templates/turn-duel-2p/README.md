# Template `turn-duel-2p`: turn-resolved two-player duel

The first supported Game Forge template (plan: "Product scope and operating assumptions"): a small,
turn-resolved, two-player or player-versus-bot game with a menu, shared rules library, match state, replay,
results and rematch. Astra Kingdoms is the first game in this family; `examples/rune-duel-brief.md` is the second.

## Files

| File | Read by | Purpose |
|---|---|---|
| `template.toml` | intake, planner, scaffold | modules, dependencies, milestones, checkpoints, estimate ranges, required rule topics, unsupported features |
| `scaffold/` | `forge scaffold` | the files a new project starts with (`__GAME_NAME__`, `__GAME_ID__`, `__NAMESPACE__` are substituted) |
| `examples/rune-duel-brief.md` | people, tests | a complete brief that produces no open questions |

## Modules

| Module | Milestone | Depends on | Checks | Human checkpoints |
|---|---|---|---|---|
| rules (shared rules library) | M1 | - | rules-tests | - |
| match_state (turn sequencing, timer, invalid actions) | M1 | rules | rules-tests | - |
| replay (seed + ordered commands -> state hash) | M1 | match_state | rules-tests | - |
| bot | M1 | rules, match_state | rules-tests | - |
| menu | M2 | - | unity-build | visual approval, device play |
| results | M2 | match_state | unity-build | visual approval, device play |
| rematch | M2 | results, match_state | rules-tests, unity-build | device play |
| ui lane | M2 | menu, results | ui-lane | visual approval, device play |
| audio lane | M2 | - | audio-lane | owner rights review |
| localization lane | M2 | ui | localization-lane | fluent-speaker review |
| balance lane | M3 | rules, bot, replay | balance-lane | owner judgement of flags |
| release | M3 | everything | release-package | release approval, store submission |

Optional lanes are planned only when a requirement routes to them; core modules are always planned.

## Rules a brief must cover

Players, turn structure, starting state, actions, how to win, draws, invalid actions, the turn timer,
randomness/seeding and rematch. A brief that leaves one out gets a NEEDS_INPUT question; Forge never fills it in.

## Out of scope for this template

Real-time play, more than two players, iOS, online multiplayer without an explicit module, open-world generation.
A brief that asks for these gets a question to remove the feature or record it as a separate expansion.

## Sample build

`dotnet test tests/Rules.Tests` in a scaffolded project builds the netstandard2.1 rules library and runs the
template checks (`TPL-*`: invalid commands rejected with a reason, turns resolve only when both players chose,
replay reproduces the final state hash, rematch swaps the first player). `SampleRules` is a placeholder, not a
game design. The Unity screens are stubs until a Unity editor is available.

## Changing the template

A feature enters the template only after it works in an integrated game and has reusable acceptance checks.
Bump `version` in `template.toml`; projects record the template version they were generated from in
`forge-template.json`, and the updater reports template migrations before applying an update.
