# Reproducible signed Android packaging (ticket 76)

Acceptance (plan): *Artifact, source, dependencies and signing authority are traceable.*
Plan rules: the integration worker produces an unsigned or development-signed artifact; the
trusted signing step receives only the approved artifact and release metadata; the release
authority chooses track and rollout; a routine Forge task cannot publish by itself.

## Roles

| Step | Who | Tool | Holds keys? |
| --- | --- | --- | --- |
| Release-configuration build (AAB) | Integration worker (Forge Unity adapter or a build machine) | `ReleaseCandidateBuild` (`unity/Assets/Editor/Release`) | No (debug key only) |
| Reproducibility check | Integration worker | `ci/repro-build.sh` -> `AstraKingdoms.Release repro-compare` | No |
| Evidence and release record | Integration worker | `AstraKingdoms.Release release-record --forge-dir` | No |
| Package (source archive, provenance, tests, unsigned artifact) | Integration worker | `forge release package` | No |
| Owner approval of one candidate hash | Owner | `forge release approve` (Ed25519 approval key) | Approval key only |
| Signing with the **upload key** | Separate signing service, own OS account | `python -m forge.release.signer --config signer.toml sign` | Upload keystore (outside every git tree) |
| Track and rollout | Release authority | `forge release authorise-submission`, `forge release submit` | Authority key; Play service account token |

Play App Signing holds the app signing key; we only ever hold the upload key.

## Procedure (commands from the repository root)

```bash
# 1. Clean tree, pinned editor. Build twice and compare (needs Unity):
UNITY=/opt/unity/6000.0.xx/Editor/Unity astra-kingdoms/ci/repro-build.sh out/rc 7 1.0.0
#    -> out/rc/a/astra.aab, out/rc/b/astra.aab, out/rc/repro.json (AK-GATE-RESULT/1)

# 2. Offline gates + device evidence (sections in release/perf/README.md), then the record:
R="dotnet run --project astra-kingdoms/tools/AstraKingdoms.Release --"
$R release-record --build-manifest out/rc/a/release-build-manifest.json \
   --unity astra-kingdoms/unity \
   --ledger astra-kingdoms/unity/Assets/Resources/Ledger/asset-ledger.json \
   --ledger astra-kingdoms/art/ledger/placeholder-assets.ledger.json \
   --evidence out/rc/repro.json --evidence out/evidence/ledger.json --evidence out/evidence/store-text.json \
   --evidence out/evidence/data-safety.json --evidence out/evidence/perf-frame-pacing.json \
   --evidence out/evidence/sustained.json --evidence out/rc/size.json \
   --test-results astra-kingdoms/.build/test-results/*.trx \
   --declarations out/evidence/data-safety-draft.md --known-issues astra-kingdoms/release/operations/known-issues.md \
   --scope "V1 closed test: ..." --support-contact "support@<domain>" \
   --rollback "see release/operations/priority-patch-runbook.md" --restore-drill "<date, record link>" \
   --store-release --out out/rc/release-record.json --md-out out/rc/release-record.md --forge-dir out/rc/forge

# 3. Forge package (unsigned) from exactly those inputs:
forge release package --commit "$(git rev-parse HEAD)" --artifact out/rc/a/astra.aab \
   --package-id com.rivailabs.astrakingdoms --version-name 1.0.0 --version-code 7 \
   --rules-version AK-TR-1 --assets out/rc/forge/forge-assets.json \
   --provenance out/rc/forge/forge-provenance.json --tests out/rc/forge/forge-tests.json \
   --dependency-manifest astra-kingdoms/unity/Packages/packages-lock.json --out out/rc/package
python -m forge.release.package verify out/rc/package/package-manifest.json

# 4. Owner approval (refuses while blockers remain: missing record fields, non-PASS tests,
#    no passing device evidence, incomplete provenance without a recorded exception):
forge release approve --manifest out/rc/package/package-manifest.json \
   --record out/rc/forge/forge-record.json --key ~/.forge/keys/owner-approval.key --out out/rc/approval.json

# 5. Separate signer (its own account; keystore and password file outside every git work tree):
python -m forge.release.signer --config /home/release/signer.toml sign \
   --artifact out/rc/package/com.rivailabs.astrakingdoms-1.0.0-unsigned.aab \
   --approval out/rc/approval.json --out /home/release/out
#    exit 0 signed, 2 refused (hash/approval mismatch), 3 BLOCKED (jarsigner/apksigner missing)

# 6. Release authority: track and rollout, then publish exactly the signed file:
forge release authorise-submission --signing-record /home/release/out/<record>.json \
   --track alpha --status completed --key ~/.forge/keys/authority.key --out out/rc/authorisation.json
forge release submit --authorisation out/rc/authorisation.json --signing-record ... \
   --artifact ... --token-file ... --trusted-key <authority public key hex>
```

`packages-lock.json` is written by the Unity editor on first open; until then the dependency
manifest is `unity/Packages/manifest.json` (versions not yet resolved), which Forge records but
which does not prove the resolved dependency set.

## What makes a build traceable here

- `release-build-manifest.json`: commit (refuses a dirty tree in `release-record`), Unity version,
  rules version and hash, version name/code, artifact SHA-256, signing statement.
- `build-report.json`: Unity's own file list and packed-asset sizes for the same build.
- `repro.json`: two builds of the commit agree entry by entry (signature files excluded); any
  difference is listed and fails unless allowlisted with a reason (`repro-allowlist.json`).
- `release-record.json` / Forge `package-manifest.json`: candidate hash binding source archive,
  assets, dependency manifest, evidence and the unsigned artifact; the signer only signs the
  artifact whose hash the owner approved.

Status: tooling done and unit-tested; **no Unity build, no two-build comparison, no signing** has
run (needs the pinned editor, an upload keystore and the Play Console app).
