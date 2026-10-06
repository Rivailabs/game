#!/usr/bin/env bash
# Editor release gate (ledgers, asset budgets of both arenas, Assets/Art import presets, ABI).
#
#   UNITY=/path/to/Unity ci/unity-release-gate.sh [--store-release]
#
# Writes unity/Builds/release-gate.json (AK-GATE-RESULT/1). Exit 0 pass/warn, 1 fail, 3 incomplete.
# Needs the pinned Unity editor (not available in CI).
set -euo pipefail
: "${UNITY:?set UNITY to the pinned Unity editor binary}"
AK="$(cd "$(dirname "$0")/.." && pwd)"
EXTRA=()
[[ "${1:-}" == "--store-release" ]] && EXTRA+=(-storeRelease)
"$UNITY" -batchmode -nographics -projectPath "$AK/unity" \
  -executeMethod AstraKingdoms.EditorTools.Release.ReleaseGate.RunFromCommandLine "${EXTRA[@]}" -logFile -
