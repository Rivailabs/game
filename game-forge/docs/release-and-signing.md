# Packaging, approval, signing and store submission (R3)

Owner workflow steps 8 and 9, and the "Release and rollback procedure". Four separate actions by people, none
of which a Forge task can take by itself:

| Step | Who | Command | Output |
|---|---|---|---|
| Package | owner | `forge release package ...` | reproducible source archive, provenance bundle, test report, dependency manifest, **unsigned** artifact, `package-manifest.json` with the candidate hash |
| Approve | owner | `forge release approve ...` | an Ed25519-signed approval of that exact candidate for `signing` |
| Sign | release authority | `python -m forge.release.signer --config signer.toml sign ...` | signed artifact + archive record (signed sha256, certificate fingerprint, approval) |
| Submit | release authority | `forge release authorise-submission ...` then `forge release submit ...` | a Google Play edit for the named track and rollout |

## Package

A release candidate is one immutable combination of source commit, rules/economy versions, accepted assets,
dependency manifest, package id, version and test evidence. Its hash is the sha256 of that combination, so any
change (one asset byte, the artifact, a test status) is a new candidate needing a new approval.

The source archive is `git archive` of the exact commit, recompressed with a fixed gzip header, so the same inputs
give the same bytes. `python -m forge.release.package verify <manifest>` re-hashes everything.

Provenance: every release asset needs a record with path, sha256, source, rights, generator, date, terms,
transformations, attribution and territory flags. Missing records or a hash that does not match the asset bytes
are listed; an approval with incomplete provenance needs an explicit recorded exception.

## Approve

`forge release approve` refuses unless the release record has scope, data declarations, support contact, rollback
method, device results and the last restore drill; every test is PASS (INCOMPLETE or BLOCKED never counts, unless
you explicitly accept a named exception); there is passing physical-device evidence; and provenance is complete or
excepted. The approval names the candidate hash, artifact sha256, package id and version.

## Sign (separate process)

The signer is its own program with its own configuration, ideally under its own OS account:

```toml
# signer.toml - outside every repository
keystore = "/home/release/keys/upload.jks"     # Play App Signing: this is the UPLOAD key
key_alias = "upload"
store_password_file = "/home/release/keys/upload.pass"
archive_dir = "/home/release/archive"
trusted_approvers = ["<owner public key hex from forge release keygen>"]
```

It signs only when the artifact's sha256 equals the approved one, the approval's signature verifies with a trusted
key, its destination is `signing`, and that package/version was never signed from different bytes. The keystore and
password file must be outside every git work tree and mode 0600. It uses `apksigner` for `.apk` and `jarsigner` for
`.aab`; when the tool is missing the result is **BLOCKED** (exit 3). The password reaches the tool only as a
`file:` reference, the tool gets a minimal environment, and its output is scrubbed before archiving.

With Play App Signing, this is the upload-key signature; Google re-signs with the app-signing key.

## Submit (separately authorised)

The release authority signs a submission authorisation naming the signed sha256 from the signing archive, the
track (`internal`, `alpha`, `beta`, `production`), the release status and, for a staged rollout, the fraction.
Forge chooses no default fraction: ten percent of a very small audience is not an informative test. `submit` checks
the authorisation, the archive record and the artifact bytes, then runs the Play Developer API edits flow (create
edit, upload, set track, commit). Any failure deletes the edit. Nothing in the orchestrator imports or calls this.

## Verified here vs not

- Verified: reproducible packaging, approval signing/verification, real `jarsigner` signing and verification with a
  generated test keystore, archive records, all refusal paths, the Play edits flow against a fake transport.
- **Not verified:** `apksigner` (Android build-tools are not installed here; a scripted stand-in was used), a real
  upload keystore, and any real Google Play API call.
