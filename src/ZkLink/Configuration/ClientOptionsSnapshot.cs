using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ZkLink.Protocol;

namespace ZkLink.Configuration;

internal sealed class ClientOptionsSnapshot : IDisposable
{
    private int _disposed;

    private ClientOptionsSnapshot(
        ZooKeeperClientOptions settings,
        ParsedConnectionString parsed,
        byte[][] authentication
    )
    {
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
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxPendingRequestBytes, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxPendingRequestBytes, 1 << 30);
        _ = new ProtocolLimits(
            options.MaxFrameBytes,
            options.MaxCollectionElements,
            options.MaxDataBytes
        );
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxFrameBytes, 45);
        if (options.Authentication.Count > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        if (
            !options.Tls.Enabled
            && options.Authentication.Count != 0
            && !options.AllowUnsafePlaintextDigest
        )
        {
            throw new ArgumentException(
                "Plaintext digest requires the explicit unsafe development override.",
                nameof(options)
            );
        }
        var total = 0;
        foreach (var auth in options.Authentication)
        {
            if (auth is null)
            {
                throw new ArgumentException(
                    "Authentication entries cannot be null.",
                    nameof(options)
                );
            }
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
        var roots = new List<X509Certificate2>();
        X509Certificate2? certificate = null;
        var copies = new List<byte[]>();
        try
        {
            foreach (var root in options.Tls.TrustRoots)
            {
                roots.Add(new X509Certificate2(root));
            }
            if (options.Tls.ClientCertificate is { } clientCertificate)
            {
                if (!clientCertificate.HasPrivateKey)
                {
                    throw new ArgumentException(
                        "The mTLS certificate requires a private key.",
                        nameof(options)
                    );
                }
                certificate = new X509Certificate2(clientCertificate);
            }
            foreach (var authentication in options.Authentication)
            {
                copies.Add(authentication.Copy());
            }
            var settings = new ZooKeeperClientOptions
            {
                ConnectionString = options.ConnectionString,
                Chroot = options.Chroot,
                Tls = options.Tls.Enabled
                    ? ZooKeeperTlsOptions.Required(
                        options.Tls.RevocationMode,
                        certificate,
                        roots,
                        options.Tls.ServerName
                    )
                    : ZooKeeperTlsOptions.Disabled(),
                AllowUnsafePlaintextDigest = options.AllowUnsafePlaintextDigest,
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
            };
            var result = new ClientOptionsSnapshot(settings, parsed, copies.ToArray());
            certificate = null;
            roots.Clear();
            copies.Clear();
            return result;
        }
        finally
        {
            foreach (var copy in copies)
            {
                CryptographicOperations.ZeroMemory(copy);
            }
            foreach (var root in roots)
            {
                root.Dispose();
            }
            certificate?.Dispose();
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
        Settings.Tls.ClientCertificate?.Dispose();
        foreach (var root in Settings.Tls.TrustRoots)
        {
            root.Dispose();
        }
    }
}
