namespace ZkLink.IntegrationTests;

[Collection("Security")]
[Trait("Fixture", "Security")]
public sealed class SecurityRejectionTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 30_000;
    private readonly SecurityFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData("name")]
    [InlineData("trust")]
    [InlineData("expired")]
    [InlineData("plaintext")]
    [Trait("Acceptance", "T20")]
    public async Task RequiredTlsRejectsInvalidServerAndCannotDowngrade(string failure)
    {
        var tls = _fixture.Tls(
            _fixture.Mutual ? _fixture.Certificate : null,
            root: failure == "trust" ? _fixture.UntrustedRoot : null,
            serverName: failure == "name" ? "wrong.zklink.test" : "zklink.test"
        );
        string endpoint = failure switch
        {
            "expired" => _fixture.ExpiredEndpoint,
            "plaintext" => _fixture.PlaintextEndpoint,
            _ => _fixture.ConnectionString.Split(',')[0],
        };
        TestContext.Current.TestOutputHelper?.WriteLine("Starting rejection request.");
        var error = await Assert
            .ThrowsAsync<ZooKeeperConnectionException>(async () =>
            {
                var unexpected = await _fixture
                    .ConnectAsync(endpoint, tls: tls, rejectionProbe: true)
                    .ConfigureAwait(true);
                await using var lifetime = unexpected.ConfigureAwait(true);
            })
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperConnectionFailure.Tls, error.Failure);
        TestContext.Current.TestOutputHelper?.WriteLine($"Rejection completed: {error.Failure}.");
        _fixture.AssertSafe(error);
        await _fixture
            .Client.SyncAsync(_fixture.Root, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }
}
