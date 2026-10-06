#!/usr/bin/env bash
# Run one device scenario from release/scenarios/scenarios.json on a physical phone (tickets 73, 75).
#
#   ci/device/run-scenario.sh <adb-serial> <scenario-id> <out-dir>
#
# Launches the installed DEVELOPMENT build with the scenario's intent extras (the existing
# automation runner reads them; see LaunchOptions/LaunchArgs), samples `dumpsys meminfo`,
# `dumpsys thermalservice` and `dumpsys battery` while it runs, waits for the app to exit or the
# FORGE_SCENARIO_RESULT line, then pulls the autoplay report, match records and logcat.
#
# Exit: 0 when the scenario printed PASS, 1 on FAIL, 3 when the evidence is incomplete (timeout,
# device disconnect, missing report). Incomplete evidence is never converted into a pass
# (plan: "Device disconnect, permission dialogs and interrupted runs are reported as incomplete").
#
# Not runnable in CI: needs a phone over adb. Requires adb and python3 on the host.
set -euo pipefail

SERIAL="${1:?adb serial}"
SCENARIO="${2:?scenario id}"
OUT="${3:?output directory}"
HERE="$(cd "$(dirname "$0")" && pwd)"
AK="$(cd "$HERE/../.." && pwd)"
PKG="${AK_PACKAGE:-com.rivailabs.astrakingdoms}"
ACTIVITY="${AK_ACTIVITY:-com.unity3d.player.UnityPlayerActivity}"
ADB=(adb -s "$SERIAL")

mkdir -p "$OUT"
read_scenario() {
  python3 - "$AK/release/scenarios/scenarios.json" "$SCENARIO" "$1" <<'PY'
import json, sys
doc = json.load(open(sys.argv[1]))
s = next((x for x in doc["scenarios"] if x["id"] == sys.argv[2]), None)
if s is None:
    sys.exit(f"unknown scenario {sys.argv[2]}")
key = sys.argv[3]
if key == "extras":
    print(" ".join(f"-e {k} {v}" for k, v in s["extras"].items()))
else:
    print(s[key])
PY
}

EXTRAS="$(read_scenario extras)"
TIMEOUT="$(read_scenario timeout_seconds)"
MEM_EVERY="$(read_scenario meminfo_interval_seconds)"
THERM_EVERY="$(read_scenario thermal_interval_seconds)"

"${ADB[@]}" get-state >/dev/null 2>&1 || { echo "INCOMPLETE: device $SERIAL not connected"; exit 3; }
{
  echo "scenario=$SCENARIO serial=$SERIAL model=$("${ADB[@]}" shell getprop ro.product.model | tr -d '\r')"
  echo "started_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ) video_capture=off"
  echo "battery=$("${ADB[@]}" shell dumpsys battery | grep -E 'level|AC powered|USB powered|temperature' | tr -d '\r' | tr '\n' ' ')"
} > "$OUT/conditions.txt"

"${ADB[@]}" logcat -c
"${ADB[@]}" shell am force-stop "$PKG"
"${ADB[@]}" shell rm -rf "/sdcard/Android/data/$PKG/files/automation" || true
# shellcheck disable=SC2086
"${ADB[@]}" shell am start -W -n "$PKG/$ACTIVITY" $EXTRAS > "$OUT/am-start.txt"

start=$(date +%s); last_mem=0; last_therm=0; result=""
while true; do
  now=$(date +%s)
  if ! "${ADB[@]}" get-state >/dev/null 2>&1; then echo "INCOMPLETE: device disconnected"; exit 3; fi
  if (( MEM_EVERY > 0 && now - last_mem >= MEM_EVERY )); then
    "${ADB[@]}" shell dumpsys meminfo "$PKG" > "$OUT/meminfo-$now.txt" || true; last_mem=$now
  fi
  if (( THERM_EVERY > 0 && now - last_therm >= THERM_EVERY )); then
    "${ADB[@]}" shell dumpsys thermalservice > "$OUT/thermal-$now.txt" || true
    "${ADB[@]}" shell dumpsys battery > "$OUT/battery-$now.txt" || true; last_therm=$now
  fi
  result="$("${ADB[@]}" logcat -d -s Unity:V | grep -o 'FORGE_SCENARIO_RESULT: [A-Z]*' | tail -1 || true)"
  pid="$("${ADB[@]}" shell pidof "$PKG" | tr -d '\r' || true)"
  if [[ -n "$result" || -z "$pid" ]]; then break; fi
  if (( now - start > TIMEOUT )); then echo "INCOMPLETE: timeout after ${TIMEOUT}s"; break; fi
  sleep 1
done

"${ADB[@]}" logcat -d > "$OUT/logcat.txt"
"${ADB[@]}" pull "/sdcard/Android/data/$PKG/files/automation/." "$OUT/" >/dev/null 2>&1 || true
"${ADB[@]}" pull "/sdcard/Android/data/$PKG/files/records/." "$OUT/records/" >/dev/null 2>&1 || true
echo "ended_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ) wall_s=$(( $(date +%s) - start ))" >> "$OUT/conditions.txt"

if [[ ! -f "$OUT/autoplay-report.json" ]]; then echo "INCOMPLETE: no autoplay report pulled"; exit 3; fi
case "$result" in
  *PASS) echo "PASS: $SCENARIO"; exit 0 ;;
  *FAIL) echo "FAIL: $SCENARIO"; exit 1 ;;
  *) echo "INCOMPLETE: no FORGE_SCENARIO_RESULT line"; exit 3 ;;
esac
