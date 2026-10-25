#!/usr/bin/env bash
set -euo pipefail

task_root="$(cd "$(dirname "$0")/.." && pwd)"
task_output="$task_root/artifacts/jute-oracle"
task_jar="$task_output/zookeeper-jute-3.9.6.jar"
mkdir -p "$task_output/classes" "$task_output/csharp"
curl -fsSL https://repo.maven.apache.org/maven2/org/apache/zookeeper/zookeeper-jute/3.9.6/zookeeper-jute-3.9.6.jar -o "$task_jar"
python3 - "$task_jar" <<'PY'
import hashlib, pathlib, sys, zipfile
actual = hashlib.sha256(pathlib.Path(sys.argv[1]).read_bytes()).hexdigest()
if actual != '74f9cb1e4ba899ecc4b816201b4afd1c63c256705bd9176393fac4071597c75f':
    raise SystemExit('Jute artifact SHA-256 mismatch')
with zipfile.ZipFile(sys.argv[1]) as archive:
    manifest = archive.read('META-INF/MANIFEST.MF').decode()
if 'Implementation-Build: a355171b081b5b60749db8f19cca1528b0df936f' not in manifest:
    raise SystemExit('Jute artifact source commit mismatch')
PY
javac -cp "$task_jar" -d "$task_output/classes" "$task_root/tests/ZkLink.JavaOracle/JuteOracle.java"
java -cp "$task_jar:$task_output/classes" JuteOracle generate "$task_output/java.hex"
cmp "$task_output/java.hex" "$task_root/tests/ZkLink.Protocol.Tests/Fixtures/jute-records.hex"
cd "$task_root"
ZKLINK_ORACLE_OUTPUT="$task_output/csharp" dotnet test --project tests/ZkLink.Protocol.Tests -c Release
java -cp "$task_jar:$task_output/classes" JuteOracle verify "$task_output/csharp/jute-records.hex"
echo "Java generation and C#-to-Java verification passed. Artifacts: $task_output"
