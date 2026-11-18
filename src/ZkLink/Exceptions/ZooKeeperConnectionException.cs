using System.Net.Sockets;

namespace ZkLink;

/// <summary>Initial connection failed and all partially owned resources were released.</summary>
public sealed class ZooKeeperConnectionException : IOException
{
    /// <summary>Creates safe inspectable connection metadata without retaining a platform inner exception.</summary>
    public ZooKeeperConnectionException(
        ZooKeeperConnectionFailure failure,
        ZooKeeperEndpoint? endpoint = null,
        int? errorCode = null
    )
        : this(failure, endpoint, errorCode, null, null) { }

    internal ZooKeeperConnectionException(
        ZooKeeperConnectionFailure failure,
        ZooKeeperEndpoint? endpoint,
        int? errorCode,
        ZooKeeperTlsFailureReason? tlsFailureReason,
        SocketError? socketError
    )
        : base(FormatMessage(failure, errorCode, tlsFailureReason, socketError))
    {
        if (!Enum.IsDefined(failure))
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }
        if (errorCode == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        }
        TlsFailureReason = tlsFailureReason;
        SocketError = socketError;
        Failure = failure;
        Endpoint = endpoint;
        ErrorCode = errorCode;
    }

    private static string FormatMessage(
        ZooKeeperConnectionFailure failure,
        int? errorCode,
        ZooKeeperTlsFailureReason? tlsReason,
        SocketError? socketError
    )
    {
        string details =
            tlsReason is { } tls ? $" ({tls})"
            : socketError is { } socket ? $" ({socket})"
            : string.Empty;
        if (errorCode is { } code)
        {
            details += FormattableString.Invariant($" (server code {code})");
        }
        return $"ZooKeeper connection failed with {failure}{details}.";
    }

    /// <summary>Gets the platform or provider TLS failure category when available.</summary>
    public ZooKeeperTlsFailureReason? TlsFailureReason { get; }

    /// <summary>Gets the socket failure code when available.</summary>
    public SocketError? SocketError { get; }

    /// <summary>Gets a safe failure classification.</summary>
    public ZooKeeperConnectionFailure Failure { get; }

    /// <summary>Gets optional endpoint metadata, excluded from default formatting.</summary>
    public ZooKeeperEndpoint? Endpoint { get; }

    /// <summary>Gets an optional original server error.</summary>
    public int? ErrorCode { get; }
}
