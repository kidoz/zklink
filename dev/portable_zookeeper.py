"""Owned, loopback-only Java ZooKeeper fixtures for native CI without Docker."""

import json
import os
import pathlib
import re
import secrets
import shutil
import signal
import socket
import subprocess
import time
import xml.etree.ElementTree as ET

from aot_artifacts import ROOT, sha256

SERVER_VERSIONS = ("3.8.7", "3.9.6")
SECURITY_MODES = ("tls", "mtls")
COMMAND_TIMEOUT_SECONDS = 30
GRADLE_TIMEOUT_SECONDS = 600
PROBE_TIMEOUT_SECONDS = 15
STARTUP_TIMEOUT_SECONDS = 90
STOP_GRACE_SECONDS = 8
POLL_SECONDS = 0.2
SOCKET_TIMEOUT_SECONDS = 2
STATUS_MAX_BYTES = 16 * 1024
RSA_KEY_BITS = 2048
CERTIFICATE_VALID_DAYS = 2
TICK_MILLISECONDS = 2000
INIT_TICKS = 10
SYNC_TICKS = 5
JVM_HEAP_MIB = 192
JVM_PROCESSORS = 2
VOTER_COUNT = 3


class FixtureError(RuntimeError):
    """Safe, fixed diagnostic text; never include command output or credentials."""


class OwnedProcess:
    def __init__(self, command, output, environment=None, cwd=ROOT):
        self.output = output.open("wb")
        try:
            self.process = subprocess.Popen(
                list(map(str, command)), cwd=cwd, env=environment, stdout=self.output,
                stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                start_new_session=os.name != "nt",
                creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0)
        except BaseException:
            self.output.close()
            raise

    def wait(self, timeout):
        try:
            return self.process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            raise FixtureError("Owned fixture command exceeded its deadline.") from None
        finally:
            self.stop()

    def stop(self):
        try:
            if self.process.poll() is None:
                if os.name == "nt":
                    subprocess.run(["taskkill", "/PID", str(self.process.pid), "/T", "/F"],
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                   timeout=STOP_GRACE_SECONDS, check=False)
                else:
                    try:
                        os.killpg(self.process.pid, signal.SIGTERM)
                    except ProcessLookupError:
                        pass
                try:
                    self.process.wait(timeout=STOP_GRACE_SECONDS)
                except subprocess.TimeoutExpired:
                    if os.name == "nt":
                        self.process.kill()
                    else:
                        try:
                            os.killpg(self.process.pid, signal.SIGKILL)
                        except ProcessLookupError:
                            pass
                    self.process.wait(timeout=STOP_GRACE_SECONDS)
        finally:
            self.output.close()


def checked(command, output, environment=None, timeout=COMMAND_TIMEOUT_SECONDS, cwd=ROOT):
    child = OwnedProcess(command, output, environment, cwd)
    result = child.wait(timeout)
    if result != 0:
        raise FixtureError("Owned fixture command failed; inspect the retained command log.")


def java_tools(run):
    executable = "java.exe" if os.name == "nt" else "java"
    java_home = os.environ.get("JAVA_HOME")
    java = pathlib.Path(java_home) / "bin" / executable if java_home else pathlib.Path(shutil.which(executable) or "missing-java")
    keytool = java.with_name("keytool.exe" if os.name == "nt" else "keytool")
    checked([java, "-version"], run / "java-version.txt")
    if not re.search(r'version "25\.0\.5"', (run / "java-version.txt").read_text(encoding="utf-8")):
        raise FixtureError("The portable fixture requires the pinned JDK 25.0.5.")
    checked([keytool, "-version"], run / "keytool-version.txt")
    return java, keytool


def export_java(java, run):
    project = ROOT / "tests/ZkLink.JavaEnsemble"
    wrapper = ROOT / "tests/ZkLink.JavaOracle/gradle/wrapper/gradle-wrapper.jar"
    output = run / "java"
    # Invoke the pinned wrapper JAR directly; no platform shell or batch quoting is needed.
    checked([java, "-cp", wrapper, "org.gradle.wrapper.GradleWrapperMain", "-p", project,
             "--project-cache-dir", ROOT / "artifacts/java-ensemble-cache", "--no-daemon",
             "--warning-mode", "fail", "--dependency-verification", "strict",
             f"-PfixtureOutput={output}", "exportRuntime"], run / "gradle.txt",
            timeout=GRADLE_TIMEOUT_SECONDS)
    receipt = {}
    verification = ET.parse(project / "gradle/verification-metadata.xml")
    namespace = {"v": "https://schema.gradle.org/dependency-verification"}
    approved = {}
    for component in verification.findall(".//v:component", namespace):
        coordinate = ":".join(component.attrib[key] for key in ("group", "name", "version"))
        for artifact in component.findall("v:artifact", namespace):
            approved[(coordinate, artifact.attrib["name"])] = {item.attrib["value"].upper() for item in artifact.findall("v:sha256", namespace)}
    for version in SERVER_VERSIONS:
        folder = output / version
        receipt[version] = {str(path.relative_to(folder)): sha256(path)
                            for path in sorted(folder.rglob("*")) if path.is_file()}
        for line in (folder / "dependencies.tsv").read_text(encoding="utf-8").splitlines():
            filename, coordinate = line.split("\t")
            if sha256(folder / "lib" / filename) not in approved.get((coordinate, filename), set()):
                raise FixtureError("An exported runtime jar differs from its pinned verification metadata.")
    (run / "java-artifacts.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    for name in ["gradle.lockfile", "gradle/verification-metadata.xml"]:
        shutil.copy2(project / name, run / pathlib.Path(name).name)
    return output


def verify_runtime(runtime, run):
    expected = json.loads((run / "java-artifacts.json").read_text(encoding="utf-8"))
    actual = {version: {str(path.relative_to(runtime / version)): sha256(path)
                        for path in sorted((runtime / version).rglob("*")) if path.is_file()}
              for version in SERVER_VERSIONS}
    if actual != expected:
        raise FixtureError("An exported Java runtime changed during the portable matrix.")


def generate_pki(keytool, private, run):
    server = private / "server"
    server.mkdir()
    password_file = server / "store-password"
    password_file.write_text(secrets.token_hex(32) + "\n", encoding="ascii")
    password_file.chmod(0o600)

    def tool(*arguments):
        checked([keytool, *arguments], private / "keytool-private.log")

    def store(name):
        return private / (name + ".p12")

    password = ["-storepass:file", password_file]
    for name in ["root", "untrusted-root"]:
        tool("-genkeypair", "-alias", name, "-keystore", store(name), "-storetype", "PKCS12",
             *password, "-keyalg", "RSA", "-keysize", RSA_KEY_BITS, "-sigalg", "SHA256withRSA",
             "-dname", f"CN=zklink-fixture-{name}", "-validity", CERTIFICATE_VALID_DAYS,
             "-ext", "BC:critical=ca:true", "-ext", "KU:critical=keyCertSign,cRLSign")
        tool("-exportcert", "-alias", name, "-keystore", store(name), *password,
             "-rfc", "-file", private / (name + ".pem"))
    for name in ["server", "server-expired", "client", "other-client"]:
        tool("-genkeypair", "-alias", name, "-keystore", store(name), "-storetype", "PKCS12",
             *password, "-keyalg", "RSA", "-keysize", RSA_KEY_BITS, "-dname", f"CN={name}",
             "-validity", CERTIFICATE_VALID_DAYS)
        request = private / (name + ".csr")
        certificate = private / (name + ".pem")
        tool("-certreq", "-alias", name, "-keystore", store(name), *password, "-file", request)
        validity = ["-startdate", "2020/01/01 00:00:00", "-validity", "1"] if name == "server-expired" else ["-validity", CERTIFICATE_VALID_DAYS]
        tool("-gencert", "-alias", "root", "-keystore", store("root"), *password,
             "-infile", request, "-outfile", certificate, "-rfc", *validity,
             "-sigalg", "SHA256withRSA", "-ext", "BC:critical=ca:false",
             "-ext", "KU:critical=digitalSignature,keyEncipherment",
             "-ext", "EKU=" + ("serverAuth" if name.startswith("server") else "clientAuth"),
             "-ext", "SAN=dns:localhost,dns:zklink.test,ip:127.0.0.1")
        tool("-importcert", "-noprompt", "-alias", "fixture-root", "-keystore", store(name),
             *password, "-file", private / "root.pem")
        tool("-importcert", "-noprompt", "-alias", name, "-keystore", store(name),
             *password, "-file", certificate)
    tool("-importcert", "-noprompt", "-alias", "fixture-root", "-keystore", server / "trust.p12",
         "-storetype", "PKCS12", *password, "-file", private / "root.pem")
    for name in ["root", "untrusted-root", "server", "server-expired", "client", "other-client"]:
        checked([keytool, "-printcert", "-file", private / (name + ".pem")], run / (name + "-certificate.txt"))
    for path in private.rglob("*"):
        if path.is_file():
            path.chmod(0o600)


def property_value(value):
    text = str(value).replace("\\", "\\\\").replace("\n", "\\n").replace("\r", "\\r")
    # Java Properties.load uses ISO-8859-1; preserve Unicode Windows/macOS paths.
    escaped = []
    for char in text:
        if char.isascii():
            escaped.append(char)
        else:
            encoded = char.encode("utf-16-be")
            for offset in range(0, len(encoded), 2):
                escaped.append(f"\\u{int.from_bytes(encoded[offset:offset + 2], 'big'):04x}")
    return "".join(escaped)


def write_properties(path, properties):
    path.write_text("".join(f"{key}={property_value(value)}\n" for key, value in properties.items()), encoding="ascii")


class JavaEnsemble:
    def __init__(self, java, runtime, private, run, version, mode):
        if version not in SERVER_VERSIONS or mode not in SECURITY_MODES:
            raise FixtureError("Unsupported portable fixture selection.")
        self.java, self.private, self.run = java, private, run
        self.version, self.mode = version, mode
        self.key = version + "-" + mode
        self.folder = private / self.key
        self.folder.mkdir()
        self.classpath = os.pathsep.join(map(str, [runtime / version / "classes", *sorted((runtime / version / "lib").glob("*.jar"))]))
        self.processes = []
        self.leases = []
        self.nodes = []
        self.closed = False
        self.environment = os.environ.copy()
        self.environment.update(ZKLINK_AOT_CERTIFICATE_DIRECTORY=str(private),
                                ZKLINK_USER="zklink-portable-readiness", ZKLINK_PASSWORD=secrets.token_hex(24))
        self.server_logback = private / "server-logback.xml"
        self.server_logback.write_text('<configuration><appender name="console" class="ch.qos.logback.core.ConsoleAppender"><encoder><pattern>%level %logger - %msg%n%ex</pattern></encoder></appender><root level="WARN"><appender-ref ref="console"/></root></configuration>', encoding="utf-8")
        self.probe_logback = private / "probe-logback.xml"
        self.probe_logback.write_text('<configuration><root level="OFF"/></configuration>', encoding="utf-8")

    def base_command(self, logging):
        return [self.java, f"-Xmx{JVM_HEAP_MIB}m", f"-XX:ActiveProcessorCount={JVM_PROCESSORS}",
                "-Djava.net.preferIPv4Stack=true", "-Dio.netty.transport.noNative=true",
                f"-Dlogback.configurationFile={logging}", "-cp", self.classpath]

    def reserve(self):
        lease = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            if os.name == "nt":
                lease.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
            lease.bind(("127.0.0.1", 0))
        except BaseException:
            lease.close()
            raise
        self.leases.append(lease)
        return lease.getsockname()[1]

    def start(self):
        verify_runtime(self.run / "java", self.run)
        checked([*self.base_command(self.probe_logback), "FixtureTool", "version", self.version],
                self.run / (self.key + "-runtime-version.txt"))
        for index in range(VOTER_COUNT + 1):
            self.nodes.append({"name": "zoo" + str(index + 1) if index < VOTER_COUNT else "expired",
                               "plain": self.reserve(), "secure": self.reserve(),
                               "quorum": self.reserve() if index < VOTER_COUNT else None,
                               "election": self.reserve() if index < VOTER_COUNT else None})
        servers = {f"server.{index + 1}": f"127.0.0.1:{node['quorum']}:{node['election']}" for index, node in enumerate(self.nodes[:VOTER_COUNT])}
        for index, node in enumerate(self.nodes):
            folder = self.folder / node["name"]
            data = folder / "data"
            data.mkdir(parents=True)
            (data / "myid").write_text(str(index + 1 if index < VOTER_COUNT else 1), encoding="ascii")
            configuration = {"tickTime": TICK_MILLISECONDS, "initLimit": INIT_TICKS, "syncLimit": SYNC_TICKS,
                "dataDir": data.as_posix(), "clientPort": node["plain"], "clientPortAddress": "127.0.0.1",
                "secureClientPort": node["secure"], "secureClientPortAddress": "127.0.0.1",
                "standaloneEnabled": "false" if index < VOTER_COUNT else "true", "admin.enableServer": "false",
                "4lw.commands.whitelist": "srvr", "serverCnxnFactory": "org.apache.zookeeper.server.NettyServerCnxnFactory",
                "ssl.keyStore.location": (self.private / ("server.p12" if index < VOTER_COUNT else "server-expired.p12")).as_posix(),
                "ssl.keyStore.passwordPath": (self.private / "server/store-password").as_posix(), "ssl.keyStore.type": "PKCS12",
                "ssl.trustStore.location": (self.private / "server/trust.p12").as_posix(),
                "ssl.trustStore.passwordPath": (self.private / "server/store-password").as_posix(), "ssl.trustStore.type": "PKCS12",
                "ssl.clientAuth": "need" if self.mode == "mtls" else "none", "ssl.sslProvider": "JDK",
                "authProvider.x509": "org.apache.zookeeper.server.auth.X509AuthenticationProvider",
                "client.portUnification": "false"}
            if index < VOTER_COUNT:
                configuration.update(servers)
            config = folder / "zoo.cfg"
            write_properties(config, configuration)
            shutil.copy2(config, self.run / (self.key + "-" + node["name"] + "-config.properties"))
            # Release only this member's reserved ports immediately before its owned JVM starts.
            ports = {node["plain"], node["secure"], node["quorum"], node["election"]}
            for lease in self.leases:
                if lease.fileno() >= 0 and lease.getsockname()[1] in ports:
                    lease.close()
            child = OwnedProcess([*self.base_command(self.server_logback), "org.apache.zookeeper.server.quorum.QuorumPeerMain", config],
                                 self.run / (self.key + "-" + node["name"] + "-server.log"))
            self.processes.append(child)
            node["pid"] = child.process.pid
            self.save_processes()
        deadline = time.monotonic() + STARTUP_TIMEOUT_SECONDS
        while True:
            self.require_alive()
            try:
                states = self.status()
                if sorted(states[:VOTER_COUNT]) == ["follower", "follower", "leader"] and states[-1] == "standalone":
                    break
            except (OSError, FixtureError):
                pass
            if time.monotonic() >= deadline:
                raise FixtureError("Portable ensemble did not establish the required roles/version.")
            time.sleep(POLL_SECONDS)
        self.probe_all("initial")

    def save_processes(self):
        (self.run / (self.key + "-processes.json")).write_text(json.dumps(self.nodes, indent=2) + "\n", encoding="utf-8")

    def require_alive(self):
        if any(child.process.poll() is not None for child in self.processes):
            raise FixtureError("An owned ZooKeeper JVM exited unexpectedly.")

    def status(self):
        states = []
        for node in self.nodes:
            with socket.create_connection(("127.0.0.1", node["plain"]), timeout=SOCKET_TIMEOUT_SECONDS) as connection:
                connection.sendall(b"srvr")
                response = bytearray()
                while True:
                    chunk = connection.recv(STATUS_MAX_BYTES - len(response))
                    if not chunk:
                        break
                    response.extend(chunk)
                    if len(response) >= STATUS_MAX_BYTES:
                        raise FixtureError("Server status exceeded its bound.")
            text = response.decode("utf-8")
            if not re.search(r"Zookeeper version: " + re.escape(self.version) + r"(?:-|,|\s)", text):
                raise FixtureError("Unexpected server patch in native fixture.")
            role = re.search(r"^Mode: (leader|follower|standalone)$", text, re.MULTILINE)
            if not role:
                raise FixtureError("Missing native fixture server role.")
            states.append(role.group(1))
            (self.run / (self.key + "-" + node["name"] + "-status.txt")).write_text(text, encoding="utf-8")
        return states

    def probe_all(self, label):
        self.require_alive()
        roles = self.status()
        if sorted(roles[:VOTER_COUNT]) != ["follower", "follower", "leader"] or roles[-1] != "standalone":
            raise FixtureError("Portable fixture lost its required roles.")
        for node in self.nodes:
            secure = node["name"] != "expired"
            port = node["secure"] if secure else node["plain"]
            checked([*self.base_command(self.probe_logback), "FixtureTool", "probe", f"127.0.0.1:{port}", "secure" if secure else "plaintext"],
                    self.private / "readiness-private.log", self.environment, timeout=PROBE_TIMEOUT_SECONDS)
            (self.run / (self.key + "-" + node["name"] + "-" + label + "-readiness.txt")).write_text("Authenticated create/read/delete: PASS\n", encoding="utf-8")

    def close(self):
        if not self.closed:
            errors = []
            for child in reversed(self.processes):
                try:
                    child.stop()
                except (OSError, subprocess.SubprocessError, FixtureError) as error:
                    errors.append(type(error).__name__)
            for lease in self.leases:
                lease.close()
            self.closed = True
            processes = [{"pid": child.process.pid, "exitCode": child.process.poll()} for child in self.processes]
            (self.run / (self.key + "-cleanup.json")).write_text(json.dumps({"ownedProcessesExited": all(item["exitCode"] is not None for item in processes), "processes": processes, "errors": errors}, indent=2) + "\n", encoding="utf-8")
            if errors:
                raise FixtureError("Owned ZooKeeper process cleanup failed.")
