using System.Text;
using ZkLink.Configuration;
using ZkLink.Protocol;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class AuthenticationSessionTests : IAsyncLifetime
{
    private readonly SessionFixture _fixture = new();
    private readonly SessionPeer _first = new() { ReplyAuth = false };
    private readonly SessionPeer _second = new() { ReplyAuth = false };

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _fixture.DisposeAsync().ConfigureAwait(true);
        _first.Dispose();
        _second.Dispose();
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T21")]
    public async Task AuthenticationIsSerializedAndReplayedBeforeReadinessWithOwnedCredentials()
    {
        using var firstAuth = ZooKeeperAuthentication.Digest("usér", "sëcret");
        using var secondAuth = ZooKeeperAuthentication.Digest("second", "password");
        var credentials = new List<ZooKeeperAuthentication> { firstAuth, secondAuth };
        var options = SessionFixture.Options(
            connection: "one:2181,two:2181",
            authentication: credentials
        );
        var connecting = _fixture.ConnectAsync(
            _first,
            options,
            TestContext.Current.CancellationToken
        );
        DisposeCredentials(firstAuth, secondAuth);
        credentials.Clear();
        await SessionTestSupport.NextAsync(_first).ConfigureAwait(true);
        var auth = await SessionTestSupport.NextAsync(_first).ConfigureAwait(true);
        AssertAuth(auth, "usér:sëcret");
        Assert.False(connecting.IsCompleted);
        Assert.Equal(0, _first.Requests.Count);
        _first.Reply(auth);
        var nextAuth = await SessionTestSupport.NextAsync(_first).ConfigureAwait(true);
        AssertAuth(nextAuth, "second:password");
        Assert.False(connecting.IsCompleted);
        _first.Reply(nextAuth);
        var client = await SessionTestSupport.Bounded(connecting).ConfigureAwait(true);
        _fixture.Connector.Add(_second);
        var observer = new StateObserver(client);
        await using var observerLifetime = observer.ConfigureAwait(true);
        _first.Drop();
        await observer.WaitAsync(ZooKeeperSessionState.Disconnected).ConfigureAwait(true);
        await SessionTestSupport.NextAsync(_second).ConfigureAwait(true);
        auth = await SessionTestSupport.NextAsync(_second).ConfigureAwait(true);
        AssertAuth(auth, "usér:sëcret");
        var ready = client.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        Assert.False(ready.IsCompleted);
        Assert.Equal(ZooKeeperSessionState.Authenticating, client.Session.State);
        _second.Reply(auth);
        nextAuth = await SessionTestSupport.NextAsync(_second).ConfigureAwait(true);
        AssertAuth(nextAuth, "second:password");
        Assert.False(ready.IsCompleted);
        _second.Reply(nextAuth);
        await SessionTestSupport.Bounded(ready).ConfigureAwait(true);
        Assert.Equal(123, client.Session.SessionId);
        Assert.Equal(2, client.Session.ConnectionGeneration);
        Assert.True(_first.IsDisposed);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T21")]
    public async Task AuthenticationFailureStopsTheSessionAndRedactsCredentialText()
    {
        using var auth = ZooKeeperAuthentication.Digest("sensitive-user", "sensitive-password");
        var options = SessionFixture.Options(authentication: [auth]);
        var connecting = _fixture.ConnectAsync(
            _first,
            options,
            TestContext.Current.CancellationToken
        );
        await SessionTestSupport.NextAsync(_first).ConfigureAwait(true);
        var request = await SessionTestSupport.NextAsync(_first).ConfigureAwait(true);
        _first.Reply(request, -115);
        var error = await Assert
            .ThrowsAsync<ZooKeeperConnectionException>(() => SessionTestSupport.Bounded(connecting))
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperConnectionFailure.Authentication, error.Failure);
        Assert.Equal(-115, error.ErrorCode);
        Assert.DoesNotContain("sensitive-", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-", auth.ToString(), StringComparison.Ordinal);
        Assert.True(_first.IsDisposed);
        Assert.Equal(1, _fixture.Connector.Attempts.Reader.Count);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T19")]
    public async Task OperationNoAuthLeavesTheSessionUsable()
    {
        _first.ReplyApplications = false;
        var client = await _fixture
            .ConnectAsync(_first, cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(_first).ConfigureAwait(true);
        var denied = client.SyncAsync("/denied", TestContext.Current.CancellationToken);
        var request = await SessionTestSupport.NextAsync(_first).ConfigureAwait(true);
        _first.Reply(request, -102);
        var error = await Assert.ThrowsAsync<ZooKeeperException>(() => denied).ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Rejected, error.Outcome);
        Assert.Equal(ZooKeeperSessionState.Connected, client.Session.State);
        _first.ReplyApplications = true;
        await SessionTestSupport
            .Bounded(client.SyncAsync("/allowed", TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
    }

    [Fact]
    public void PlaintextDigestRequiresExplicitUnsafeOptIn()
    {
        using var auth = ZooKeeperAuthentication.Digest("user", "password");
        var options = new ZooKeeperClientOptions
        {
            ConnectionString = "localhost",
            Tls = ZooKeeperTlsOptions.Disabled(),
            Authentication = [auth],
        };
        Assert.Throws<ArgumentException>(() => ClientOptionsSnapshot.Capture(options));
    }

    [Fact(Timeout = 10000)]
    public async Task ExpiryAcknowledgmentDuringAuthenticationIsDistinguishedFromBadCredentials()
    {
        using var auth = ZooKeeperAuthentication.Digest("user", "password");
        var options = SessionFixture.Options(authentication: [auth]);
        var connecting = _fixture.ConnectAsync(
            _first,
            options,
            TestContext.Current.CancellationToken
        );
        await SessionTestSupport.NextAsync(_first).ConfigureAwait(true);
        var request = await SessionTestSupport.NextAsync(_first).ConfigureAwait(true);
        _first.Reply(request, -112);
        var error = await Assert
            .ThrowsAsync<ZooKeeperConnectionException>(() => connecting)
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperConnectionFailure.SessionRejected, error.Failure);
        Assert.Equal(-112, error.ErrorCode);
        Assert.True(_first.IsDisposed);
    }

    [Fact]
    public void OwnedAuthenticationIsIndependentAndZeroedOnSnapshotDisposal()
    {
        using var auth = ZooKeeperAuthentication.Digest("user", "password");
        using var snapshot = ClientOptionsSnapshot.Capture(
            SessionFixture.Options(authentication: [auth])
        );
        var owned = Assert.Single(snapshot.Authentication);
        auth.Dispose();
        Assert.Equal(Encoding.UTF8.GetBytes("user:password"), owned);
        snapshot.Dispose();
        Assert.All(owned, value => Assert.Equal(0, value));
        Assert.Empty(snapshot.Settings.Authentication);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public void NonFiniteOrOutOfRangeTimeoutsAreRejected(long ticks) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ClientOptionsSnapshot.ValidateTimeout(TimeSpan.FromTicks(ticks))
        );

    private static void DisposeCredentials(
        ZooKeeperAuthentication firstAuth,
        ZooKeeperAuthentication secondAuth
    )
    {
        firstAuth.Dispose();
        secondAuth.Dispose();
    }

    private static void AssertAuth(ReceivedPacket packet, string credentials)
    {
        Assert.Equal(-4, packet.Xid);
        Assert.Equal(100, packet.Opcode);
        var reader = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
        reader.ReadRequestHeader();
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal("digest", reader.ReadString());
        Assert.Equal(Encoding.UTF8.GetBytes(credentials), reader.ReadBuffer());
        reader.EnsureEnd();
    }
}
