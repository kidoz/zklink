using System.Security.Cryptography;

namespace ZkLink.IntegrationTests;

internal sealed class AuthenticationRequiredFixture
{
    internal const int TestTimeoutMilliseconds = 30_000;
    internal static readonly TimeSpan EventGuard = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InitialGuard = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AttemptGuard = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OperationGuard = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CloseGuard = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(1);
    private const int TerminalProbeCount = 3;
    private const int MemberCount = 3;
    private readonly string _private;
    private readonly string[] _endpoints;
    private readonly ZooKeeperCompatibilityProfile _profile;

    internal AuthenticationRequiredFixture(string expectedScheme)
    {
        string scheme = Required("ZKLINK_TEST_AUTH_SCHEME");
        string version = Required("ZKLINK_TEST_SERVER_VERSION");
        _endpoints = Required("ZKLINK_TEST_CONNECTION_STRING").Split(',');
        _private = Required("ZKLINK_AUTH_PRIVATE_DIRECTORY");
        if (
            scheme != expectedScheme
            || version is not ("3.8.7" or "3.9.6")
            || _endpoints.Length != MemberCount
            || _endpoints.Distinct().Count() != MemberCount
            || _endpoints.Any(endpoint =>
                !Uri.TryCreate("tcp://" + endpoint, UriKind.Absolute, out var uri)
                || uri.Host != "127.0.0.1"
                || uri.Port <= 0
            )
            || !Path.IsPathFullyQualified(_private)
            || !Directory.Exists(_private)
            || !Path.GetFileName(_private)
                .StartsWith("zklink-authentication-private.", StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException(
                "The owned authentication-policy fixture is required."
            );
        }
        _profile =
            version == "3.9.6"
                ? ZooKeeperCompatibilityProfile.ZooKeeper39
                : ZooKeeperCompatibilityProfile.ZooKeeper38;
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(
            "Run just zk-authentication with its disposable fixtures."
        );

    internal string Endpoint(int node) => _endpoints[node];

    internal string NewPassword()
    {
        string password = "sëcret-一-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        // The shell's final scan redacts accidental secret output before private cleanup.
        File.WriteAllText(
            Path.Combine(_private, Guid.NewGuid().ToString("N") + ".secret"),
            password
        );
        return password;
    }

    internal Task<ZooKeeperClient> ConnectAsync(
        string endpoint,
        IReadOnlyList<ZooKeeperAuthentication> authentication,
        CancellationToken token
    ) =>
        ZooKeeperClient.ConnectAsync(
            new ZooKeeperClientOptions
            {
                ConnectionString = endpoint,
                Tls = ZooKeeperTlsOptions.Disabled(),
                AllowUnsafePlaintextDigest = true,
                Authentication = authentication,
                CompatibilityProfile = _profile,
                InitialConnectTimeout = InitialGuard,
                ConnectAttemptTimeout = AttemptGuard,
                OperationTimeout = OperationGuard,
                CloseTimeout = CloseGuard,
            },
            token
        );

    internal async Task AssertTerminalRejectionAsync(int node, bool digest, CancellationToken token)
    {
        using var auth = ZooKeeperAuthentication.Digest("zklink-unsupported-é", NewPassword());
        var client = await ConnectAsync(Endpoint(node), digest ? [auth] : [], token)
            .ConfigureAwait(true);
        await using var lifetime = client.ConfigureAwait(true);
        var error = await Assert
            .ThrowsAsync<ZooKeeperException>(() => client.SyncAsync("/", token))
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperErrorCode.SessionClosedRequiresSaslAuth, error.KnownErrorCode);
        Assert.Equal(RequestOutcome.Rejected, error.Outcome);
        Assert.Equal(ZooKeeperOperation.Sync, error.Request.Operation);
        Assert.NotNull(error.Request.RequestId);
        Assert.Equal(ZooKeeperSessionState.AuthFailed, client.Session.State);
        var terminal = await Assert
            .ThrowsAsync<ZooKeeperSessionException>(() => client.WaitForConnectionAsync(token))
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.AuthFailed, terminal.State);
        Assert.Equal((int)ZooKeeperErrorCode.SessionClosedRequiresSaslAuth, terminal.ErrorCode);
        long session = client.Session.SessionId;
        long generation = client.Session.ConnectionGeneration;
        for (int probe = 0; probe < TerminalProbeCount; probe++)
        {
            await Task.Delay(ProbeInterval, token).ConfigureAwait(true);
            var rejected = await Assert
                .ThrowsAsync<ZooKeeperException>(() => client.SyncAsync("/", token))
                .ConfigureAwait(true);
            Assert.Equal(RequestOutcome.NotSent, rejected.Outcome);
            Assert.Equal(ZooKeeperSessionState.AuthFailed, client.Session.State);
            Assert.Equal(session, client.Session.SessionId);
            Assert.Equal(generation, client.Session.ConnectionGeneration);
        }
        TestContext.Current.TestOutputHelper?.WriteLine(
            "Required authentication: PASS (typed rejection, terminal AuthFailed, closed admission, no replacement)."
        );
    }
}
