using System.Net;
using ZkLink;

namespace ZkLink.Samples.Basic;

internal static class SampleClientOptionsFactory
{
    internal const string PlaintextError =
        "The --plaintext sample flag requires only literal loopback endpoints. Use TLS for remote servers.";

    internal static bool IsLoopbackConnectionString(string connectionString)
    {
        var slash = connectionString.IndexOf('/', StringComparison.Ordinal);
        var endpoints = slash < 0 ? connectionString : connectionString[..slash];
        return endpoints.Split(',').All(IsLoopbackEndpoint);
    }

    private static bool IsLoopbackEndpoint(string endpoint)
    {
        if (
            !Uri.TryCreate("tcp://" + endpoint, UriKind.Absolute, out var uri)
            || uri.Host.Length == 0
            || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/"
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
        )
        {
            return false;
        }
        // Uri normalizes the DNS name "loopback" to "localhost". Validate the
        // original host so an arbitrary DNS name cannot enable plaintext digest.
        var end =
            endpoint[0] == '['
                ? endpoint.IndexOf(']', StringComparison.Ordinal) + 1
                : endpoint.IndexOf(':', StringComparison.Ordinal);
        var host = end < 0 ? endpoint.AsSpan() : endpoint.AsSpan(0, end);
        return IPAddress.TryParse(host, out var address)
            && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }

    internal static ZooKeeperClientOptions Create(
        SampleArguments arguments,
        ZooKeeperAuthentication? authentication
    )
    {
        var plaintext = arguments.Plaintext;
        if (plaintext && !IsLoopbackConnectionString(arguments.ConnectionString))
        {
            throw new ArgumentException(PlaintextError, nameof(arguments));
        }
        return new ZooKeeperClientOptions
        {
            ConnectionString = arguments.ConnectionString,
            Tls = plaintext ? ZooKeeperTlsOptions.Disabled() : ZooKeeperTlsOptions.Required(),
            Authentication = authentication is null ? [] : [authentication],
            // This switch is for the explicitly selected local plaintext fixture only.
            AllowUnsafePlaintextDigest = plaintext,
        };
    }
}
