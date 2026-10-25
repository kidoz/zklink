using System.Globalization;

namespace ZkLink.Configuration;

internal sealed class ParsedConnectionString(
    IReadOnlyList<ZooKeeperEndpoint> endpoints,
    string? chroot
)
{
    internal IReadOnlyList<ZooKeeperEndpoint> Endpoints { get; } = endpoints;
    internal string? Chroot { get; } = chroot;
}

internal static class ConnectionStringParser
{
    internal const int MaxConnectionStringCharacters = 1_048_575;
    internal const int MaxEndpoints = 1024;

    internal static ParsedConnectionString Parse(string connectionString, string? chroot = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            connectionString.Length,
            MaxConnectionStringCharacters
        );
        int slash = connectionString.IndexOf('/', StringComparison.Ordinal);
        string hosts = slash < 0 ? connectionString : connectionString[..slash];
        string? embeddedChroot = slash < 0 ? null : NormalizeChroot(connectionString[slash..]);
        string? explicitChroot = chroot is null ? null : NormalizeChroot(chroot);
        if (
            slash >= 0
            && chroot is not null
            && !string.Equals(embeddedChroot, explicitChroot, StringComparison.Ordinal)
        )
            throw new ArgumentException(
                "Connection string and explicit chroot conflict.",
                nameof(chroot)
            );
        // Count before splitting or allocating endpoint objects.
        int count = 1;
        foreach (char c in hosts)
            if (c == ',' && ++count > MaxEndpoints)
                throw new ArgumentException(
                    "Too many configured endpoints.",
                    nameof(connectionString)
                );
        string[] entries = hosts.Split(',');
        var endpoints = new ZooKeeperEndpoint[entries.Length];
        for (int i = 0; i < entries.Length; i++)
            endpoints[i] = ParseEndpoint(entries[i]);
        return new ParsedConnectionString(
            Array.AsReadOnly(endpoints),
            slash < 0 ? explicitChroot : embeddedChroot
        );
    }

    private static ZooKeeperEndpoint ParseEndpoint(string value)
    {
        string host;
        string? port = null;
        if (value.StartsWith('['))
        {
            int close = value.IndexOf(']', StringComparison.Ordinal);
            if (close <= 1 || !value.AsSpan(1, close - 1).Contains(':'))
                throw new ArgumentException("Malformed bracketed IPv6 endpoint.");
            host = value[1..close];
            if (close + 1 != value.Length)
            {
                if (value[close + 1] != ':')
                    throw new ArgumentException("Malformed IPv6 port separator.");
                port = value[(close + 2)..];
            }
        }
        else
        {
            int colon = value.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                if (value.AsSpan(colon + 1).Contains(':'))
                    throw new ArgumentException("IPv6 endpoints must be bracketed.");
                host = value[..colon];
                port = value[(colon + 1)..];
            }
            else
                host = value;
        }
        int number = 2181;
        if (
            port is not null
            && !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out number)
        )
            throw new ArgumentException("Invalid endpoint port.");
        return new ZooKeeperEndpoint(host, number);
    }

    private static string? NormalizeChroot(string path)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(path.Length, MaxConnectionStringCharacters);
        ZNodePath.Validate(path);
        return path == "/" ? null : path;
    }
}
