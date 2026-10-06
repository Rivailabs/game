#!/usr/bin/env bash
# Ticket 76: build the same commit twice in release configuration and compare the artifacts.
#
#   UNITY=/path/to/Unity ci/repro-build.sh <out-dir> [version-code] [version-name]
#
# Each build runs in a fresh copy of the project's Library-free checkout so cached import results
# cannot hide nondeterminism. Needs the pinned Unity editor with Android support (not available in
# CI). The comparison ignores signature files because the integration build is debug-signed and the
# store signature is applied later by Forge's separate signer.
set -euo pipefail
: "${UNITY:?set UNITY to the pinned Unity editor binary}"
OUT="${1:?output dir}"
CODE="${2:-1}"
NAME="${3:-0.1.0}"
AK="$(cd "$(dirname "$0")/.." && pwd)"
REPO="$(git -C "$AK" rev-parse --show-toplevel)"
COMMIT="$(git -C "$REPO" rev-parse HEAD)"
[[ -z "$(git -C "$REPO" status --porcelain)" ]] || { echo "FAIL: working tree is dirty"; exit 1; }
mkdir -p "$OUT"

for tag in a b; do
  work="$(mktemp -d)"
  git -C "$REPO" worktree add --detach "$work/src" "$COMMIT" >/dev/null
  FORGE_COMMIT="$COMMIT" "$UNITY" -batchmode -nographics -quit -projectPath "$work/src/astra-kingdoms/unity" \
    -buildTarget Android -executeMethod AstraKingdoms.EditorTools.Release.ReleaseCandidateBuild.BuildFromCommandLine \
    -releaseOutput "$OUT/$tag" -versionCode "$CODE" -versionName "$NAME" -logFile "$OUT/unity-$tag.log"
  git -C "$REPO" worktree remove --force "$work/src"
  rm -rf "$work"
done

dotnet run --project "$AK/tools/AstraKingdoms.Release" -c Release -- repro-compare \
  --a "$OUT/a/astra.aab" --b "$OUT/b/astra.aab" --ignore-signing \
  --allowlist "$AK/release/packaging/repro-allowlist.json" --out "$OUT/repro.json"
