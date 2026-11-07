# Release the core NuGet package

The release workflow validates the tag and changelog, downloads the original
package from a successful shared-candidate run for that exact source, verifies
its hashes and provenance, and runs installed-package examples against both
pinned ZooKeeper versions. A separate job publishes the verified package and
adjacent symbols through NuGet trusted publishing. A final job checks a fresh
public-only installation and the indexed PDB.

Build and test jobs have read-only permissions. Only the publish job receives
NuGet credentials through GitHub OIDC in the `nuget` environment.

## Publish a version

Set `VersionPrefix` in `src/ZkLink/ZkLink.csproj`, add a dated version section to
`CHANGELOG.md`, and tag the committed release source as `vMAJOR.MINOR.PATCH`.

Run candidate verification for that tag if it has no successful candidate yet:

```bash
gh workflow run release-candidate.yml --ref v1.0.0 -f version=1.0.0
```

After it succeeds, dispatch the release workflow from main:

```bash
gh workflow run release.yml --ref main -f release_tag=v1.0.0 -f publish=true
```

The workflow selects the most recent successful candidate run for the selected
source. It publishes those original tested bytes; it does not repack them.
Duplicate package versions fail. Inspect the existing NuGet package and retained
run evidence before retrying a failed push.

For validation without publication, omit `publish=true`. This runs package and
live-example verification without NuGet login or push. A fresh dispatch uses the
current workflow; rerunning an old failed run retains its old definition.

## Production qualification

Sustained soak tests, controlled benchmarks and `release-evidence.json`/`.zip`
are independent production-qualification work. They do not block NuGet
publication. For 1.0.0 the owner explicitly requested publication without the
remaining soak tests and benchmarks. Their incomplete status must not be
reported as a pass or as full production qualification.

The [production evidence validator](../../dev/verify-release-evidence.py) retains
its strict six-hour-per-version and repeated-measurement requirements for a full
qualification report. See [release readiness](../reference/release-readiness.md)
for the remaining qualification scope. Optional dependency-injection and analyzer
packages have separate release requirements; this workflow publishes core ZkLink.
