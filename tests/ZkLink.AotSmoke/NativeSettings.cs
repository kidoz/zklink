using System.Security.Cryptography.X509Certificates;

namespace ZkLink.AotSmoke;

internal sealed class NativeSettings : IDisposable
{
    private static readonly TimeSpan ConnectGuard = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan OperationGuard = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CloseGuard = TimeSpan.FromSeconds(3);
    private readonly List<X509Certificate2> _certificates = [];
    private readonly string _user = Required("ZKLINK_USER");
    private readonly string _password = Required("ZKLINK_PASSWORD");
    internal string Endpoint { get; } = Required("ZKLINK_AOT_ENDPOINT");
    internal bool Mutual { get; private init; }
    private ZooKeeperCompatibilityProfile Profile { get; init; }
    internal X509Certificate2 Root { get; private set; } = null!;
    internal X509Certificate2 UntrustedRoot { get; private set; } = null!;
    internal X509Certificate2 Certificate { get; private set; } = null!;
    internal X509Certificate2 OtherCertificate { get; private set; } = null!;

    internal static async Task<NativeSettings> ReadAsync(CancellationToken token)
    {
        var mode = Required("ZKLINK_AOT_SECURITY_MODE");
        Require(mode is "tls" or "mtls");
        var version = Required("ZKLINK_AOT_SERVER_VERSION");
        Require(version is "3.8.7" or "3.9.6");
        var settings = new NativeSettings
        {
            Mutual = mode == "mtls",
            Profile =
                version == "3.8.7"
                    ? ZooKeeperCompatibilityProfile.ZooKeeper38
                    : ZooKeeperCompatibilityProfile.ZooKeeper39,
        };
        try
        {
            var directory = Required("ZKLINK_AOT_CERTIFICATE_DIRECTORY");
            var password = (
                await File.ReadAllTextAsync(
                        Path.Combine(directory, "server", "store-password"),
                        token
                    )
                    .ConfigureAwait(false)
            ).Trim();
            settings.Root = settings.Own(
                X509CertificateLoader.LoadCertificateFromFile(Path.Combine(directory, "root.pem"))
            );
            settings.UntrustedRoot = settings.Own(
                X509CertificateLoader.LoadCertificateFromFile(
                    Path.Combine(directory, "untrusted-root.pem")
                )
            );
            settings.Certificate = settings.Own(LoadCertificate("client"));
            settings.OtherCertificate = settings.Own(LoadCertificate("other-client"));
            return settings;

            X509Certificate2 LoadCertificate(string name) =>
                X509CertificateLoader.LoadPkcs12FromFile(
                    Path.Combine(directory, name + ".p12"),
                    password,
                    OperatingSystem.IsMacOS()
                        ? X509KeyStorageFlags.DefaultKeySet
                        : X509KeyStorageFlags.EphemeralKeySet
                );
        }
        catch
        {
            settings.Dispose();
            throw;
        }
    }

    internal ZooKeeperTlsOptions Tls(
        X509Certificate2? certificate,
        X509Certificate2? root = null,
        string name = "zklink.test"
    ) =>
        ZooKeeperTlsOptions.Required(X509RevocationMode.NoCheck, certificate, [root ?? Root], name);

    internal async Task<ZooKeeperClient> ConnectAsync(
        string endpoint,
        CancellationToken token,
        bool digest = true,
        ZooKeeperTlsOptions? tls = null
    )
    {
        using var auth = ZooKeeperAuthentication.Digest(_user, _password);
        return await ZooKeeperClient
            .ConnectAsync(
                new ZooKeeperClientOptions
                {
                    ConnectionString = endpoint,
                    Tls = tls ?? Tls(Mutual ? Certificate : null),
                    Authentication = digest ? [auth] : [],
                    CompatibilityProfile = Profile,
                    InitialConnectTimeout = ConnectGuard,
                    ConnectAttemptTimeout = ConnectGuard,
                    OperationTimeout = OperationGuard,
                    CloseTimeout = CloseGuard,
                },
                token
            )
            .ConfigureAwait(false);
    }

    internal static async Task CloseAsync(ZooKeeperClient client, CancellationToken token)
    {
        await client.CloseAsync(token).ConfigureAwait(false);
        Require(client.Session.CloseStatus == ZooKeeperCloseStatus.Acknowledged);
    }

    internal static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException("Required native fixture configuration is missing.");

    internal static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Native smoke invariant failed.");
        }
    }

    private X509Certificate2 Own(X509Certificate2 certificate)
    {
        _certificates.Add(certificate);
        return certificate;
    }

    public void Dispose()
    {
        foreach (var certificate in _certificates)
        {
            certificate.Dispose();
        }
    }
}
