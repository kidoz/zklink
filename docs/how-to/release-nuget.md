# Release the core NuGet package

The [release workflow](../../.github/workflows/release.yml) publishes `ZkLink`
and its companion symbol package. The optional dependency-injection and analyzer
packages have separate qualification and are not published by this workflow.

Update `VersionPrefix` (and `VersionSuffix` for a prerelease) in
`src/ZkLink/ZkLink.csproj`. Add a dated `## [VERSION] - YYYY-MM-DD` section to
`CHANGELOG.md`, review the public API baseline and commit the release source.
The tag must be `vVERSION`, for example `v0.1.0`.

Run the release checks before publishing:

```sh
python3 -B dev/test-release-version.py
python3 -B dev/release_version.py --tag v0.1.0
bash dev/test-core-package.sh 0.1.0
```

The package check requires the pinned .NET SDK, Docker with Compose, Python 3
and OpenSSL. It verifies metadata, dependencies, portable symbols and embedded
sources, then restores an isolated package-only consumer and runs the examples
on all nodes of ZooKeeper 3.8.7 and 3.9.6. It retains the package, symbols, checksums
and execution/cleanup evidence under `artifacts/packages/run.*/`.

The GitHub repository uses an environment named `nuget` with the `NUGET_USER`
secret set to the NuGet profile name. Its NuGet trusted-publishing policy must
match repository owner `kidoz`, repository `zklink`, workflow `release.yml` and
environment `nuget`, with permission to push new packages and versions.
The workflow exchanges a GitHub OIDC token for a temporary API key immediately
before pushing. No persistent NuGet API key is stored.

Publish a GitHub release for the committed tag. The workflow validates the tag,
project version and changelog, then waits for the complete build/live verification
and Native AOT workflows. It independently verifies the exact versioned package,
attests its provenance and uploads the packages and evidence. Only then does it
authenticate to NuGet and push the core package; the adjacent `.snupkg` is sent by
the same push. An existing version fails rather than being silently skipped.

A manual workflow run uses `0.0.0-manual` and produces verified artifacts without
authenticating to or publishing on NuGet. After a release run succeeds, check the
NuGet listing and restore the published version into a fresh consumer. Publication
does not widen the documented server, security or Native AOT qualification scope.

[How-to guides](README.md) · [Documentation home](../README.md)
