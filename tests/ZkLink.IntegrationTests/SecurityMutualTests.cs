namespace ZkLink.IntegrationTests;

[Collection("Security")]
[Trait("Fixture", "Security")]
[Trait("Transport", "MutualOnly")]
public sealed class SecurityMutualTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 30_000;
    private readonly SecurityFixture _fixture = new();

    public async ValueTask InitializeAsync()
    {
        await _fixture.InitializeAsync().ConfigureAwait(true);
        Assert.True(_fixture.Mutual);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData("missing")]
    [InlineData("untrusted")]
    [InlineData("expired")]
    [Trait("Acceptance", "T20")]
    public async Task ServerRejectsMissingUntrustedOrExpiredClientCertificate(string failure)
    {
        var certificate = failure switch
        {
            "untrusted" => _fixture.UntrustedCertificate,
            "expired" => _fixture.ExpiredCertificate,
            _ => null,
        };
        TestContext.Current.TestOutputHelper?.WriteLine("Starting rejection request.");
        var error = await Assert
            .ThrowsAsync<ZooKeeperConnectionException>(async () =>
            {
                var unexpected = await _fixture
                    .ConnectAsync(tls: _fixture.Tls(certificate), rejectionProbe: true)
                    .ConfigureAwait(true);
                await using var lifetime = unexpected.ConfigureAwait(true);
            })
            .ConfigureAwait(true);
        // TLS 1.3 can deliver the server's rejection after client-side TLS completion.
        // In every case no ZooKeeper session becomes available; the valid control stays usable.
        Assert.True(
            error.Failure
                is ZooKeeperConnectionFailure.Tls
                    or ZooKeeperConnectionFailure.Unreachable
                    or ZooKeeperConnectionFailure.Timeout
        );
        TestContext.Current.TestOutputHelper?.WriteLine($"Rejection completed: {error.Failure}.");
        _fixture.AssertSafe(error);
        TestContext.Current.TestOutputHelper?.WriteLine("Opening valid control connection.");
        var control = await _fixture.ConnectAsync().ConfigureAwait(true);
        await using var controlLifetime = control.ConfigureAwait(true);
        await control
            .SyncAsync(_fixture.Root, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T19")]
    public async Task TrustedClientCertificatesHaveDistinctX509AuthorizationIdentities()
    {
        var identities = await _fixture
            .Client.WhoAmIAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        string identity = Assert.Single(identities, static item => item.Scheme == "x509").User;
        string path = _fixture.Root + "/certificate-only";
        await _fixture
            .Client.CreateAsync(
                path,
                null,
                new CreateOptions([new ZooKeeperAcl(ZooKeeperPermissions.All, "x509", identity)]),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        var other = await _fixture
            .ConnectAsync(digest: false, tls: _fixture.Tls(_fixture.OtherCertificate))
            .ConfigureAwait(true);
        await using var otherLifetime = other.ConfigureAwait(true);
        var otherIdentities = await other
            .WhoAmIAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.NotEqual(
            identity,
            Assert.Single(otherIdentities, static item => item.Scheme == "x509").User
        );
        var error = await Assert
            .ThrowsAsync<ZooKeeperException>(() =>
                other.GetDataAsync(path, TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperErrorCode.NoAuth, error.KnownErrorCode);
        Assert.Equal(ZooKeeperSessionState.Connected, other.Session.State);
        await _fixture
            .Client.GetDataAsync(path, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }
}
