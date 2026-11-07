# Release the NuGet packages

The release workflow validates the tag and changelog, downloads the original
package from a successful shared-candidate run for that exact source, verifies
its hashes and provenance, and runs installed-package examples against both
pinned ZooKeeper versions. The same job packs and tests `ZkLink.Extensions.DependencyInjection` and
`ZkLink.Analyzers` at the release version. It verifies a fresh consumer of all
three, DI registration, analyzer diagnostics and a checksum manifest covering
all packages. A separate job publishes the three packages through NuGet trusted
publishing. A final job checks public-only installation of all three and the
indexed core/DI PDBs.

Build and test jobs have read-only permissions. Only the publish job receives
NuGet credentials through GitHub OIDC in the `nuget` environment.

## Publish a version

Set the same `VersionPrefix` in all three projects under `src/`, add a dated version section to
`CHANGELOG.md`, and tag the committed release source as `vMAJOR.MINOR.PATCH`.

Run candidate verification for that tag if it has no successful candidate yet:

```bash
gh workflow run release-candidate.yml --ref v1.0.3 -f version=1.0.3
```

After it succeeds, dispatch the release workflow from main:

```bash
gh workflow run release.yml --ref main -f release_tag=v1.0.3 -f publish=true
```

The workflow selects the most recent successful core candidate run for the
selected source. It reuses those original core bytes, packs the optional projects,
and freezes the complete verified artifact before publication. Only core and DI
produce companion symbol packages; analyzers are compiler-only.
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
for the remaining qualification scope. DI-specific Native AOT and full cross-platform qualification remain separate;
publication does not claim those unverified scopes.
