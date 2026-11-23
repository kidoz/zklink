# Release the NuGet packages

The release workflow validates the tag and changelog, downloads the original
package from a successful shared-candidate run for that exact source, verifies
its hashes and provenance, and runs installed-package examples against both
pinned ZooKeeper versions. The same job packs and tests `ZkLink.Extensions.DependencyInjection` and
`ZkLink.Analyzers` at the release version. Fresh .NET 10 and .NET 11 consumers
verify all three packages, DI registration, and analyzer diagnostics. One
checksum manifest covers all packages. A separate job publishes the three
packages through NuGet trusted publishing. A final job checks public-only
installation of all three and the
indexed core/DI PDBs.

Build and test jobs have read-only permissions. Only the publish job receives
NuGet credentials through GitHub OIDC in the `nuget` environment.

The validation job refuses a tag whose commit is not contained in `main`, so
branch protection on `main` also bounds which sources can reach NuGet.
nuget.org adds its repository signature to published packages, which changes
their hash: verify build attestations against the assets retained by the
candidate and release runs, not against packages downloaded from nuget.org.

## Publish a version

Before preparing a new DI release, move declarations from the previous public
release into `PublicAPI.Shipped.txt` and set `PackageValidationBaselineVersion`
to that published version. Keep new declarations in `PublicAPI.Unshipped.txt`
while the candidate is under review.

Set the same `VersionPrefix` in all three projects under `src/`, add a dated version section to
`CHANGELOG.md`, and tag the committed release source as `vMAJOR.MINOR.PATCH`.

Run candidate verification for that tag if it has no successful candidate yet:

```bash
gh workflow run release-candidate.yml --ref v1.2.0 -f version=1.2.0
```

After it succeeds, dispatch the release workflow from main:

```bash
gh workflow run release.yml --ref main -f release_tag=v1.2.0 -f publish=true
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
