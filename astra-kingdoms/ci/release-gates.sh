#!/usr/bin/env bash
# Offline release gates (no Unity, no phone): asset ledger, store text, Data safety draft.
# Writes AK-GATE-RESULT/1 files to <out-dir> (default .build/evidence) for `release-record`.
#
#   ci/release-gates.sh [out-dir] [--store-release]
#
# Exit status is the worst gate: 0 pass/warn, 1 fail, 3 incomplete. Today the Data safety gate is
# INCOMPLETE by design (vendors not chosen), so CI runs this script for its report, not as a
# blocking check; a store release requires it to be 0.
set -uo pipefail
AK="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-$AK/.build/evidence}"
STORE="${2:-}"
mkdir -p "$OUT"
cd "$AK"
dotnet build tools/AstraKingdoms.Release -c Release -warnaserror >/dev/null
R=(dotnet "$AK/.build/AstraKingdoms.Release/bin/Release/net8.0/AstraKingdoms.Release.dll")

worst=0
note() { local c=$1; if (( c == 1 )); then worst=1; elif (( c == 3 && worst != 1 )); then worst=3; fi; }

LEDGER_ARGS=(--unity unity --ledger unity/Assets/Resources/Ledger/asset-ledger.json --ledger art/ledger/placeholder-assets.ledger.json)
[[ "$STORE" == "--store-release" ]] && LEDGER_ARGS+=(--require-approved)
"${R[@]}" ledger-validate "${LEDGER_ARGS[@]}" --out "$OUT/ledger.json"; note $?

LISTINGS=(--file release/store/listing.en.md)
[[ "$STORE" == "--store-release" ]] && LISTINGS+=(--file release/store/listing.hi.md --file release/store/listing.kn.md)
"${R[@]}" store-text-lint --rules release/store/store-lint-rules.json "${LISTINGS[@]}" --out "$OUT/store-text.json"; note $?

"${R[@]}" data-safety --data-map release/declarations/privacy-data-map.json \
  --sdk-inventory release/declarations/sdk-inventory.json \
  --md-out "$OUT/data-safety-draft.md" --out "$OUT/data-safety.json"; note $?

echo "offline release gates: exit $worst (0 pass/warn, 1 fail, 3 incomplete); evidence in $OUT"
exit $worst
