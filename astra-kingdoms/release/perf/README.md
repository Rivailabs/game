# Device performance, memory and sustained play (tickets 73 and 75)

Plan gates ("Physical device quality and release gates"), all **proposed budgets to validate**:

| Gate | Pass condition | Tool |
| --- | --- | --- |
| Frame pacing | 30 fps target; steady-state p95 frame time <= 35 ms; p99 and long stalls (> 100 ms) recorded separately | `perf-compare` |
| Memory | Peak Android **total PSS** <= 400 MB on the reference phone (dumpsys meminfo, not Profiler counters) | `perf-compare`, `sustained-collate` |
| Sustained play | 20-minute run of representative matches incl. effects and screen transitions; thermal degradation and crashes reported | `sustained-collate` |

Android emulators are unsupported by Unity and never authorise a release; `PerfGate` reports an
emulator, editor or unregistered phone as **INCOMPLETE**.

## 1. Register the reference phones (once, before detailed art)

Add one object per phone to `devices.json` (`AK-DEVICE-REGISTER/1`):

```json
{ "role": "reference-2gb", "model": "<Build.MODEL exactly as in an autoplay report>",
  "manufacturer": "", "chipset": "", "ram_mb": 2048, "android": "", "api_level": 0,
  "abi": "arm64-v8a", "graphics_api": "Vulkan|OpenGLES3", "resolution": "1600x720",
  "storage_free_gb": 0, "serial": "<adb serial, used only by the device scripts>", "recorded_utc": "" }
```

Collect with `adb shell getprop ro.product.model`, `ro.product.manufacturer`, `ro.soc.model`,
`ro.build.version.release`, `ro.build.version.sdk`, `ro.product.cpu.abi`, `adb shell wm size`,
`adb shell cat /proc/meminfo | head -1`, `adb shell df /data`. The graphics API is the one Unity
chose (Player log line "GfxDevice"). A second target phone gets `"role": "second-target"`.

## 2. Run the scenarios (development build on the phone)

Scenarios live in `release/scenarios/scenarios.json` and use only launch extras the existing
automation runner understands. The device scripts install nothing; build and install first
(`Forge.Build.BuildAndroid`, `adb install -r Builds/Android/astra.apk`).

```bash
ci/device/run-scenario.sh  <serial> pilot-smoke     out/perf/pilot-smoke     # cold smoke
ci/device/run-scenario.sh  <serial> frame-pacing    out/perf/frame-pacing    # ticket 73
ci/device/run-scenario.sh  <serial> sustained-20m   out/perf/sustained       # ticket 75
```

Each run directory holds `autoplay-report.json`, `meminfo-<epoch>.txt`, `thermal-<epoch>.txt`,
`battery-<epoch>.txt`, `logcat.txt` and the pulled match records. Keep the phone unplugged from
fast chargers, at room temperature, screen brightness fixed, no video capture (record capture
conditions in the run's `conditions.txt`).

## 3. Compare and record

```bash
R="dotnet run --project tools/AstraKingdoms.Release --"
$R perf-compare --report out/perf/frame-pacing/autoplay-report.json --scenario frame-pacing \
   --meminfo-dir out/perf/frame-pacing \
   --baseline release/perf/baselines.json --devices release/perf/devices.json \
   --out out/evidence/perf-frame-pacing.json
$R sustained-collate --samples out/perf/sustained --report out/perf/sustained/autoplay-report.json \
   --cold-report out/perf/pilot-smoke/autoplay-report.json --logcat out/perf/sustained/logcat.txt \
   --out out/evidence/sustained.json
```

The first clean run on a registered phone is stored as the baseline only with a named approver:
`... perf-compare ... --update-baseline --approved-by "<owner>" --commit <sha>`. Later runs fail
on regression: p95 +10 %, p99 +15 %, +2 long stalls, PSS +5 % (`PerfTolerance`). Changing a
budget or tolerance is a reviewed change (plan: a change that disables a gate or changes a
threshold cannot earn a normal automatic pass).

## Known limits of this tooling

- The automation report aggregates frame times over the whole run. Thermal degradation is
  therefore measured as sustained-run p95 versus a cold `pilot-smoke` p95, plus the thermal status
  timeline. Per-match frame statistics in `AutomationReport` (client code) would allow a
  first-versus-last-segment comparison; that is a proposed follow-up for the client owner.
- Lifecycle recovery (ticket 75: background/foreground, lock screen, incoming call) is a manual
  checklist on the phone (`release/operations/rollout-checklist.md`, device section); the
  automation runner drives semantic commands, not OS interruptions.
- The 1 MB/min PSS trend warning in `sustained-collate` is a proposed heuristic, not a plan budget.

Status: **no reference phone registered, no device run, no baseline.** All device gates are
INCOMPLETE until then.
