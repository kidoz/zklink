using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ZkLink.IntegrationTests;

internal sealed class SecurityFixture(Action<string>? progress = null) : IAsyncLifetime
{
    private static readonly TimeSpan ConnectGuard = TimeSpan.FromSeconds(4);

    // A cold server TLS pipeline can exceed one second on a shared runner.
    // Keep certificate failures observable within the enclosing 30-second test.
    private static readonly TimeSpan RejectionGuard = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ConnectAttemptGuard = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OperationGuard = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CloseGuard = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CleanupGuard = TimeSpan.FromSeconds(15);
    private readonly string _user = "zklink-secure-" + Guid.NewGuid().ToString("N");
    private readonly string _password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private X509Certificate2? _root;
    private X509Certificate2? _untrustedRoot;
    private X509Certificate2? _certificate;
    private X509Certificate2? _otherCertificate;
    private X509Certificate2? _expiredCertificate;
    private X509Certificate2? _untrustedCertificate;
    private ZooKeeperClient? _client;
    private bool _created;
    internal string Root { get; } = "/zklink-security-" + Guid.NewGuid().ToString("N");
    internal string ConnectionString { get; private set; } = "";
    internal string PlaintextEndpoint { get; private set; } = "";
    internal string ExpiredEndpoint { get; private set; } = "";
    internal bool Mutual { get; private set; }
    internal ZooKeeperCompatibilityProfile Profile { get; private set; }
    internal ZooKeeperClient Client => _client!;
    internal X509Certificate2 Certificate => _certificate!;
    internal X509Certificate2 OtherCertificate => _otherCertificate!;
    internal X509Certificate2 ExpiredCertificate => _expiredCertificate!;
    internal X509Certificate2 UntrustedCertificate => _untrustedCertificate!;
    internal X509Certificate2 UntrustedRoot => _untrustedRoot!;
    internal CreateOptions DigestCreate { get; private set; } = null!;

    private void Report(string step)
    {
        if (progress is not null)
            progress(step);
        else
            TestContext.Current.TestOutputHelper?.WriteLine(step);
    }

    private static string Required(string key) =>
        Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException(
            "Run just zk-security-matrix with its required isolated configuration."
        );

    public async ValueTask InitializeAsync()
    {
        ConnectionString = Required("ZKLINK_TEST_CONNECTION_STRING");
        PlaintextEndpoint = Required("ZKLINK_TEST_PLAINTEXT_ENDPOINT");
        ExpiredEndpoint = Required("ZKLINK_TEST_EXPIRED_ENDPOINT");
        string mode = Required("ZKLINK_TEST_SECURITY_MODE");
        if (mode is not ("tls" or "mtls"))
            throw new InvalidOperationException("A verified TLS or mTLS fixture is required.");
        Mutual = mode == "mtls";
        string version = Required("ZKLINK_TEST_SERVER_VERSION");
        if (version is not ("3.8.7" or "3.9.6"))
            throw new InvalidOperationException(
                "The secure runner must verify a pinned server version."
            );
        Profile =
            version == "3.9.6"
                ? ZooKeeperCompatibilityProfile.ZooKeeper39
                : ZooKeeperCompatibilityProfile.ZooKeeper38;
        string directory = Required("ZKLINK_TEST_CERTIFICATE_DIRECTORY");
        string storePassword = (
            await File.ReadAllTextAsync(
                    Path.Combine(directory, "server", "store-password"),
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(false)
        ).Trim();
        try
        {
            Report("certificates.begin");
            _root = X509CertificateLoader.LoadCertificateFromFile(
                Path.Combine(directory, "root.pem")
            );
            _untrustedRoot = X509CertificateLoader.LoadCertificateFromFile(
                Path.Combine(directory, "untrusted-root.pem")
            );
            _certificate = Load("client");
            _otherCertificate = Load("other-client");
            _expiredCertificate = Load("expired-client");
            _untrustedCertificate = Load("untrusted-client");
            Report("certificates.end");
            _client = await ConnectAsync().ConfigureAwait(false);
            await Client
                .CreateAsync(
                    Root,
                    null,
                    LiveZooKeeperFixture.OwnerCreate,
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(false);
            _created = true;
            var acl = await Client
                .GetAclAsync(Root, TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
            DigestCreate = new CreateOptions([
                acl.Acl.Single(static item => item.Scheme == "digest"),
            ]);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
        // Schannel needs a disk-backed key on Windows; macOS uses platform storage.
        // Without PersistKeySet, disposal releases the temporary imported key.
        X509Certificate2 Load(string name) =>
            X509CertificateLoader.LoadPkcs12FromFile(
                Path.Combine(directory, name + ".p12"),
                storePassword,
                OperatingSystem.IsLinux()
                    ? X509KeyStorageFlags.EphemeralKeySet
                    : X509KeyStorageFlags.DefaultKeySet
            );
    }

    internal ZooKeeperTlsOptions Tls(
        X509Certificate2? certificate = null,
        X509Certificate2? root = null,
        string serverName = "zklink.test"
    ) =>
        ZooKeeperTlsOptions.Required(
            X509RevocationMode.NoCheck,
            certificate,
            [root ?? _root!],
            serverName
        );

    internal Task<ZooKeeperClient> ConnectAsync(
        string? endpoint = null,
        bool digest = true,
        ZooKeeperTlsOptions? tls = null,
        bool rejectionProbe = false
    ) =>
        ConnectWithCancellationAsync(
            endpoint,
            digest,
            tls,
            rejectionProbe,
            TestContext.Current.CancellationToken
        );

    internal async Task<ZooKeeperClient> ConnectWithCancellationAsync(
        string? endpoint,
        bool digest,
        ZooKeeperTlsOptions? tls,
        bool rejectionProbe,
        CancellationToken cancellationToken
    )
    {
        using var authentication = ZooKeeperAuthentication.Digest(_user, _password);
        Report("capture.begin");
        var pending = ZooKeeperClient.ConnectAsync(
            new ZooKeeperClientOptions
            {
                ConnectionString = endpoint ?? ConnectionString,
                Tls = tls ?? Tls(Mutual ? Certificate : null),
                Authentication = digest ? [authentication] : [],
                CompatibilityProfile = Profile,
                InitialConnectTimeout = rejectionProbe ? RejectionGuard : ConnectGuard,
                ConnectAttemptTimeout = rejectionProbe ? RejectionGuard : ConnectAttemptGuard,
                OperationTimeout = OperationGuard,
                CloseTimeout = CloseGuard,
            },
            cancellationToken
        );
        Report("capture.end");
        try
        {
            return await pending.ConfigureAwait(false);
        }
        finally
        {
            Report("connect.end");
        }
    }

    internal void AssertSafe(Exception error) =>
        Assert.True(
            !error.ToString().Contains(_password, StringComparison.Ordinal),
            "Default failure output exposed a credential."
        );

    public async ValueTask DisposeAsync()
    {
        Report("fixture.dispose.begin");
        try
        {
            if (_client is not null)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(CleanupGuard);
                    if (_created)
                        await DeleteTreeAsync(Root, cleanup.Token).ConfigureAwait(false);
                }
                finally
                {
                    await _client.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _root?.Dispose();
            _untrustedRoot?.Dispose();
            _certificate?.Dispose();
            _otherCertificate?.Dispose();
            _expiredCertificate?.Dispose();
            _untrustedCertificate?.Dispose();
            Report("fixture.dispose.end");
        }
    }

    private async Task DeleteTreeAsync(string path, CancellationToken token)
    {
        try
        {
            var children = await Client.GetChildrenAsync(path, token).ConfigureAwait(false);
            foreach (string name in children.Children)
                await DeleteTreeAsync(path + "/" + name, token).ConfigureAwait(false);
            await Client.DeleteAsync(path, ZNodeVersion.Any, token).ConfigureAwait(false);
        }
        catch (ZooKeeperException error) when (error.KnownErrorCode == ZooKeeperErrorCode.NoNode)
        { }
    }
}
