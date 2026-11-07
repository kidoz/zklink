# Release the core NuGet package

The core **1.0.0 remains unqualified**. The publication workflow rejects missing,
failed or mismatched qualification evidence. Optional dependency-injection and
analyzer packages retain separate qualification.

Prepare the release source by reviewing compatibility against published 0.1.0,
updating `VersionPrefix` in `src/ZkLink/ZkLink.csproj`, and adding a dated
`## [VERSION] - YYYY-MM-DD` changelog section. Reconcile the package description,
operator documentation and tested support matrix. Compatibility metadata must
follow observed verification; it does not establish support by itself.

## Verify one candidate

Run [Release candidate verification](../../.github/workflows/release-candidate.yml)
on the committed release source with the requested package version. It packs and
inspects the package once, records its package/symbol checksums and runtime
inputs in `candidate.json`, and attests those bytes. Managed inspection and
installed consumers on three OS families and rooted/unrooted Native AOT checks
on four targets download the same bundle. Native online-revocation and secure
live checks are required. A supplied but invalid candidate never falls back to
a fresh package build.

The ordinary build/native workflows can still prepare local development
candidates. Those independent packages do not qualify a production release.

## Retain and review qualification

Complete the required recovery/security cases, at least six continuous hours on **each**
pinned server version, and repeated controlled benchmark measurements. Preserve
failed attempts and their resolutions. Driver or build-input changes require a
review of which measurements remain applicable.

Six hours is the chosen release minimum; optional completed runs up to 24 hours
per version remain eligible. This accepts less long-duration coverage than the
previous requirement. Unresolved failures still block publication, and interrupted
checkpoints cannot be relabelled as a completed workload.

Prepare `release-evidence.json` with the fields checked by
[the evidence validator](../../dev/verify-release-evidence.py):

- The immutable candidate manifest checksum and completed candidate workflow run ID.
- Reviewed managed/native target results and required behavior cases.
- Resolutions and proof for retained failures, with no unresolved release failures.
- Both full soak resource series, authenticated cleanup proof and measured input manifests.
- Controlled protocol, live-operation, watch fan-out and watch-recovery reports,
  including repetitions, machine/server settings, raw results and baseline comparisons.
- A checksum map for every referenced evidence file.

Measurements must match the candidate's recorded runtime inputs. The validator
also rejects short workloads, resource failures and gaps exceeding two minutes
in the retained resource series. Six-hour workloads sample about every 15 seconds;
24-hour workloads sample about once a minute. Reports describe observed measurements; they do not invent
throughput or latency requirements.

Put the referenced files in `release-evidence.zip`. Bundle safe results and
metadata; exclude credentials, private keys, session identifiers and request
payloads. Keep the original forensic evidence separately.

Check the bundle before preparing publication:

```sh
python3 -B dev/test-release-candidate.py
python3 -B dev/test-release-evidence.py
python3 -B dev/verify-release-evidence.py \
  --evidence artifacts/qualification \
  --candidate artifacts/candidate \
  --candidate-sha256 EXPECTED_MANIFEST_SHA256
```

## Publish the verified bytes

After all required checks pass, prepare a draft GitHub release for `vVERSION`
targeting the exact candidate commit. Attach `candidate.json`, the original
`.nupkg` and `.snupkg`, `release-evidence.json` and `release-evidence.zip`.
Publishing the GitHub release invokes [Release](../../.github/workflows/release.yml).
A manual run defaults to validation only. It checks out the selected `release_tag`,
then pins the package job to that exact commit, even when the workflow runs from
`main`. To validate an existing release or draft:

```sh
gh workflow run release.yml --ref main -f release_tag=v1.0.0
```

To explicitly request a fresh NuGet publication run for the same version:

```sh
gh workflow run release.yml --ref main -f release_tag=v1.0.0 -f publish=true
```

Both modes require the original candidate assets and complete qualification
evidence. The publication option enables trusted publishing and public-feed
verification only after those checks pass. Runs for the same release tag share
one concurrency group.

Check that every required asset is attached before publishing the draft:

```sh
PACKAGE_VERSION=VERSION QUALIFIED_RELEASE_TAG=vVERSION \
  GITHUB_REPOSITORY=kidoz/zklink \
  python3 -B dev/download-qualified-release.py --check-assets
```

The preflight lists missing or empty assets before the publication job starts.
It checks presence only; the full qualification below still verifies their contents.
An older candidate cannot qualify a later release commit, even when the runtime
source is unchanged. Run candidate verification on the exact intended release
commit before assembling its assets.

The publication job downloads these assets and the original candidate workflow
artifact. It requires the completed verification run to match the release source,
compares the package and symbols byte for byte, verifies their attestations and
checks the complete production evidence. It then runs the installed examples
against both pinned server versions using that same candidate. It does not
repack the publication package.

The repository's `nuget` environment supplies `NUGET_USER`, and its trusted
publishing policy must match owner `kidoz`, repository `zklink`, workflow
`release.yml` and environment `nuget`. The workflow exchanges its GitHub OIDC
token for a temporary API key immediately before pushing. The adjacent `.snupkg`
is sent by the same push. Existing versions fail rather than being silently
skipped.

After publication, the workflow checks the public package payload against the
candidate, restores a fresh public-only consumer and verifies the indexed PDB
against the qualified symbols. Indexing checks are bounded and retain their
results. Publication does not widen the documented qualification scope.

[How-to guides](README.md) · [Documentation home](../README.md)
