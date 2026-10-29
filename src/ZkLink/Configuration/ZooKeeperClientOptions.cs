namespace ZkLink;

/// <summary>Settings copied and validated synchronously when connecting a client.</summary>
public sealed class ZooKeeperClientOptions
{
    /// <summary>Gets the conventional host list and optional chroot.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Gets an optional explicit chroot, which must agree with an embedded chroot.</summary>
    public string? Chroot { get; init; }

    /// <summary>Gets transport security settings; TLS is required by default.</summary>
    public ZooKeeperTlsOptions Tls { get; init; } = ZooKeeperTlsOptions.Required();

    /// <summary>Gets configured authentication entries; they are replayed before readiness on reconnect.</summary>
    public IReadOnlyList<ZooKeeperAuthentication> Authentication { get; init; } = [];

    /// <summary>Gets an explicit unsafe development opt-in for digest over plaintext.</summary>
    public bool AllowUnsafePlaintextDigest { get; init; }

    /// <summary>Gets explicit permission to create TTL nodes; every endpoint must have extended node types enabled.</summary>
    /// <remarks>Disabled by default. This operator assertion does not configure or probe the server.</remarks>
    public bool EnableTtlNodes { get; init; }

    /// <summary>Gets the operator-asserted minimum endpoint compatibility profile.</summary>
    public ZooKeeperCompatibilityProfile CompatibilityProfile { get; init; } =
        ZooKeeperCompatibilityProfile.ZooKeeper38;

    /// <summary>Gets the requested session timeout; the server's negotiated value governs liveness.</summary>
    public TimeSpan SessionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the total deadline for initial connection across all endpoints.</summary>
    /// <remarks>Completion also waits for owned TLS cleanup; native certificate validation can delay cancellation.</remarks>
    public TimeSpan InitialConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the maximum DNS/TCP/TLS/handshake time for one endpoint attempt.</summary>
    /// <remarks>
    /// Also bounds each certificate download where supported by the platform. Native chain evaluation
    /// can perform multiple downloads or delay cancellation; macOS ignores the download timeout.
    /// After an accepted handshake, authentication and watch restoration use negotiated inbound liveness.
    /// </remarks>
    public TimeSpan ConnectAttemptTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the application request deadline, including admission delay.</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the deadline for acknowledged close and local cleanup initiation.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the application in-flight limit; control traffic has separate reserved capacity.</summary>
    public int MaxInFlightRequests { get; init; } = 256;

    /// <summary>Gets the maximum number of queued application requests.</summary>
    public int MaxQueuedRequests { get; init; } = 1024;

    /// <summary>Gets the byte budget for owned queued/in-flight encoded application requests.</summary>
    public int MaxPendingRequestBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>Gets the maximum payload length excluding the four-byte frame prefix.</summary>
    public int MaxFrameBytes { get; init; } = 1_048_575;

    /// <summary>Gets the independent znode data limit, additionally constrained by frame size.</summary>
    public int MaxDataBytes { get; init; } = 1_000_000;

    /// <summary>Gets the maximum collection element count, additionally constrained by frame size.</summary>
    public int MaxCollectionElements { get; init; } = 100_000;

    /// <summary>Gets the bound for owned watch handles and cleanup metadata, including pending reads and unread completed streams.</summary>
    public int MaxWatches { get; init; } = 10_000;

    /// <summary>Gets the global buffered state/watch-event budget and maximum state-subscription count.</summary>
    public int MaxBufferedEvents { get; init; } = 4096;
}
