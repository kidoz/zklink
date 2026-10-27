#!/usr/bin/env python3
"""Guarded network mutation for the runner's disposable selected peer only."""

import ipaddress
import json
import os
from pathlib import Path
import re
import selectors
import socket
import subprocess
import sys
import tempfile
import time

ACTION_SECONDS = 10
MIN_WAIT_SECONDS = 0.001
CLIENT_PROBE_SECONDS = 1
OUTPUT_BYTES = 65_536
READ_BYTES = 4096
PROBE_SECONDS = "1s"
QUORUM_PORT = 2888
ELECTION_PORT = 3888
ELECTION_SECONDS = 15
ROLE_POLL_SECONDS = 0.25
LEADER = "leader"
FOLLOWER = "follower"
SERVICES = ("zoo1", "zoo2", "zoo3")


def docker(*args, check=True):
    with subprocess.Popen(["docker", *args], stdout=subprocess.PIPE, stderr=subprocess.PIPE) as process:
        streams = selectors.DefaultSelector()
        output = {process.stdout: bytearray(), process.stderr: bytearray()}
        deadline = time.monotonic() + ACTION_SECONDS
        try:
            for stream in output:
                streams.register(stream, selectors.EVENT_READ)
            while streams.get_map():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("Owned Docker action exceeded its guard.")
                for key, _ in streams.select(remaining):
                    data = os.read(key.fileobj.fileno(), READ_BYTES)
                    if not data:
                        streams.unregister(key.fileobj)
                    else:
                        output[key.fileobj].extend(data)
                        if sum(map(len, output.values())) > OUTPUT_BYTES:
                            raise RuntimeError("Owned Docker output exceeded its bound.")
            code = process.wait(timeout=max(MIN_WAIT_SECONDS, deadline - time.monotonic()))
            text = output[process.stdout].decode() + output[process.stderr].decode()
            if check and code != 0:
                raise RuntimeError("Owned Docker action failed with exit " + str(code))
            return code, text.strip()
        finally:
            streams.close()
            if process.poll() is None:
                process.kill()
            process.wait()


def required(name):
    return os.environ[name]


action = sys.argv[1] if len(sys.argv) == 2 else ""
project = required("ZKLINK_FAULT_PROJECT")
run_id = required("ZKLINK_FAULT_RUN_ID")
target = required("ZKLINK_FAULT_SERVICE")
target_role = required("ZKLINK_PEER_TARGET_ROLE")
state_file = Path(required("ZKLINK_PEER_STATE_FILE"))
if (action not in ("isolate", "election", "heal", "verify") or target not in SERVICES
        or target_role not in (LEADER, FOLLOWER)
        or not re.fullmatch(r"[a-f0-9]{32}", run_id)
        or not re.fullmatch(r"zklink-peer-fault-3-(8-7|9-6)-" + run_id, project)
        or not state_file.is_absolute() or not state_file.parent.name.startswith("zklink-peer-faults.")):
    raise RuntimeError("Owned peer fixture metadata is required.")
peer_network = project + "_peer"
client_network = project + "_default"


def network(name):
    template = '{{index .Labels "org.zklink.fault-run"}}|{{index .Labels "com.docker.compose.project"}}'
    _, labels = docker("network", "inspect", "--format", template, name)
    if labels != run_id + "|" + project:
        raise RuntimeError("Refusing a network outside this run.")


def member(service):
    _, cid = docker("ps", "--all", "--quiet", "--no-trunc", "--filter", "label=com.docker.compose.project=" + project,
                    "--filter", "label=com.docker.compose.service=" + service)
    if not re.fullmatch(r"[a-f0-9]{64}", cid):
        raise RuntimeError("The owned member is not uniquely identified.")
    template = ('{"run":{{json (index .Config.Labels "org.zklink.fault-run")}},'
                '"project":{{json (index .Config.Labels "com.docker.compose.project")}},'
                '"service":{{json (index .Config.Labels "com.docker.compose.service")}},'
                '"pid":{{.State.Pid}},"running":{{.State.Running}},'
                '"started":{{json .State.StartedAt}},'
                '"client":{{json (index .NetworkSettings.Networks "' + client_network + '")}},'
                '"peer":{{json (index .NetworkSettings.Networks "' + peer_network + '")}},'
                '"ports":{{json (index .NetworkSettings.Ports "2181/tcp")}}}')
    _, text = docker("inspect", "--format", template, cid)
    value = json.loads(text)
    if (value["run"] != run_id or value["project"] != project or value["service"] != service
            or not value["running"] or value["pid"] <= 0):
        raise RuntimeError("Owned running member labels do not match.")
    value["id"] = cid
    return value


def probe(cid, address, port, connected):
    ipaddress.IPv4Address(address)
    code, _ = docker("exec", cid, "timeout", PROBE_SECONDS, "bash", "-c",
                     'exec 3<>"/dev/tcp/$1/$2"', "_", address, str(port), check=False)
    if (connected and code != 0) or (not connected and code not in (1, 124)):
        raise RuntimeError("Peer TCP connectivity did not match the experiment.")


def identities(state, attached):
    for service in SERVICES:
        old = state["members"][service]
        now = member(service)
        if any(old[key] != now[key] for key in ("id", "pid", "started", "client", "ports")):
            raise RuntimeError("Container process/client identity changed during peer isolation.")
        if service == target and not attached:
            if now["peer"] is not None:
                raise RuntimeError("Isolated peer interface remains attached.")
        elif now["peer"] is None or now["peer"]["IPAddress"] != old["peer"]["IPAddress"]:
            raise RuntimeError("Expected peer address was not restored.")


def read_role(cid):
    _, status = docker("exec", cid, "zkServer.sh", "status", check=False)
    return next((role for role in (LEADER, FOLLOWER) if "Mode: " + role in status.splitlines()), None)


def save_election(state):
    # Keep the original rollback metadata intact if this process exits mid-write.
    with tempfile.NamedTemporaryFile("w", dir=state_file.parent, prefix=state_file.name + ".", delete=False) as output:
        temporary = Path(output.name)
        json.dump(state, output)
    try:
        os.replace(temporary, state_file)
    finally:
        temporary.unlink(missing_ok=True)


def majority_election(state):
    majority = [service for service in SERVICES if service != target]
    deadline = time.monotonic() + ELECTION_SECONDS
    while time.monotonic() < deadline:
        roles = {service: read_role(state["members"][service]["id"]) for service in majority}
        if list(roles.values()).count(LEADER) == 1 and list(roles.values()).count(FOLLOWER) == 1:
            leader = next(service for service in majority if roles[service] == LEADER)
            if state["serving_leader"] is not None and state["serving_leader"] != leader:
                raise RuntimeError("The serving majority leader changed during the experiment.")
            ready = True
            for service in majority:
                code, _ = docker("exec", state["members"][service]["id"], "bash",
                                 "/opt/zklink/zookeeper-healthcheck.sh", check=False)
                if code != 0:
                    ready = False
                    break
            if ready:
                if state["serving_leader"] is None:
                    state["serving_leader"] = leader
                    save_election(state)
                return leader
        time.sleep(ROLE_POLL_SECONDS)
    raise TimeoutError("The surviving majority did not elect within its guard.")


def prove_majority(state):
    identities(state, attached=False)
    leader = majority_election(state)
    follower = next(service for service in SERVICES if service not in (target, leader))
    probe(state["members"][follower]["id"], state["members"][leader]["peer"]["IPAddress"], QUORUM_PORT, True)
    probe(state["members"][target]["id"], state["members"][leader]["peer"]["IPAddress"], QUORUM_PORT, False)
    print("peer-majority-leader: " + leader + "; authenticated majority ready")


def healed_roles(state):
    expected = state["serving_leader"] or state["leader"]
    roles = {service: read_role(value["id"]) for service, value in state["members"].items()}
    matches = (roles[expected] == LEADER and list(roles.values()).count(LEADER) == 1
               and list(roles.values()).count(FOLLOWER) == 2
               and (target_role != LEADER or state["serving_leader"] is None or roles[target] == FOLLOWER))
    return roles, matches


def verify_membership(state):
    roles = {service: None for service in SERVICES}
    deadline = time.monotonic() + ELECTION_SECONDS
    while time.monotonic() < deadline:
        identities(state, attached=True)
        roles, matches = healed_roles(state)
        if matches:
            ready = True
            for service, value in state["members"].items():
                code, _ = docker("exec", value["id"], "bash",
                                 "/opt/zklink/zookeeper-healthcheck.sh", check=False)
                if code != 0:
                    ready = False
                    break
            if ready:
                identities(state, attached=True)
                roles, matches = healed_roles(state)
                if matches:
                    print("peer-verified-serving-leader: " + (state["serving_leader"] or state["leader"]))
                    return
        time.sleep(ROLE_POLL_SECONDS)
    summary = ",".join(service + "=" + (role or "unknown") for service, role in roles.items())
    raise TimeoutError("Healed membership/readiness did not converge within its guard: " + summary)


network(peer_network)
network(client_network)
if action == "isolate":
    members = {service: member(service) for service in SERVICES}
    roles = {}
    for service, value in members.items():
        roles[service] = read_role(value["id"])
        index = service[-1]
        if (value["peer"] is None or value["peer"]["IPAddress"] != required("ZKLINK_FAULT_IP_" + index)
                or value["client"]["IPAddress"] != required("ZKLINK_FAULT_CLIENT_IP_" + index)):
            raise RuntimeError("Fixture addresses do not match its selected topology.")
    if roles[target] != target_role or list(roles.values()).count(LEADER) != 1 or list(roles.values()).count(FOLLOWER) != 2:
        raise RuntimeError("Only the observed selected role may be isolated.")
    leader = next(service for service in SERVICES if roles[service] == LEADER)
    majority = [service for service in SERVICES if service != target]
    state = {"run": run_id, "project": project, "target": target, "target_role": target_role,
             "leader": leader, "serving_leader": leader if target_role == FOLLOWER else None, "members": members}
    quorum_paths = [(target, leader)] if target_role == FOLLOWER else [(service, target) for service in majority]
    for service in SERVICES:
        if service != target:
            probe(members[target]["id"], members[service]["peer"]["IPAddress"], ELECTION_PORT, True)
            probe(members[service]["id"], members[target]["peer"]["IPAddress"], ELECTION_PORT, True)
    for first, second in quorum_paths:
        probe(members[first]["id"], members[second]["peer"]["IPAddress"], QUORUM_PORT, True)
    with state_file.open("x") as output:
        json.dump(state, output)
    # Recheck labels immediately before the only disruptive action.
    network(peer_network)
    current = member(target)
    if any(current[key] != members[target][key] for key in ("id", "pid", "started", "client", "ports")):
        raise RuntimeError("The selected peer changed before isolation.")
    if read_role(current["id"]) != target_role:
        raise RuntimeError("The selected peer no longer has its expected role.")
    docker("network", "disconnect", peer_network, current["id"])
    identities(state, attached=False)
    binding = members[target]["ports"][0]
    with socket.create_connection((binding["HostIp"], int(binding["HostPort"])), timeout=CLIENT_PROBE_SECONDS):
        pass
    for service in SERVICES:
        if service != target:
            probe(members[target]["id"], members[service]["peer"]["IPAddress"], ELECTION_PORT, False)
            probe(members[service]["id"], members[target]["peer"]["IPAddress"], ELECTION_PORT, False)
    for first, second in quorum_paths:
        probe(members[first]["id"], members[second]["peer"]["IPAddress"], QUORUM_PORT, False)
    for first, second in (majority, majority[::-1]):
        probe(members[first]["id"], members[second]["peer"]["IPAddress"], ELECTION_PORT, True)
    if target_role == FOLLOWER and read_role(members[leader]["id"]) != LEADER:
        raise RuntimeError("The majority did not retain its original leader.")
    print("peer-isolated: bidirectional peer TCP blocked; majority TCP and client mapping preserved")
else:
    if not state_file.exists():
        if action == "heal" and member(target)["peer"] is not None:
            print("peer-heal: no fault state was created")
            sys.exit(0)
        raise RuntimeError("Owned peer state is required for recovery.")
    state = json.loads(state_file.read_text())
    if (state["run"], state["project"], state["target"], state["target_role"]) != (run_id, project, target, target_role):
        raise RuntimeError("Refusing foreign peer recovery state.")
    old = state["members"][target]
    current = member(target)
    if any(old[key] != current[key] for key in ("id", "pid", "started", "client", "ports")):
        raise RuntimeError("Refusing recovery of a changed container process.")
    if action == "election":
        prove_majority(state)
        sys.exit(0)
    if action == "heal" and current["peer"] is None:
        if target_role == LEADER:
            prove_majority(state)
        network(peer_network)
        docker("network", "connect", "--ip", old["peer"]["IPAddress"], "--gw-priority", "0", peer_network, current["id"])
    identities(state, attached=True)
    if action == "verify":
        verify_membership(state)
    print("peer-healed: original process/client/peer identities verified")
