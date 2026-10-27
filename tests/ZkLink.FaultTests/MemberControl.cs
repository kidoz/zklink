using System.Diagnostics;

namespace ZkLink.FaultTests;

// Only the runner's labeled, nonce-named disposable fixture can be controlled.
internal sealed class MemberControl
{
    private static readonly TimeSpan ActionGuard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadyGuard = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ReadyPoll = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan EnsembleGuard = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan PeerActionGuard = TimeSpan.FromSeconds(20);
    internal const string Leader = "leader";
    internal const string Follower = "follower";
    internal const string Quorum = "quorum";
    internal const string Partition = "partition";
    internal const string Peer = "peer";
    internal const string Brief = "brief";
    internal const string UntilLost = "lost";
    internal const string Incoming = "incoming";
    internal const string Outgoing = "outgoing";
    private static readonly string[] Services = ["zoo1", "zoo2", "zoo3"];
    private static readonly TimeSpan ElectionGuard = TimeSpan.FromSeconds(15);
    private const string StopGraceSeconds = "1";
    private const string ClientPort = "2181";
    private const int OutputLimit = 65_536;
    private readonly string _root = Required("ZKLINK_FAULT_REPO_ROOT");
    private readonly string _runId = Required("ZKLINK_FAULT_RUN_ID");
    private readonly string _version = Required("ZKLINK_TEST_SERVER_VERSION");
    private readonly string _project = Required("ZKLINK_FAULT_PROJECT");
    internal string Role { get; } = Required("ZKLINK_FAULT_ROLE");
    internal string Service { get; } = Required("ZKLINK_FAULT_SERVICE");
    internal string Endpoint { get; } = Required("ZKLINK_FAULT_ENDPOINT");
    internal string? Outage { get; } = Environment.GetEnvironmentVariable("ZKLINK_FAULT_OUTAGE");
    internal string? Direction { get; } =
        Environment.GetEnvironmentVariable("ZKLINK_PARTITION_DIRECTION");
    internal string? PeerTargetRole { get; } =
        Environment.GetEnvironmentVariable("ZKLINK_PEER_TARGET_ROLE");
    private readonly string[] _targets;
    internal bool RestoreRequired { get; private set; }

    internal MemberControl()
    {
        _targets = Role == Quorum ? [Service, Required("ZKLINK_FAULT_SECOND_SERVICE")] : [Service];
        string? peerState = Environment.GetEnvironmentVariable("ZKLINK_PEER_STATE_FILE");
        const int RunIdLength = 32;
        if (
            Role is not (Leader or Follower or Quorum or Partition or Peer)
            || _version is not ("3.8.7" or "3.9.6")
            || _runId.Length != RunIdLength
            || !_runId.All(char.IsAsciiHexDigit)
            || _project != $"zklink-{Role}-fault-{_version.Replace('.', '-')}-{_runId}"
            || !Services.Contains(Service, StringComparer.Ordinal)
            || _targets.Any(target => !Services.Contains(target, StringComparer.Ordinal))
            || _targets.Distinct(StringComparer.Ordinal).Count() != _targets.Length
            || (Role is Quorum or Partition && Outage is not (Brief or UntilLost))
            || (Role == Partition && Direction is not (Incoming or Outgoing))
            || !Path.IsPathFullyQualified(_root)
            || !File.Exists(Path.Combine(_root, "compose.fault.yaml"))
            || (
                Role == Peer
                && (
                    PeerTargetRole is not (Leader or Follower)
                    || peerState is null
                    || !Path.IsPathFullyQualified(peerState)
                    || Path.GetFileName(Path.GetDirectoryName(peerState))
                        ?.StartsWith("zklink-peer-faults.", StringComparison.Ordinal) != true
                    || !File.Exists(Path.Combine(_root, "compose.peer-fault.yaml"))
                    || !File.Exists(Path.Combine(_root, "dev", "zookeeper-peer-network.py"))
                )
            )
        )
        {
            throw new InvalidOperationException(
                "The owned member fault runner configuration is required."
            );
        }
    }

    internal static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(
            "Run the matching just zk-follower-faults, zk-leader-faults, zk-quorum-faults, zk-partition-faults or zk-peer-partition-faults command with its required disposable fixture."
        );

    private string[] Compose(params string[] arguments) =>
        [
            "compose",
            "-f",
            Path.Combine(_root, "compose.yaml"),
            "-f",
            Path.Combine(_root, "compose.fault.yaml"),
            .. (
                Role == Quorum
                    ? new[] { "-f", Path.Combine(_root, "compose.quorum-fault.yaml") }
                    : []
            ),
            .. (Role == Peer ? new[] { "-f", Path.Combine(_root, "compose.peer-fault.yaml") } : []),
            "--env-file",
            Path.Combine(_root, $"dev/zookeeper-{_version}.env"),
            "--project-name",
            _project,
            .. arguments,
        ];

    private async Task VerifyOwnershipAsync(string service, CancellationToken token)
    {
        var (_, id) = await RunAsync(Compose("ps", "--all", "--quiet", service), token)
            .ConfigureAwait(false);
        id = id.Trim();
        const int ContainerIdLength = 64;
        if (id.Length != ContainerIdLength || !id.All(char.IsAsciiHexDigit))
        {
            throw new InvalidOperationException("The owned fault target could not be identified.");
        }
        var (code, labels) = await RunAsync(
                [
                    "inspect",
                    "--format",
                    "{{index .Config.Labels \"org.zklink.fault-run\"}}|{{index .Config.Labels \"com.docker.compose.project\"}}|{{index .Config.Labels \"com.docker.compose.service\"}}",
                    id,
                ],
                token
            )
            .ConfigureAwait(false);
        if (code != 0 || labels.Trim() != $"{_runId}|{_project}|{service}")
        {
            throw new InvalidOperationException(
                "Refusing control of a container outside this fault run."
            );
        }
    }

    internal async Task StopAsync(CancellationToken token)
    {
        if (Role is Partition or Peer)
        {
            throw new InvalidOperationException("Partition experiments must not stop any server.");
        }
        foreach (string target in _targets)
        {
            await VerifyOwnershipAsync(target, token).ConfigureAwait(false);
            string expected = Role == Quorum ? (target == Service ? Leader : Follower) : Role;
            if (await ReadRoleAsync(target, token).ConfigureAwait(false) != expected)
            {
                throw new InvalidOperationException(
                    "Abort: the selected target no longer has the expected role."
                );
            }
        }
        RestoreRequired = true;
        var stopped = await RunAsync(
                Compose(["stop", "--timeout", StopGraceSeconds, .. _targets]),
                token
            )
            .ConfigureAwait(false);
        if (stopped.Code != 0)
        {
            throw new InvalidOperationException("The owned member did not stop cleanly.");
        }
        var (_, running) = await RunAsync(
                Compose(["ps", "--status", "running", "--quiet", .. _targets]),
                token
            )
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(running))
        {
            throw new InvalidOperationException("The fault target is still running.");
        }
        if (Role == Quorum)
        {
            var (code, remaining) = await RunAsync(
                    Compose("ps", "--status", "running", "--quiet"),
                    token
                )
                .ConfigureAwait(false);
            if (
                code != 0
                || remaining.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length != 1
            )
            {
                throw new InvalidOperationException(
                    "Quorum outage requires exactly one running voter."
                );
            }
        }
    }

    internal async Task RestoreAsync(CancellationToken token)
    {
        if (!RestoreRequired)
        {
            return;
        }
        if (Role == Peer)
        {
            await RunPeerAsync("heal", token).ConfigureAwait(false);
            await WaitForEnsembleAsync(requireAll: true, token).ConfigureAwait(false);
            await RunPeerAsync("verify", token).ConfigureAwait(false);
            RestoreRequired = false;
            return;
        }
        if (Role == Quorum)
        {
            await RecoverQuorumAsync(token).ConfigureAwait(false);
            await WaitForEnsembleAsync(requireAll: true, token).ConfigureAwait(false);
            RestoreRequired = false;
            return;
        }
        if (Role == Leader)
        {
            // Do not let an early-abort rollback race the replacement election.
            await WaitForReplacementLeaderAsync(token).ConfigureAwait(false);
        }
        await StartTargetsAsync(_targets, token).ConfigureAwait(false);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < ReadyGuard)
        {
            if (await ReadRoleAsync(Service, token).ConfigureAwait(false) == Follower)
            {
                var ready = await RunAsync(
                        Compose(
                            "exec",
                            "-T",
                            Service,
                            "bash",
                            "/opt/zklink/zookeeper-healthcheck.sh"
                        ),
                        token
                    )
                    .ConfigureAwait(false);
                if (ready.Code != 0)
                {
                    throw new InvalidOperationException(
                        "Restored follower failed authenticated readiness."
                    );
                }
                RestoreRequired = false;
                return;
            }
            await Task.Delay(ReadyPoll, token).ConfigureAwait(false);
        }
        throw new TimeoutException(
            "Follower role/readiness did not recover within the rollback guard."
        );
    }

    internal async Task PartitionPeerAsync(CancellationToken token)
    {
        if (Role != Peer)
        {
            throw new InvalidOperationException("The owned peer-partition runner is required.");
        }
        await VerifyOwnershipAsync(Service, token).ConfigureAwait(false);
        if (await ReadRoleAsync(Service, token).ConfigureAwait(false) != PeerTargetRole)
        {
            throw new InvalidOperationException(
                "The selected peer no longer has its expected role."
            );
        }
        RestoreRequired = true;
        await RunPeerAsync("isolate", token).ConfigureAwait(false);
    }

    private async Task RunPeerAsync(string action, CancellationToken token)
    {
        var (code, output) = await RunAsync(
                [Path.Combine(_root, "dev/zookeeper-peer-network.py"), action],
                token,
                executable: "python3",
                actionGuard: PeerActionGuard
            )
            .ConfigureAwait(false);
        TestContext.Current.TestOutputHelper?.WriteLine(output);
        if (code != 0)
        {
            throw new InvalidOperationException("Owned peer-network control failed.");
        }
    }

    internal async Task RecoverQuorumAsync(CancellationToken token)
    {
        if (Role != Quorum || !RestoreRequired)
        {
            throw new InvalidOperationException(
                "Only an active owned quorum outage can recover quorum."
            );
        }
        // Recover a serving majority before adding the old leader, avoiding a
        // competing three-member election while all peers are starting together.
        await StartTargetsAsync([_targets[1]], token).ConfigureAwait(false);
        await WaitForEnsembleAsync(requireAll: false, token).ConfigureAwait(false);
        await StartTargetsAsync([Service], token).ConfigureAwait(false);
        var ports = await Task.WhenAll(
                Services.Select(service => RunAsync(Compose("port", service, ClientPort), token))
            )
            .ConfigureAwait(false);
        string[] expected = Required("ZKLINK_TEST_CONNECTION_STRING").Split(',');
        if (
            ports.Any(static result => result.Code != 0)
            || !ports
                .Select(static result => result.Output.Trim())
                .SequenceEqual(expected, StringComparer.Ordinal)
        )
        {
            throw new InvalidOperationException(
                "Quorum fixture host endpoints changed across restart."
            );
        }
    }

    private async Task StartTargetsAsync(string[] targets, CancellationToken token)
    {
        foreach (string target in targets)
        {
            await VerifyOwnershipAsync(target, token).ConfigureAwait(false);
        }
        var started = await RunAsync(Compose(["start", .. targets]), token).ConfigureAwait(false);
        if (started.Code != 0)
        {
            throw new InvalidOperationException("Could not restore the owned members.");
        }
    }

    private async Task WaitForEnsembleAsync(bool requireAll, CancellationToken token)
    {
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
        guard.CancelAfter(requireAll ? EnsembleGuard : ElectionGuard);
        while (true)
        {
            var roles = await Task.WhenAll(
                    Services.Select(service => ReadRoleAsync(service, guard.Token))
                )
                .ConfigureAwait(false);
            if (
                roles.Count(static role => role == Leader) == 1
                && roles.Count(static role => role == Follower) >= (requireAll ? 2 : 1)
            )
            {
                foreach (string service in Services.Where((_, index) => roles[index] is not null))
                {
                    var ready = await RunAsync(
                            Compose(
                                "exec",
                                "-T",
                                service,
                                "bash",
                                "/opt/zklink/zookeeper-healthcheck.sh"
                            ),
                            guard.Token
                        )
                        .ConfigureAwait(false);
                    if (ready.Code != 0)
                    {
                        throw new InvalidOperationException(
                            "Restored ensemble failed authenticated readiness."
                        );
                    }
                }
                return;
            }
            await Task.Delay(ReadyPoll, guard.Token).ConfigureAwait(false);
        }
    }

    internal async Task<string> WaitForReplacementLeaderAsync(CancellationToken token)
    {
        if (Role != Leader && !(Role == Peer && PeerTargetRole == Leader))
        {
            throw new InvalidOperationException(
                "Replacement election applies only to a leader outage."
            );
        }
        if (Role == Peer && RestoreRequired)
        {
            await RunPeerAsync("election", token).ConfigureAwait(false);
        }
        string[] survivors = Services.Where(service => service != Service).ToArray();
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
        guard.CancelAfter(ElectionGuard);
        while (true)
        {
            var roles = await Task.WhenAll(
                    survivors.Select(service => ReadRoleAsync(service, guard.Token))
                )
                .ConfigureAwait(false);
            int leaders = roles.Count(static role => role == Leader);
            int followers = roles.Count(static role => role == Follower);
            if (leaders == 1 && followers == 1)
            {
                return survivors[Array.FindIndex(roles, static role => role == Leader)];
            }
            await Task.Delay(ReadyPoll, guard.Token).ConfigureAwait(false);
        }
    }

    private async Task<string?> ReadRoleAsync(string service, CancellationToken token)
    {
        var (code, output) = await RunAsync(
                Compose("exec", "-T", service, "zkServer.sh", "status"),
                token
            )
            .ConfigureAwait(false);
        if (code != 0)
        {
            return null;
        }
        foreach (string line in output.Split('\n'))
        {
            if (line.Trim() == "Mode: " + Leader)
            {
                return Leader;
            }
            if (line.Trim() == "Mode: " + Follower)
            {
                return Follower;
            }
        }
        return null;
    }

    private static async Task<(int Code, string Output)> RunAsync(
        string[] arguments,
        CancellationToken token,
        string executable = "docker",
        TimeSpan? actionGuard = null
    )
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process =
            Process.Start(start)
            ?? throw new InvalidOperationException("Docker control could not start.");
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
        guard.CancelAfter(actionGuard ?? ActionGuard);
        var output = ReadOutputAsync(process.StandardOutput, guard.Token);
        var error = ReadOutputAsync(process.StandardError, guard.Token);
        try
        {
            await process.WaitForExitAsync(guard.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            string text = await output.ConfigureAwait(false) + await error.ConfigureAwait(false);
            if (text.Length > OutputLimit)
            {
                throw new InvalidOperationException("Fault control output exceeded its bound.");
            }
            return (process.ExitCode, text);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            try
            {
                await Task.WhenAll(output, error).ConfigureAwait(false);
            }
            catch (Exception failure)
                when (failure
                        is OperationCanceledException
                            or IOException
                            or InvalidOperationException
                ) { }
        }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[OutputLimit];
        int used = 0;
        while (used < buffer.Length)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(used), token).ConfigureAwait(false);
            if (count == 0)
            {
                return new string(buffer, 0, used);
            }
            used += count;
        }
        var extra = new char[1];
        if (await reader.ReadAsync(extra, token).ConfigureAwait(false) != 0)
        {
            throw new InvalidOperationException("Fault control output exceeded its bound.");
        }
        return new string(buffer);
    }
}
