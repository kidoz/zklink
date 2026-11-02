using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace ZkLink.FaultTests;

// Diagnostic only: preserve the existing namespace-wide rejection rule until
// retained tuples/ownership establish what kept it pending. No payload or IP text.
internal sealed record SoakSocketSnapshot(
    DateTimeOffset ObservedAtUtc,
    int[] ListenerPorts,
    int[] UpstreamPorts,
    int TargetPort,
    string OwnershipStatus,
    int TotalMatches,
    IReadOnlyList<SoakSocketEntry> Matches,
    int OpenOwnedHandles
)
{
    internal const int MaximumMatches = 16;
    private const int MaximumDescriptors = 4096;
    private const int MaximumKernelRows = 32768;
    private const int KernelLocalField = 1;
    private const int KernelRemoteField = 2;
    private const int KernelUidField = 7;
    private const int KernelInodeField = 9;
    private const int HexWordCharacters = sizeof(uint) * 2;
    private const string SocketLinkPrefix = "socket:[";

    internal static SoakSocketSnapshot Capture(SoakSocketInventory inventory)
    {
        int[] listeners = inventory.Listeners.Select(s => s.Endpoint.Port).ToArray();
        var upstream = inventory
            .Connections.Where(c =>
                !inventory.Listeners.Any(l => SoakSocketInventory.SameEndpoint(l.Endpoint, c.Local))
            )
            .ToArray();
        int[] upstreams = upstream.Select(c => c.Local.Port).ToArray();
        int target = upstream.FirstOrDefault()?.Remote.Port ?? 0;
        var network = IPGlobalProperties.GetIPGlobalProperties();
        var candidates = network
            .GetActiveTcpConnections()
            .Where(inventory.Matches)
            .Select(c => (c.LocalEndPoint, Remote: (IPEndPoint?)c.RemoteEndPoint, c.State))
            .Concat(
                network
                    .GetActiveTcpListeners()
                    .Where(e =>
                        inventory.Listeners.Any(l =>
                            !l.Handle.IsClosed && SoakSocketInventory.SameEndpoint(l.Endpoint, e)
                        )
                    )
                    .Select(e =>
                        (LocalEndPoint: e, Remote: (IPEndPoint?)null, State: TcpState.Listen)
                    )
            )
            .ToArray();
        string status = OperatingSystem.IsLinux() ? "Captured" : "Unsupported";
        var owned = new HashSet<ulong>();
        var rows = new List<KernelSocket>();
        if (OperatingSystem.IsLinux())
        {
            try
            {
                int descriptors = 0;
                foreach (string path in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
                {
                    if (++descriptors > MaximumDescriptors)
                    {
                        throw new InvalidDataException();
                    }
                    string? link = new FileInfo(path).LinkTarget;
                    if (
                        link is not null
                        && link.StartsWith(SocketLinkPrefix, StringComparison.Ordinal)
                        && link.EndsWith(']')
                        && ulong.TryParse(
                            link.AsSpan(
                                SocketLinkPrefix.Length,
                                link.Length - SocketLinkPrefix.Length - 1
                            ),
                            out ulong inode
                        )
                    )
                    {
                        owned.Add(inode);
                    }
                }
                // Kernel documentation: docs.kernel.org/networking/proc_net_tcp.html.
                foreach (string file in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
                {
                    int count = 0;
                    foreach (string line in File.ReadLines(file).Skip(1))
                    {
                        if (++count > MaximumKernelRows)
                        {
                            throw new InvalidDataException();
                        }
                        string[] fields = line.Split(
                            (char[]?)null,
                            StringSplitOptions.RemoveEmptyEntries
                        );
                        if (fields.Length <= KernelInodeField)
                        {
                            throw new InvalidDataException();
                        }
                        IPEndPoint local = ParseEndpoint(fields[KernelLocalField]);
                        IPEndPoint remote = ParseEndpoint(fields[KernelRemoteField]);
                        if (
                            candidates.Any(c =>
                                SoakSocketInventory.SameEndpoint(c.LocalEndPoint, local)
                                && (
                                    c.Remote is null
                                        ? remote.Port == 0
                                        : SoakSocketInventory.SameEndpoint(c.Remote, remote)
                                )
                            )
                        )
                        {
                            rows.Add(
                                new(
                                    local,
                                    remote,
                                    uint.Parse(
                                        fields[KernelUidField],
                                        CultureInfo.InvariantCulture
                                    ),
                                    ulong.Parse(
                                        fields[KernelInodeField],
                                        CultureInfo.InvariantCulture
                                    )
                                )
                            );
                        }
                    }
                }
            }
            catch (Exception error)
                when (error
                        is IOException
                            or UnauthorizedAccessException
                            or FormatException
                            or OverflowException
                            or InvalidDataException
                )
            {
                status = "Unavailable";
            }
        }
        var matches = candidates
            .Take(MaximumMatches)
            .Select(c =>
            {
                KernelSocket? row = rows.FirstOrDefault(r =>
                    SoakSocketInventory.SameEndpoint(r.Local, c.LocalEndPoint)
                    && (
                        c.Remote is null
                            ? r.Remote.Port == 0
                            : SoakSocketInventory.SameEndpoint(r.Remote, c.Remote)
                    )
                );
                return new SoakSocketEntry(
                    (int)c.LocalEndPoint.AddressFamily,
                    c.LocalEndPoint.Port,
                    c.Remote?.Port ?? 0,
                    c.State,
                    row?.Uid,
                    row?.Inode,
                    row is null || row.Inode == 0 || status != "Captured"
                        ? null
                        : owned.Contains(row.Inode)
                );
            })
            .ToArray();
        return new(
            DateTimeOffset.UtcNow,
            listeners,
            upstreams,
            target,
            status,
            candidates.Length,
            matches,
            inventory.OpenHandles
        );
    }

    internal static IPEndPoint ParseEndpoint(string value)
    {
        string[] parts = value.Split(':');
        byte[] address = new byte[parts[0].Length / 2];
        for (int offset = 0; offset < parts[0].Length; offset += HexWordCharacters)
        {
            uint word = uint.Parse(
                parts[0].AsSpan(offset, HexWordCharacters),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture
            );
            BitConverter.TryWriteBytes(address.AsSpan(offset / 2, sizeof(uint)), word);
        }
        return new(
            new IPAddress(address),
            int.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        );
    }

    internal string ToJson() =>
        JsonSerializer.Serialize(
            new
            {
                observed_at_utc = ObservedAtUtc,
                listener_ports = ListenerPorts,
                upstream_ports = UpstreamPorts,
                target_port = TargetPort,
                open_owned_handles = OpenOwnedHandles,
                ownership_status = OwnershipStatus,
                total_matches = TotalMatches,
                dropped_matches = TotalMatches - Matches.Count,
                matches = Matches.Select(m => new
                {
                    address_family = m.AddressFamily,
                    local_port = m.LocalPort,
                    remote_port = m.RemotePort,
                    state = m.State.ToString(),
                    uid = m.Uid,
                    inode = m.Inode,
                    owned_by_test_process = m.OwnedByTestProcess,
                }),
            }
        );

    private sealed record KernelSocket(IPEndPoint Local, IPEndPoint Remote, uint Uid, ulong Inode);
}

internal sealed record SoakSocketEntry(
    int AddressFamily,
    int LocalPort,
    int RemotePort,
    TcpState State,
    uint? Uid,
    ulong? Inode,
    bool? OwnedByTestProcess
);
