#!/usr/bin/env bash
# Ticket 74: device-specific compressed initial download for the reference phone.
#
#   ci/size-check.sh <release-output-dir> <device-spec.json> [installed-bytes]
#
# <release-output-dir> is a ReleaseCandidateBuild output (astra.aab, build-report.json).
# <device-spec.json> comes from the reference phone: `bundletool get-device-spec --output=ref.json`.
# Needs bundletool (java -jar bundletool.jar) and a debug keystore for build-apks (local testing
# only; the measured download does not depend on the signing key). Installed size is read on the
# phone after `bundletool install-apks` (Settings > Apps > Storage, or `adb shell dumpsys diskstats`)
# and passed in as bytes.
set -euo pipefail
DIR="${1:?release output dir}"
SPEC="${2:?device spec json}"
INSTALLED="${3:-}"
BUNDLETOOL="${BUNDLETOOL:-bundletool}"
AK="$(cd "$(dirname "$0")/.." && pwd)"

$BUNDLETOOL build-apks --bundle="$DIR/astra.aab" --output="$DIR/astra.apks" --overwrite
$BUNDLETOOL get-size total --apks="$DIR/astra.apks" --device-spec="$SPEC" > "$DIR/download-size.csv"

ARGS=(--aab "$DIR/astra.aab" --bundletool-size "$DIR/download-size.csv" --device-spec "$(basename "$SPEC")"
      --build-report "$DIR/build-report.json" --out "$DIR/size.json")
[[ -n "$INSTALLED" ]] && ARGS+=(--installed-bytes "$INSTALLED")
dotnet run --project "$AK/tools/AstraKingdoms.Release" -c Release -- size-check "${ARGS[@]}"
