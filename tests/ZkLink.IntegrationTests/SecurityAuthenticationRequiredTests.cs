using System.Diagnostics;
using ZkLink.Testing;

namespace ZkLink.IntegrationTests;

[Collection("Security")]
[Trait("Fixture", "AuthenticationRequired")]
[Trait("AuthenticationPolicy", "digest")]
public sealed class SecurityDigestRequiredTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly CreateOptions CreatorEphemeral = new(
        [new(ZooKeeperPermissions.All, "auth", "")],
        CreateMode.Ephemeral
    );

    [Theory(Timeout = AuthenticationRequiredFixture.TestTimeoutMilliseconds)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Acceptance", "T19")]
    [Trait("Acceptance", "T21")]
    [Trait("Acceptance", "T26")]
    [Trait("Acceptance", "T34")]
    public async Task MultipleUtf8CredentialsReplayBeforeProtectedWatchRestoration(int node)
    {
        var fixture = new AuthenticationRequiredFixture("digest");
        var token = TestContext.Current.CancellationToken;
        using var capture = new TelemetryCapture();
        string suffix = Guid.NewGuid().ToString("N");
        string firstUser = "zklink-é-" + suffix;
        string secondUser = "zklink-一-" + suffix;
        string firstPassword = fixture.NewPassword();
        string secondPassword = fixture.NewPassword();
        using var first = ZooKeeperAuthentication.Digest(firstUser, firstPassword);
        using var second = ZooKeeperAuthentication.Digest(secondUser, secondPassword);
        var proxy = new WatchReconnectProxy(fixture.Endpoint(node), token);
        await using var proxyLifetime = proxy.ConfigureAwait(true);
        var owner = await fixture
            .ConnectAsync(proxy.Endpoint, [first, second], token)
            .ConfigureAwait(true);
        await using var ownerLifetime = owner.ConfigureAwait(true);
        string path = "/zklink-authentication-" + suffix;
        await owner
            .CreateAsync(path, new byte[] { 1 }, CreatorEphemeral, token)
            .ConfigureAwait(true);
        var acl = await owner.GetAclAsync(path, token).ConfigureAwait(true);
        Assert.Equal(2, acl.Acl.Count);
        Assert.True(acl.Acl.All(entry => entry.Scheme == "digest"));
        Assert.Contains(
            acl.Acl,
            entry => entry.Id.StartsWith(firstUser + ":", StringComparison.Ordinal)
        );
        Assert.Contains(
            acl.Acl,
            entry => entry.Id.StartsWith(secondUser + ":", StringComparison.Ordinal)
        );
        foreach (var identity in new[] { first, second })
        {
            var reader = await fixture
                .ConnectAsync(fixture.Endpoint(node), [identity], token)
                .ConfigureAwait(true);
            await using var readerLifetime = reader.ConfigureAwait(true);
            Assert.NotNull((await reader.GetDataAsync(path, token).ConfigureAwait(true)).Data);
            await reader.CloseAsync(token).ConfigureAwait(true);
            Assert.Equal(ZooKeeperCloseStatus.Acknowledged, reader.Session.CloseStatus);
        }

        var watch = await owner
            .AddWatchAsync(path, PersistentWatchMode.Persistent, token)
            .ConfigureAwait(true);
        await using var watchLifetime = watch.ConfigureAwait(true);
        long session = owner.Session.SessionId;
        long generation = owner.Session.ConnectionGeneration;
        proxy.DropAndPause();
        var timer = Stopwatch.StartNew();
        while (owner.Session.State == ZooKeeperSessionState.Connected)
        {
            Assert.True(timer.Elapsed < AuthenticationRequiredFixture.EventGuard);
            await Task.Delay(PollInterval, token).ConfigureAwait(true);
        }
        var ready = owner.WaitForConnectionAsync(token);
        Assert.False(ready.IsCompleted);
        proxy.Resume();
        await ready.WaitAsync(AuthenticationRequiredFixture.EventGuard, token).ConfigureAwait(true);
        Assert.Equal(session, owner.Session.SessionId);
        Assert.True(owner.Session.ConnectionGeneration > generation);
        Assert.Equal(1, proxy.RestoreReplies);
        var identities = await owner.WhoAmIAsync(token).ConfigureAwait(true);
        Assert.Equal(2, identities.Count(identity => identity.Scheme == "digest"));
        await owner.GetDataAsync(path, token).ConfigureAwait(true);

        using var wrong = ZooKeeperAuthentication.Digest(firstUser, fixture.NewPassword());
        var denied = await fixture
            .ConnectAsync(fixture.Endpoint(node), [wrong], token)
            .ConfigureAwait(true);
        await using var deniedLifetime = denied.ConfigureAwait(true);
        var noAuth = await Assert
            .ThrowsAsync<ZooKeeperException>(() => denied.GetDataAsync(path, token))
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperErrorCode.NoAuth, noAuth.KnownErrorCode);
        Assert.Equal(RequestOutcome.Rejected, noAuth.Outcome);
        Assert.Equal(ZooKeeperSessionState.Connected, denied.Session.State);
        await denied.SyncAsync("/", token).ConfigureAwait(true);
        await denied.CloseAsync(token).ConfigureAwait(true);
        await owner.CloseAsync(token).ConfigureAwait(true);
        Assert.Equal(ZooKeeperCloseStatus.Acknowledged, owner.Session.CloseStatus);
        var control = await fixture
            .ConnectAsync(fixture.Endpoint(node), [first], token)
            .ConfigureAwait(true);
        await using var controlLifetime = control.ConfigureAwait(true);
        Assert.Null(await control.ExistsAsync(path, token).ConfigureAwait(true));
        await control.CloseAsync(token).ConfigureAwait(true);
        capture.Collect();
        capture.AssertRedacted(firstUser, secondUser, firstPassword, secondPassword, path);
        TestContext.Current.TestOutputHelper?.WriteLine(
            "Required digest: PASS (two UTF-8 identities, auth replay before watch reset, NoAuth stays per operation, acknowledged close)."
        );
    }

    [Theory(Timeout = AuthenticationRequiredFixture.TestTimeoutMilliseconds)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Acceptance", "T21")]
    public Task UnauthenticatedSessionFailsTerminally(int node) =>
        new AuthenticationRequiredFixture("digest").AssertTerminalRejectionAsync(
            node,
            digest: false,
            TestContext.Current.CancellationToken
        );
}

[Collection("Security")]
[Trait("Fixture", "AuthenticationRequired")]
[Trait("AuthenticationPolicy", "sasl")]
public sealed class SecuritySaslRequiredTests
{
    [Theory(Timeout = AuthenticationRequiredFixture.TestTimeoutMilliseconds)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [Trait("Acceptance", "T21")]
    public Task UnsupportedSaslPolicyFailsTerminallyWithOrWithoutDigest(int node, bool digest) =>
        new AuthenticationRequiredFixture("sasl").AssertTerminalRejectionAsync(
            node,
            digest,
            TestContext.Current.CancellationToken
        );
}
