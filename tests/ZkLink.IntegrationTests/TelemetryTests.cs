using System.Diagnostics;
using System.Text;
using ZkLink.Testing;

namespace ZkLink.IntegrationTests;

[CollectionDefinition("Telemetry", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Maintainability",
    "CA1515",
    Justification = "xUnit collection definitions must be public for discovery (xUnit1027)."
)]
public sealed class TelemetryIsolation;

[Collection("Telemetry")]
public sealed class TelemetryTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 60_000;
    private const string SecretMarker = "private-live-telemetry-marker";
    private static readonly TimeSpan EventGuard = TimeSpan.FromSeconds(15);
    private readonly LiveZooKeeperFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T34")]
    public async Task OperationsAclRejectionAndRestorationEmitRedactedTelemetry()
    {
        using var capture = new TelemetryCapture();
        using var parent = new Activity("consumer");
        parent.Start();
        parent.AddBaggage("private", SecretMarker);
        var proxy = new WatchReconnectProxy(
            _fixture.ConnectionString,
            TestContext.Current.CancellationToken
        );
        await using var proxyLifetime = proxy.ConfigureAwait(true);
        var owner = await _fixture
            .ConnectAsync(connectionString: proxy.Endpoint)
            .ConfigureAwait(true);
        await using var ownerLifetime = owner.ConfigureAwait(true);
        string path = _fixture.Root + "/" + SecretMarker;
        byte[] data = Encoding.UTF8.GetBytes(SecretMarker);
        await owner
            .CreateAsync(
                path,
                data,
                LiveZooKeeperFixture.OwnerCreate,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        var watch = await owner
            .AddWatchAsync(
                path,
                PersistentWatchMode.Persistent,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await using var watchLifetime = watch.ConfigureAwait(true);
        var states = owner.SubscribeStateChanges();
        await using var statesLifetime = states.ConfigureAwait(true);
        var reader = states
            .ReadStatesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var readerLifetime = reader.ConfigureAwait(true);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        long session = owner.Session.SessionId;
        proxy.DropAndPause();
        do
        {
            Assert.True(
                await reader
                    .MoveNextAsync()
                    .AsTask()
                    .WaitAsync(EventGuard, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            );
        } while (reader.Current.State != ZooKeeperSessionState.Disconnected);
        proxy.Resume();
        await owner
            .WaitForConnectionAsync(TestContext.Current.CancellationToken)
            .WaitAsync(EventGuard, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(session, owner.Session.SessionId);
        await owner
            .SetDataAsync(path, data, ZNodeVersion.Any, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(
            data,
            (
                await owner
                    .GetDataAsync(path, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            ).Data!.Value.ToArray()
        );
        var anonymous = await _fixture.ConnectAsync(authenticated: false).ConfigureAwait(true);
        await using (anonymous.ConfigureAwait(true))
        {
            var error = await Assert
                .ThrowsAsync<ZooKeeperException>(() =>
                    anonymous.GetDataAsync(path, TestContext.Current.CancellationToken)
                )
                .ConfigureAwait(true);
            Assert.Equal(ZooKeeperErrorCode.NoAuth, error.KnownErrorCode);
            await anonymous.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        await watch.DisposeAsync().ConfigureAwait(true);
        await owner
            .DeleteAsync(path, ZNodeVersion.Any, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await owner.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await capture
            .WaitForAsync(
                static activity => activity.Name == "zklink.close",
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        // The owner's last operation span precedes its close in the diagnostic FIFO.
        await capture
            .WaitForAsync(
                static activity =>
                    activity.Name == "zklink.operation"
                    && activity.Tags.Any(static tag =>
                        tag.Key == "operation" && Equals(tag.Value, "Delete")
                    ),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        capture.Collect();
        Assert.Contains(
            capture.Activities,
            static activity => activity.Name == "zklink.authenticate"
        );
        Assert.True(
            capture.Activities.Count(static activity => activity.Name == "zklink.restore") >= 2
        );
        Assert.Contains(
            capture.Activities,
            static activity =>
                activity.Name == "zklink.operation"
                && activity.Tags.Any(static tag =>
                    tag.Key == "outcome" && Equals(tag.Value, "Rejected")
                )
        );
        Assert.True(capture.Value("zklink.reconnect.attempts") >= 1);
        Assert.Contains(
            capture.Histograms,
            static value => value.Name == "zklink.session.disconnected.duration"
        );
        capture.AssertRedacted(SecretMarker, path);
    }
}
