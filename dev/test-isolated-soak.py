"""Server-free rejection checks for isolation, ownership and supervision bounds."""
import copy
import datetime
import importlib.util
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import isolated_soak as worker

spec = importlib.util.spec_from_file_location("soak_supervisor", Path(__file__).with_name("supervise-zookeeper-soak.py"))
supervisor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(supervisor)


class IsolationTests(unittest.TestCase):
    def setUp(self):
        self.manifest = {"serverVersion": "3.8.7", "runId": "a" * 32,
                         "project": "zklink-soak-fault-3-8-7-" + "a" * 32,
                         "containerName": "zklink-soak-fault-3-8-7-" + "a" * 32 + "-driver",
                         "containerId": "b" * 64, "namespaceContainerId": "c" * 64,
                         "imageId": "sha256:" + "d" * 64, "user": "1001:1001",
                         "binaryDirectory": "/checkout/tests/bin", "evidenceDirectory": "/owned/evidence",
                         "containerEvidenceDirectory": "/evidence/zklink-soak-faults.owned"}
        self.info = {"Id": self.manifest["containerId"], "Name": "/" + self.manifest["containerName"],
                     "Image": self.manifest["imageId"], "Config": {"User": "1001:1001", "Labels": {
                         worker.RUN_LABEL: self.manifest["runId"], worker.PROJECT_LABEL: self.manifest["project"],
                         worker.ROLE_LABEL: "driver"}},
                     "HostConfig": {"NetworkMode": "container:" + self.manifest["namespaceContainerId"],
                                    "PortBindings": {}, "PublishAllPorts": False, "Privileged": False},
                     "Mounts": [{"Type": "bind", "Source": "/checkout/tests/bin", "Destination": "/test", "RW": False},
                                {"Type": "bind", "Source": "/owned/evidence", "Destination": self.manifest["containerEvidenceDirectory"], "RW": True}]}

    def test_valid_owned_namespace(self):
        worker.validate_runtime(self.info, self.manifest)

    def test_rejects_host_network_ports_privileges_wrong_image_user_and_writable_binaries(self):
        changes = [("HostConfig", "NetworkMode", "host"), ("HostConfig", "PortBindings", {"2181/tcp": []}),
                   ("HostConfig", "PublishAllPorts", True), ("HostConfig", "Privileged", True),
                   ("Config", "User", "0:0")]
        for section, key, value in changes:
            with self.subTest(key=key):
                info = copy.deepcopy(self.info)
                info[section][key] = value
                with self.assertRaises(RuntimeError):
                    worker.validate_runtime(info, self.manifest)
        for key in ("Image", "Id", "Name"):
            with self.subTest(key=key):
                info = copy.deepcopy(self.info)
                info[key] = "foreign"
                with self.assertRaises(RuntimeError):
                    worker.validate_runtime(info, self.manifest)
        self.info["Mounts"][0]["RW"] = True
        with self.assertRaises(RuntimeError):
            worker.validate_runtime(self.info, self.manifest)

    def test_cleanup_refuses_foreign_or_replaced_container(self):
        for key in (worker.RUN_LABEL, worker.PROJECT_LABEL, worker.ROLE_LABEL):
            with self.subTest(label=key):
                info = copy.deepcopy(self.info)
                info["Config"]["Labels"][key] = "foreign"
                with patch.object(worker, "inspect_container", return_value=info), patch.object(worker, "command") as command:
                    with self.assertRaises(RuntimeError):
                        worker.remove_owned(self.manifest)
                    command.assert_not_called()

    def test_cleanup_is_idempotent_and_confirms_removal(self):
        with patch.object(worker, "inspect_container", side_effect=[self.info, None]), patch.object(worker, "command") as command:
            worker.remove_owned(self.manifest)
            command.assert_called_once_with("container", "rm", "--force", self.info["Id"])
        with patch.object(worker, "inspect_container", return_value=None), patch.object(worker, "command") as command:
            worker.remove_owned(self.manifest)
            command.assert_not_called()

    def test_cleanup_rejects_arbitrary_project(self):
        self.manifest["project"] = "shared"
        with patch.object(worker, "inspect_container") as inspect:
            with self.assertRaises(RuntimeError):
                worker.remove_owned(self.manifest)
            inspect.assert_not_called()

    def test_mounted_binary_mutation_or_addition_is_failure(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory / "consumer.dll").write_bytes(b"original")
            manifest = {"binaryDirectory": temporary, "binarySha256": worker.binary_hashes(directory)}
            worker.validate_binaries(manifest)
            (directory / "consumer.dll").write_bytes(b"changed")
            with self.assertRaises(RuntimeError):
                worker.validate_binaries(manifest)
            (directory / "consumer.dll").write_bytes(b"original")
            (directory / "new.dll").write_bytes(b"added")
            with self.assertRaises(RuntimeError):
                worker.validate_binaries(manifest)

    def test_member_liveness_is_required_except_for_owned_emergency_teardown(self):
        info = {"Config": {"Labels": {worker.RUN_LABEL: self.manifest["runId"],
                                      "com.docker.compose.project": self.manifest["project"],
                                      "com.docker.compose.service": "zoo1"}}, "State": {"Running": False}}
        with patch.object(worker, "inspect_container", return_value=info):
            with self.assertRaises(RuntimeError):
                worker.member("owned", self.manifest["project"], self.manifest["runId"])
            worker.member("owned", self.manifest["project"], self.manifest["runId"], require_running=False)
            info["Config"]["Labels"][worker.RUN_LABEL] = "foreign"
            with self.assertRaises(RuntimeError):
                worker.member("owned", self.manifest["project"], self.manifest["runId"], require_running=False)

    def test_missing_runtime_is_failure(self):
        self.manifest.update(state="running")
        with patch.object(supervisor, "inspect_container", side_effect=RuntimeError("missing")):
            with self.assertRaisesRegex(RuntimeError, "missing"):
                supervisor.observe_runtime(self.manifest)


class SupervisionTests(unittest.TestCase):
    def test_observation_rejects_clock_gaps_backwards_clock_and_low_disk(self):
        now = datetime.datetime.now(datetime.timezone.utc)
        supervisor.check_observation(now, 0, now + datetime.timedelta(seconds=10), 10, supervisor.MINIMUM_HOST_FREE)
        cases = [(-1, 10, supervisor.MINIMUM_HOST_FREE), (121, 10, supervisor.MINIMUM_HOST_FREE),
                 (10, 121, supervisor.MINIMUM_HOST_FREE), (10, -1, supervisor.MINIMUM_HOST_FREE),
                 (10, 10, supervisor.MINIMUM_HOST_FREE - 1)]
        for utc_gap, monotonic_gap, free in cases:
            with self.subTest(utc_gap=utc_gap, monotonic_gap=monotonic_gap, free=free):
                with self.assertRaises(RuntimeError):
                    supervisor.check_observation(now, 0, now + datetime.timedelta(seconds=utc_gap), monotonic_gap, free)

    def test_vm_low_disk_and_missing_member_abort(self):
        manifest = {"project": "owned", "runId": "nonce"}
        def docker(*args):
            if args[0] == "ps":
                return "one\ntwo\nthree"
            if args[0] == "stats":
                return "owned|0%|1MiB|0B|1"
            if args[2] == "cat":
                return "some avg10=0 total=0\nfull avg10=0 total=0\n8 0 sda 1 0 0 2 3 0 0 4 0 5 6\nMemAvailable: 100 kB"
            return "Filesystem 1024-blocks Used Available Capacity Mounted\n/dev/sda 100 1 1 1% /datalog"
        with patch.object(supervisor, "member"), patch.object(supervisor, "command", side_effect=docker):
            with self.assertRaisesRegex(RuntimeError, "VM disk"):
                supervisor.observe_storage(manifest)
        with patch.object(supervisor, "command", return_value="one\ntwo"):
            with self.assertRaisesRegex(RuntimeError, "member count"):
                supervisor.observe_storage(manifest)

    def test_deadline_expires(self):
        supervisor.check_deadline(10, 10)
        with self.assertRaises(RuntimeError):
            supervisor.check_deadline(11, 10)

    def test_source_additions_or_modifications_abort(self):
        with patch.object(supervisor, "sources", return_value={"input": "new"}):
            with self.assertRaises(RuntimeError):
                supervisor.source_unchanged({"input": "old"})
            with self.assertRaises(RuntimeError):
                supervisor.source_unchanged({})

    def test_stuck_idle_inhibitor_is_killed_and_cleanup_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            instance = supervisor.Supervisor(Path(temporary), None)
            instance.inhibitor = Mock(poll=Mock(return_value=None))
            instance.inhibitor.wait.side_effect = [subprocess.TimeoutExpired("owned", 10), 137]
            with self.assertRaisesRegex(RuntimeError, "inhibitor exceeded"):
                instance.stop_inhibitor()
            instance.inhibitor.kill.assert_called_once()
            self.assertEqual(instance.inhibitor.wait.call_args.kwargs["timeout"], supervisor.PROCESS_REAP_TIMEOUT)

    def test_stuck_runner_is_killed_and_boundedly_reaped(self):
        with tempfile.TemporaryDirectory() as temporary:
            instance = supervisor.Supervisor(Path(temporary), None)
            instance.child = Mock(pid=123, poll=Mock(return_value=None))
            instance.child.wait.side_effect = [subprocess.TimeoutExpired("owned", 180), 137]
            with patch.object(supervisor.os, "killpg") as kill:
                with self.assertRaisesRegex(RuntimeError, "cleanup grace"):
                    instance.stop()
                self.assertEqual(kill.call_args_list[0].args, (123, signal.SIGTERM))
                self.assertEqual(kill.call_args_list[1].args, (123, signal.SIGKILL))
                self.assertEqual(instance.child.wait.call_args.kwargs["timeout"], supervisor.PROCESS_REAP_TIMEOUT)

    def samples(self):
        baseline = {"kind": "baseline", "duration_seconds": 30, "seed": 1729, "elapsed_ms": 1,
                    "reported_at_utc": "2026-11-01T00:00:00Z", "rounds": 6, "crud_started": 10,
                    "crud_succeeded": 10, "canceled_not_sent": 6, "canceled_unknown": 6, "reconnects": 6,
                    "owned_counters": 0, "owned_live_tcp": 0, "owned_listeners": 0, "retained_heap_bytes": 100,
                    "heap_growth_limit_bytes": 100, "threads": 10, "thread_growth_limit": 2}
        summary = dict(baseline, kind="summary", elapsed_ms=30001, reported_at_utc="2026-11-01T00:00:30Z")
        return [baseline, summary]

    def test_completed_series(self):
        supervisor.validate_samples(self.samples(), 30, 1729)

    def test_missing_summary_resource_leaks_growth_incomplete_outcomes_and_sample_gaps_fail(self):
        changes = {"kind": "failure", "seed": 1730, "duration_seconds": 31, "elapsed_ms": 29999,
                   "rounds": 5, "crud_succeeded": 9, "canceled_unknown": 5, "canceled_not_sent": 5,
                   "reconnects": 5, "owned_counters": 1, "owned_live_tcp": 1, "owned_listeners": 1,
                   "retained_heap_bytes": 201, "threads": 13, "reported_at_utc": "2026-11-01T00:02:01Z"}
        for key, value in changes.items():
            with self.subTest(key=key):
                samples = self.samples()
                samples[-1][key] = value
                with self.assertRaises(RuntimeError):
                    supervisor.validate_samples(samples, 30, 1729)
        with self.assertRaises(RuntimeError):
            supervisor.validate_samples(self.samples()[:1], 30, 1729)


if __name__ == "__main__":
    unittest.main()
