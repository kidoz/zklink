using System.Net;
using System.Net.Sockets;

namespace ZkLink;

/// <summary>A configured host and port, without credentials, chroot or URI syntax.</summary>
public sealed class ZooKeeperEndpoint
{
    /// <summary>Validates a DNS name or unbracketed IP literal and an explicit port.</summary>
    public ZooKeeperEndpoint(string host, int port = 2181)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        if (
            host.Any(char.IsWhiteSpace)
            || host.IndexOfAny(['/', '@', '?', '#', '[', ']']) >= 0
            || Uri.CheckHostName(host) == UriHostNameType.Unknown
        )
        {
            throw new ArgumentException(
                "Endpoint must be a host without credentials or URI components.",
                nameof(host)
            );
        }
        if (
            host.Contains(':', StringComparison.Ordinal)
            && (
                !IPAddress.TryParse(host, out var address)
                || address.AddressFamily != AddressFamily.InterNetworkV6
            )
        )
        {
            throw new ArgumentException("Invalid endpoint host.", nameof(host));
        }
        Host = host;
        Port = port;
    }

    /// <summary>Gets the configured host, also used as TLS identity when applicable.</summary>
    public string Host { get; }

    /// <summary>Gets the client port; secure ports must be explicitly configured.</summary>
    public int Port { get; }
}
