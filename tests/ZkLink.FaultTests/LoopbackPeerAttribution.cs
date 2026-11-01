using System.Diagnostics;
using System.Globalization;

namespace ZkLink.FaultTests;

internal sealed record ProxyConnectionIdentity(
    int ListenerPort,
    int PeerPort,
    int? UpstreamLocalPort,
    int TargetPort
)
{
    internal string ToJson() =>
        FormattableString.Invariant(
            $"{{\"listener_port\":{ListenerPort},\"peer_port\":{PeerPort},\"upstream_local_port\":{UpstreamLocalPort?.ToString(CultureInfo.InvariantCulture) ?? "null"},\"target_port\":{TargetPort}}}"
        );
}

internal enum PeerAttributionStatus
{
    Found,
    NoMatch,
    Unsupported,
    ToolFailed,
    TimedOut,
    Cancelled,
    OutputLimit,
}

internal sealed record LoopbackPeerProcess(int Id, string Name)
{
    internal string ToJson() =>
        FormattableString.Invariant($"{{\"pid\":{Id},\"name\":\"{Name}\"}}");
}

internal sealed record LoopbackPeerAttribution(
    PeerAttributionStatus Status,
    IReadOnlyList<LoopbackPeerProcess> Processes
)
{
    private const int MaximumOutputCharacters = 16_384;
    private const int DrainBufferBytes = 4096;
    private const int MaximumProcesses = 8;
    private const int MaximumProcessNameCharacters = 64;
    private static readonly TimeSpan CaptureGuard = TimeSpan.FromSeconds(2);
    private static string? ToolPath =>
        OperatingSystem.IsMacOS() ? "/usr/sbin/lsof"
        : OperatingSystem.IsLinux() ? "/usr/bin/lsof"
        : null;
    internal static bool Supported => ToolPath is { } path && File.Exists(path);

    internal string ToJson() =>
        $"{{\"status\":\"{Status}\",\"processes\":[{string.Join(',', Processes.Select(item => item.ToJson()))}]}}";

    // This is test-fixture diagnostics, never a driver or platform dependency.
    // Query only the accepted loopback tuple; no arguments, environments or
    // network payloads are requested from the OS. Failure never changes the
    // malformed-frame outcome, and unsupported platforms report that explicitly.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Optional diagnostic failure returns a status and must not replace the original fatal framing exception."
    )]
    internal static async Task<LoopbackPeerAttribution> CaptureAsync(
        ProxyConnectionIdentity connection,
        CancellationToken token
    )
    {
        try
        {
            return await CaptureCoreAsync(connection, token).ConfigureAwait(false);
        }
        catch
        {
            // Includes an owned diagnostic process failing to terminate/reap;
            // preserve the original framing failure rather than replacing it.
            return new(PeerAttributionStatus.ToolFailed, []);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Optional diagnostic failure returns a status, and the owning wrapper also observes process cleanup failure."
    )]
    private static async Task<LoopbackPeerAttribution> CaptureCoreAsync(
        ProxyConnectionIdentity connection,
        CancellationToken token
    )
    {
        if (!Supported)
        {
            return new(PeerAttributionStatus.Unsupported, []);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(CaptureGuard);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(ToolPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        process.StartInfo.ArgumentList.Add("-nP");
        process.StartInfo.ArgumentList.Add(
            "-iTCP:" + connection.ListenerPort.ToString(CultureInfo.InvariantCulture)
        );
        process.StartInfo.ArgumentList.Add("-Fpcn");
        bool started = false;
        try
        {
            started = process.Start();
            if (!started)
            {
                return new(PeerAttributionStatus.ToolFailed, []);
            }
            var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var errors = process.StandardError.BaseStream.CopyToAsync(
                Stream.Null,
                DrainBufferBytes,
                deadline.Token
            );
            await Task.WhenAll(output, errors, process.WaitForExitAsync(deadline.Token))
                .ConfigureAwait(false);
            var captured = await output.ConfigureAwait(false);
            if (captured.Length == MaximumOutputCharacters)
            {
                return new(PeerAttributionStatus.OutputLimit, []);
            }
            return process.ExitCode == 0
                ? Parse(captured, connection)
                : new(PeerAttributionStatus.ToolFailed, []);
        }
        catch (OperationCanceledException)
        {
            return new(
                token.IsCancellationRequested
                    ? PeerAttributionStatus.Cancelled
                    : PeerAttributionStatus.TimedOut,
                []
            );
        }
        catch
        {
            return new(PeerAttributionStatus.ToolFailed, []);
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var reaping = new CancellationTokenSource(CaptureGuard);
                await process.WaitForExitAsync(reaping.Token).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[MaximumOutputCharacters];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            length += read;
        }
        return new string(buffer, 0, length);
    }

    internal static LoopbackPeerAttribution Parse(string output, ProxyConnectionIdentity connection)
    {
        int id = 0;
        string name = "";
        var processes = new List<LoopbackPeerProcess>();
        string expected = FormattableString.Invariant(
            $"127.0.0.1:{connection.PeerPort}->127.0.0.1:{connection.ListenerPort}"
        );
        foreach (string line in output.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }
            if (line[0] == 'p')
            {
                _ = int.TryParse(
                    line.AsSpan(1),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out id
                );
                name = "";
            }
            else if (line[0] == 'c')
            {
                name = new string(
                    line.Skip(1)
                        .Take(MaximumProcessNameCharacters)
                        .Select(value =>
                            char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or '-' or ' '
                                ? value
                                : '?'
                        )
                        .ToArray()
                );
            }
            else if (
                line[0] == 'n'
                && line.AsSpan(1).TrimEnd('\r').SequenceEqual(expected)
                && id > 0
                && processes.Count < MaximumProcesses
            )
            {
                processes.Add(new(id, name));
            }
        }
        return new(
            processes.Count > 0 ? PeerAttributionStatus.Found : PeerAttributionStatus.NoMatch,
            processes
        );
    }
}
