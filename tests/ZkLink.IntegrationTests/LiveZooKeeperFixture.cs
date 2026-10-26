using System.Security.Cryptography;

namespace ZkLink.IntegrationTests;

// These are required live tests: the matrix runner supplies configuration, and missing
// configuration fails initialization. Ordinary server-free jobs run their own projects.
internal sealed class LiveZooKeeperFixture : IAsyncLifetime
{
    internal static readonly CreateOptions OwnerCreate = new([
        new(ZooKeeperPermissions.All, "auth", ""),
    ]);
    internal string Root { get; } = "/zklink-tests-" + Guid.NewGuid().ToString("N");
    private readonly string _user = "zklink-" + Guid.NewGuid().ToString("N");
    private readonly string _password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private string _connection = "";
    private ZooKeeperClient? _client;
    private bool _created;
    internal ZooKeeperClient Client => _client!;
    internal string ConnectionString => _connection;
    internal string User => _user;

    public async ValueTask InitializeAsync()
    {
        _connection =
            Environment.GetEnvironmentVariable("ZKLINK_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Run just zk-operations-matrix to supply the required isolated live ensemble."
            );
        string? version = Environment.GetEnvironmentVariable("ZKLINK_TEST_SERVER_VERSION");
        if (version is not ("3.8.7" or "3.9.6"))
            throw new InvalidOperationException(
                "The live runner must verify and supply a pinned server version."
            );
        _client = await ConnectAsync().ConfigureAwait(false);
        await Client
            .CreateAsync(Root, null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        _created = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
            return;
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (_created)
                await DeleteTreeAsync(Root, cleanup.Token).ConfigureAwait(false);
        }
        finally
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async Task<ZooKeeperClient> ConnectAsync(
        bool authenticated = true,
        string? chroot = null,
        string? connectionString = null
    )
    {
        using var credentials = ZooKeeperAuthentication.Digest(_user, _password);
        var options = new ZooKeeperClientOptions
        {
            ConnectionString = connectionString ?? _connection,
            Chroot = chroot,
            Tls = ZooKeeperTlsOptions.Disabled(),
            AllowUnsafePlaintextDigest = true,
            Authentication = authenticated ? [credentials] : [],
            InitialConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectAttemptTimeout = TimeSpan.FromSeconds(2),
            OperationTimeout = TimeSpan.FromSeconds(5),
            CloseTimeout = TimeSpan.FromSeconds(2),
        };
        return await ZooKeeperClient
            .ConnectAsync(options, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DeleteTreeAsync(string path, CancellationToken token)
    {
        try
        {
            ZNodeChildren children = await Client
                .GetChildrenAsync(path, token)
                .ConfigureAwait(false);
            foreach (string child in children.Children)
                await DeleteTreeAsync(path + "/" + child, token).ConfigureAwait(false);
            await Client.DeleteAsync(path, ZNodeVersion.Any, token).ConfigureAwait(false);
        }
        catch (ZooKeeperException error) when (error.KnownErrorCode == ZooKeeperErrorCode.NoNode)
        { }
    }
}
