#!/usr/bin/env python3
"""Direct native Online/NoCheck controls using a disposable loopback CRL/OCSP authority."""

import json
import os
from pathlib import Path
import subprocess
import sys

from aot_artifacts import ROOT, read_manifest, sha256

PROCESS_GUARD_SECONDS = 120
CASES = ["Valid", "Revoked", "Unavailable", "InvalidSignature", "HeldCancellation"]


def main():
    manifests = [Path(sys.argv[1])] if len(sys.argv) == 2 else list((ROOT / "artifacts/aot").glob("run.*/native.json"))
    if len(sys.argv) > 2 or len(manifests) != 1:
        raise SystemExit("Supply exactly one native.json for this host")
    manifest, native = read_manifest(manifests[0])
    consumer = native["consumers"]["ZkLink.AotSmoke"]
    executable = manifest.parent / consumer["executable"]
    environment = os.environ.copy()
    environment.update(ZKLINK_AOT_EXPECTED_RID=native["rid"], ZKLINK_AOT_EXECUTABLE_SHA256=consumer["sha256"])
    log = manifest.parent / "native-revocation.log"
    with log.open("w", encoding="utf-8") as output:
        result = subprocess.run([sys.executable, str(ROOT / "dev/run-live-benchmark-process.py"),
                                 str(PROCESS_GUARD_SECONDS), str(executable), "--revocation-check"],
                                cwd=ROOT, env=environment, stdout=output, stderr=subprocess.STDOUT,
                                timeout=PROCESS_GUARD_SECONDS + 30)
    text = log.read_text(encoding="utf-8")
    checks = {case: "Native revocation case " + case + ": PASS" in text for case in CASES}
    receipt = {"rid": native["rid"], "native_manifest_sha256": sha256(manifest),
               "candidate_manifest_sha256": native["candidateManifestSha256"], "executable_sha256": consumer["sha256"],
               "package_sha256": native["packageSha256"], "runner_sha256": sha256(Path(__file__)),
               "log_sha256": sha256(log), "checks": checks, "exit_code": result.returncode,
               "passed": result.returncode == 0 and all(checks.values()) and "Native online revocation: PASS" in text}
    (manifest.parent / "native-revocation-result.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    if not receipt["passed"]:
        for line in text.splitlines():
            if line.startswith(("Native ", "Live benchmark process")):
                print(line, flush=True)
        raise SystemExit("Native online revocation failed; inspect " + str(log))
    print("Native Online revocation verification: PASS (" + native["rid"] + ")")


if __name__ == "__main__":
    main()
