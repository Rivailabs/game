"""CLI: ``forge release ...`` (Release 3). Signing itself is NOT here: it is the separate process
``python -m forge.release.signer``, run by the release authority with its own configuration."""

from __future__ import annotations

import json
import sys
from pathlib import Path


def _load(p):
    return json.loads(Path(p).read_text())


def cmd_release(args):
    if args.release_cmd == "keygen":
        from .keys import generate_key

        pub = generate_key(args.out)
        print(f"private key written to {args.out} (0600). Public key (add to the signer/publisher/updater "
              f"trusted list):\n{pub}")
        return
    if args.release_cmd == "package":
        from ..config import load_project_config
        from ..intake.repository import SpecRepository
        from ..planning.template import load_template
        from .package import PackageError, build_package

        cfg = load_project_config(args.project_file)
        repo = SpecRepository(cfg.data_dir / "specs" / cfg.project.id, cfg.project.id,
                              load_template(cfg.runtime.get("template", "turn-duel-2p")))
        spec = repo.approved()
        if spec is None:
            sys.exit("refused: no approved specification")
        try:
            m = build_package(repo=cfg.project.repo_path, commit=args.commit, out_dir=args.out,
                              package_id=args.package_id, version_name=args.version_name,
                              version_code=args.version_code, artifact=args.artifact, assets=_load(args.assets),
                              provenance=_load(args.provenance), tests=_load(args.tests), project_id=cfg.project.id,
                              rules_version=args.rules_version, spec_version=spec.version, spec_digest=spec.digest,
                              dependency_manifest=_load(args.dependency_manifest),
                              economy_version=args.economy_version)
        except PackageError as e:
            sys.exit(f"refused: {e}")
        print(f"candidate {m['candidate_hash']} (unsigned); provenance complete: {m['provenance_complete']}")
        for p in m["provenance_problems"]:
            print(f"  provenance: {p}")
        return
    if args.release_cmd == "approve":
        from .candidate import ApprovalRefused, ReleaseCandidate, ReleaseRecord, approve_release

        manifest = _load(args.manifest)
        record = ReleaseRecord(candidate=ReleaseCandidate.model_validate(manifest["candidate"]), **_load(args.record))
        try:
            appr = approve_release(record, approver=args.by, approver_key=args.key,
                                   provenance_complete=manifest["provenance_complete"],
                                   accepted_exceptions=args.accept_exception or [])
        except ApprovalRefused as e:
            sys.exit(f"refused: {e}")
        Path(args.out).write_text(json.dumps(appr, indent=2, sort_keys=True))
        print(f"approval for candidate {appr['candidate_hash']} written to {args.out}. Next (release authority): "
              "python -m forge.release.signer --config <signer.toml> sign --artifact <unsigned> "
              f"--approval {args.out} --out <dir>")
        return
    if args.release_cmd == "authorise-submission":
        from .store import SubmissionAuthorisation, SubmissionRefused, authorise

        rec = _load(args.signing_record)
        try:
            auth = authorise(SubmissionAuthorisation(rec["package_id"], rec["version_code"], rec["signed_sha256"],
                                                     args.track, args.status, args.fraction,
                                                     release_name=args.release_name or ""),
                             by=args.by, key_path=args.key)
        except SubmissionRefused as e:
            sys.exit(f"refused: {e}")
        Path(args.out).write_text(json.dumps(auth, indent=2, sort_keys=True))
        print(f"store submission authorisation written to {args.out}")
        return
    if args.release_cmd == "submit":
        from .store import GooglePlayClient, SubmissionBlocked, SubmissionRefused, submit, urllib_transport

        token = Path(args.token_file).read_text().strip()
        try:
            rec = submit(_load(args.authorisation), _load(args.signing_record), args.artifact,
                         GooglePlayClient(urllib_transport, lambda: token), trusted_authorities=args.trusted_key,
                         log_dir=args.log_dir)
        except (SubmissionRefused, SubmissionBlocked) as e:
            sys.exit(f"refused: {e}")
        print(f"submitted {rec['package_id']} {rec['version_code']} to {rec['track']} ({rec['release_status']})")


def register(sub) -> None:
    sp = sub.add_parser("release", help="R3 packaging, owner approval and separately authorised store submission")
    rs = sp.add_subparsers(dest="release_cmd", required=True)
    k = rs.add_parser("keygen", help="create an Ed25519 approval key (outside every repository)")
    k.add_argument("--out", required=True)
    p = rs.add_parser("package", help="reproducible source/provenance/test package + unsigned artifact")
    p.add_argument("--commit", required=True)
    p.add_argument("--artifact", required=True, help="unsigned .apk/.aab from the integration build")
    p.add_argument("--package-id", required=True)
    p.add_argument("--version-name", required=True)
    p.add_argument("--version-code", type=int, required=True)
    p.add_argument("--rules-version", required=True)
    p.add_argument("--economy-version")
    p.add_argument("--assets", required=True, help="JSON {path: sha256} of accepted release assets")
    p.add_argument("--provenance", required=True, help="JSON list of provenance records")
    p.add_argument("--tests", required=True, help="JSON list of {name, status, sha256, evidence_class}")
    p.add_argument("--dependency-manifest", required=True)
    p.add_argument("--out", required=True)
    a = rs.add_parser("approve", help="owner approval of one exact candidate for signing (signed record)")
    a.add_argument("--manifest", required=True)
    a.add_argument("--record", required=True, help="JSON release record (scope, device results, rollback, ...)")
    a.add_argument("--key", required=True)
    a.add_argument("--accept-exception", action="append", help="test name accepted despite a non-PASS status")
    a.add_argument("--out", required=True)
    s = rs.add_parser("authorise-submission", help="release authority: track and rollout for a signed artifact")
    s.add_argument("--signing-record", required=True)
    s.add_argument("--track", required=True, choices=["internal", "alpha", "beta", "production"])
    s.add_argument("--status", required=True, choices=["draft", "inProgress", "halted", "completed"])
    s.add_argument("--fraction", type=float)
    s.add_argument("--release-name")
    s.add_argument("--key", required=True)
    s.add_argument("--out", required=True)
    u = rs.add_parser("submit", help="publish exactly the authorised signed artifact (Google Play edits)")
    u.add_argument("--authorisation", required=True)
    u.add_argument("--signing-record", required=True)
    u.add_argument("--artifact", required=True)
    u.add_argument("--token-file", required=True, help="OAuth access token for the Play service account")
    u.add_argument("--trusted-key", action="append", required=True)
    u.add_argument("--log-dir", default="release-submissions")
    sp.set_defaults(fn=cmd_release)
