using System.Diagnostics;

namespace ZkLink.FaultTests;

// Only the runner's labeled, nonce-named disposable target can be stopped/started.
internal sealed class MemberControl
{
    private static readonly TimeSpan ActionGuard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadyGuard = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ReadyPoll = TimeSpan.FromMilliseconds(250);
    internal const string Leader = "leader";
    internal const string Follower = "follower";
    private static readonly string[] Services = ["zoo1", "zoo2", "zoo3"];
    private static readonly TimeSpan ElectionGuard = TimeSpan.FromSeconds(15);
    private const string StopGraceSeconds = "1";
    private const int OutputLimit = 65_536;
    private readonly string _root = Required("ZKLINK_FAULT_REPO_ROOT");
    private readonly string _runId = Required("ZKLINK_FAULT_RUN_ID");
    private readonly string _version = Required("ZKLINK_TEST_SERVER_VERSION");
    private readonly string _project = Required("ZKLINK_FAULT_PROJECT");
    internal string Role { get; } = Required("ZKLINK_FAULT_ROLE");
    internal string Service { get; } = Required("ZKLINK_FAULT_SERVICE");
    internal string Endpoint { get; } = Required("ZKLINK_FAULT_ENDPOINT");
    internal bool RestoreRequired { get; private set; }

    internal MemberControl()
    {
        const int RunIdLength = 32;
        if (
            Role is not (Leader or Follower)
            || _version is not ("3.8.7" or "3.9.6")
            || _runId.Length != RunIdLength
            || !_runId.All(char.IsAsciiHexDigit)
            || _project != $"zklink-{Role}-fault-{_version.Replace('.', '-')}-{_runId}"
            || !Services.Contains(Service, StringComparer.Ordinal)
            || !Path.IsPathFullyQualified(_root)
            || !File.Exists(Path.Combine(_root, "compose.fault.yaml"))
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
            "Run just zk-follower-faults or just zk-leader-faults with its required disposable fixture."
        );

    private string[] Compose(params string[] arguments) =>
        [
            "compose",
            "-f",
            Path.Combine(_root, "compose.yaml"),
            "-f",
            Path.Combine(_root, "compose.fault.yaml"),
            "--env-file",
            Path.Combine(_root, $"dev/zookeeper-{_version}.env"),
            "--project-name",
            _project,
            .. arguments,
        ];

    private async Task VerifyOwnershipAsync(CancellationToken token)
    {
        var (_, id) = await RunAsync(Compose("ps", "--all", "--quiet", Service), token)
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
        if (code != 0 || labels.Trim() != $"{_runId}|{_project}|{Service}")
        {
            throw new InvalidOperationException(
                "Refusing control of a container outside this fault run."
            );
        }
    }

    internal async Task StopAsync(CancellationToken token)
    {
        await VerifyOwnershipAsync(token).ConfigureAwait(false);
        if (await ReadRoleAsync(Service, token).ConfigureAwait(false) != Role)
        {
            throw new InvalidOperationException(
                "Abort: the selected target no longer has the expected role."
            );
        }
        RestoreRequired = true;
        var stopped = await RunAsync(Compose("stop", "--timeout", StopGraceSeconds, Service), token)
            .ConfigureAwait(false);
        if (stopped.Code != 0)
        {
            throw new InvalidOperationException("The owned member did not stop cleanly.");
        }
        var (_, running) = await RunAsync(
                Compose("ps", "--status", "running", "--quiet", Service),
                token
            )
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(running))
        {
            throw new InvalidOperationException("The fault target is still running.");
        }
    }

    internal async Task RestoreAsync(CancellationToken token)
    {
        if (!RestoreRequired)
        {
            return;
        }
        await VerifyOwnershipAsync(token).ConfigureAwait(false);
        if (Role == Leader)
        {
            // Do not let an early-abort rollback race the replacement election.
            await WaitForReplacementLeaderAsync(token).ConfigureAwait(false);
        }
        var started = await RunAsync(Compose("start", Service), token).ConfigureAwait(false);
        if (started.Code != 0)
        {
            throw new InvalidOperationException("Could not restore the owned member.");
        }
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

    internal async Task<string> WaitForReplacementLeaderAsync(CancellationToken token)
    {
        if (Role != Leader)
        {
            throw new InvalidOperationException(
                "Replacement election applies only to a leader outage."
            );
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
        CancellationToken token
    )
    {
        var start = new ProcessStartInfo("docker")
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
        guard.CancelAfter(ActionGuard);
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
