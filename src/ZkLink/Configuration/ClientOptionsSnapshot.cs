using System.Net;
using System.Security.Cryptography;
using ZkLink.Protocol;

namespace ZkLink.Configuration;

internal sealed class ClientOptionsSnapshot : IDisposable
{
    private int _disposed;
    private readonly ZooKeeperTlsCredentials? _credentials;

    private ClientOptionsSnapshot(
        ZooKeeperClientOptions settings,
        ParsedConnectionString parsed,
        byte[][] authentication,
        ZooKeeperTlsCredentials? credentials
    )
    {
        _credentials = credentials;
        Settings = settings;
        Endpoints = parsed.Endpoints;
        Paths = new ChrootPath(parsed.Chroot);
        Authentication = authentication;
        Limits = new ProtocolLimits(
            settings.MaxFrameBytes,
            settings.MaxCollectionElements,
            settings.MaxDataBytes
        );
    }

    internal ZooKeeperClientOptions Settings { get; }
    internal IReadOnlyList<ZooKeeperEndpoint> Endpoints { get; }
    internal ChrootPath Paths { get; }
    internal byte[][] Authentication { get; }
    internal ProtocolLimits Limits { get; }

    internal static ClientOptionsSnapshot Capture(ZooKeeperClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Tls);
        ArgumentNullException.ThrowIfNull(options.Authentication);
        var parsed = ConnectionStringParser.Parse(options.ConnectionString, options.Chroot);
        ValidateTimeout(options.SessionTimeout);
        ValidateTimeout(options.InitialConnectTimeout);
        ValidateTimeout(options.ConnectAttemptTimeout);
        ValidateTimeout(options.OperationTimeout);
        ValidateTimeout(options.CloseTimeout);
        if (!Enum.IsDefined(options.CompatibilityProfile))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        ValidateCount(options.MaxInFlightRequests, 65536);
        ValidateCount(options.MaxQueuedRequests, 1_000_000);
        ValidateCount(options.MaxBufferedEvents, 1_000_000);
        ValidateCount(options.MaxWatches, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxPendingRequestBytes, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxPendingRequestBytes, 1 << 30);
        _ = new ProtocolLimits(
            options.MaxFrameBytes,
            options.MaxCollectionElements,
            options.MaxDataBytes
        );
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxFrameBytes, 45);
        var authentication = Snapshot.List(options.Authentication, maxCount: 32);
        if (!options.Tls.Enabled && authentication.Count != 0)
        {
            if (!options.AllowUnsafePlaintextDigest)
            {
                throw new ArgumentException(
                    "Plaintext digest requires the explicit unsafe development override.",
                    nameof(options)
                );
            }
            ValidatePlaintextDigestEndpoints(parsed.Endpoints);
        }
        var total = 0;
        foreach (var auth in authentication)
        {
            var length = auth.Length;
            if (length > options.MaxFrameBytes - 26 || length > 32 * 1024 * 1024 - total)
            {
                throw new ArgumentException(
                    "Authentication exceeds frame or credential bounds.",
                    nameof(options)
                );
            }
            total += length;
        }
        ZooKeeperTlsCredentials? credentials = null;
        var copies = new List<byte[]>();
        try
        {
            var tls = options.Tls;
            if (tls.Enabled && tls.CredentialsProvider is null)
            {
                credentials = new ZooKeeperTlsCredentials(tls.ClientCertificate, tls.TrustRoots);
                tls = ZooKeeperTlsOptions.Required(
                    tls.RevocationMode,
                    credentials.ClientCertificate,
                    credentials.TrustRoots,
                    tls.ServerName
                );
            }
            foreach (var auth in authentication)
            {
                copies.Add(auth.Copy());
            }
            var settings = new ZooKeeperClientOptions
            {
                ConnectionString = options.ConnectionString,
                Chroot = options.Chroot,
                Tls = tls,
                AllowUnsafePlaintextDigest = options.AllowUnsafePlaintextDigest,
                EnableTtlNodes = options.EnableTtlNodes,
                CompatibilityProfile = options.CompatibilityProfile,
                SessionTimeout = options.SessionTimeout,
                InitialConnectTimeout = options.InitialConnectTimeout,
                ConnectAttemptTimeout = options.ConnectAttemptTimeout,
                OperationTimeout = options.OperationTimeout,
                CloseTimeout = options.CloseTimeout,
                MaxInFlightRequests = options.MaxInFlightRequests,
                MaxQueuedRequests = options.MaxQueuedRequests,
                MaxPendingRequestBytes = options.MaxPendingRequestBytes,
                MaxFrameBytes = options.MaxFrameBytes,
                MaxDataBytes = options.MaxDataBytes,
                MaxCollectionElements = options.MaxCollectionElements,
                MaxBufferedEvents = options.MaxBufferedEvents,
                MaxWatches = options.MaxWatches,
            };
            var result = new ClientOptionsSnapshot(settings, parsed, copies.ToArray(), credentials);
            credentials = null;
            copies.Clear();
            return result;
        }
        finally
        {
            foreach (var copy in copies)
            {
                CryptographicOperations.ZeroMemory(copy);
            }
            credentials?.Dispose();
        }
    }

    internal void ValidateEndpoints(IReadOnlyList<ZooKeeperEndpoint> endpoints)
    {
        if (!Settings.Tls.Enabled && Authentication.Length != 0)
        {
            ValidatePlaintextDigestEndpoints(endpoints);
        }
    }

    private static void ValidatePlaintextDigestEndpoints(IReadOnlyList<ZooKeeperEndpoint> endpoints)
    {
        foreach (var endpoint in endpoints)
        {
            if (
                !IPAddress.TryParse(endpoint.Host, out var address)
                || !IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
            )
            {
                throw new ArgumentException(
                    "Plaintext digest requires literal loopback addresses for every endpoint.",
                    nameof(endpoints)
                );
            }
        }
    }

    internal static void ValidateTimeout(TimeSpan value)
    {
        if (
            value.Ticks <= 0
            || value.Ticks % TimeSpan.TicksPerMillisecond != 0
            || value.Ticks / TimeSpan.TicksPerMillisecond > int.MaxValue
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Timeouts require positive bounded whole milliseconds."
            );
        }
    }

    private static void ValidateCount(int value, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, maximum);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        foreach (var value in Authentication)
        {
            CryptographicOperations.ZeroMemory(value);
        }
        _credentials?.Dispose();
    }
}
