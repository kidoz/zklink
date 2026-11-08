# Pinned fixture JDK

The build and portable Native AOT workflows share this action. It installs
Temurin **25.0.5+8.0.LTS** from the exact archives in `temurin.json` using the
pinned `actions/setup-java` action's `jdkfile` distribution.

Archive cache keys include the OS, architecture, version and SHA-256. Both cache
hits and new downloads must match the committed size and SHA-256 before
installation. Downloads use temporary files and at most three attempts, each
with a 10-second connection timeout and a 90-second transfer timeout, with
2-second and 4-second backoffs. An outer 95-second process timeout bounds each
attempt. Failed or unverified downloads never become cache entries.

The GitHub release asset API is tried first; the second attempt uses its browser
download URL. Both routes serve the same asset from GitHub, so this handles
transient request failures without providing an independent mirror. A sustained
outage still fails the job when no verified cached archive is available.

Python 3.11+ and `curl` must be on PATH. The build's Ubuntu hosted runners provide
them; the portable Windows/macOS jobs already select Python 3.12 before this
action. No GitHub token is sent to either download route.

To update the JDK, review the exact assets and published checksum files in the
[Temurin release](https://github.com/adoptium/temurin25-binaries/releases/tag/jdk-25.0.5%2B8),
then update all pins together. Never resolve a moving `latest` version at runtime.
The checksum in the cache key makes a pin change select a fresh cache entry.

Run downloader failure/cache regression checks with:

```sh
python dev/test-pinned-java.py
```
