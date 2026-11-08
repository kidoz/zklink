using ZkLink.Samples.Basic;

namespace ZkLink.IntegrationTests;

// Exercises the actual sample implementation through the raw client's public API.
public sealed class ElectionSampleTests : IAsyncLifetime
{
    private const int TimeoutMilliseconds = 60_000;
    private readonly LiveZooKeeperFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact(Timeout = TimeoutMilliseconds)]
    [Trait("Acceptance", "T15")]
    [Trait("Acceptance", "T23")]
    public async Task SeparateSessionsWatchOnlyTheirPredecessorsAndTakeTurnsAfterClose()
    {
        var token = TestContext.Current.CancellationToken;
        var first = await _fixture.ConnectAsync().ConfigureAwait(true);
        await using var firstLifetime = first.ConfigureAwait(true);
        var second = await _fixture.ConnectAsync().ConfigureAwait(true);
        await using var secondLifetime = second.ConfigureAwait(true);
        var third = await _fixture.ConnectAsync().ConfigureAwait(true);
        await using var thirdLifetime = third.ConfigureAwait(true);
        Assert.Equal(
            3,
            new[] { first.Session.SessionId, second.Session.SessionId, third.Session.SessionId }
                .Distinct()
                .Count()
        );
        var a = await ElectionCandidate.JoinAsync(first, _fixture.Root, token).ConfigureAwait(true);
        var b = await ElectionCandidate
            .JoinAsync(second, _fixture.Root, token)
            .ConfigureAwait(true);
        var c = await ElectionCandidate.JoinAsync(third, _fixture.Root, token).ConfigureAwait(true);
        Assert.Null(await a.GetPredecessorAsync(token).ConfigureAwait(true));
        Assert.Equal(a.Path, await b.GetPredecessorAsync(token).ConfigureAwait(true));
        Assert.Equal(b.Path, await c.GetPredecessorAsync(token).ConfigureAwait(true));
        using var waits = CancellationTokenSource.CreateLinkedTokenSource(token);
        var next = b.WaitForLeadershipAsync(waits.Token);
        var last = c.WaitForLeadershipAsync(waits.Token);
        try
        {
            // Neither later candidate may win while both predecessors still exist.
            await second.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
            await third.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
            Assert.False(next.IsCompleted);
            Assert.False(last.IsCompleted);
            await first.CloseAsync(token).ConfigureAwait(true);
            await next.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(true);
            await _fixture.Client.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
            Assert.Null(await _fixture.Client.ExistsAsync(a.Path, token).ConfigureAwait(true));
            Assert.Null(await b.GetPredecessorAsync(token).ConfigureAwait(true));
            Assert.Equal(b.Path, await c.GetPredecessorAsync(token).ConfigureAwait(true));
            Assert.False(last.IsCompleted);
            await second.CloseAsync(token).ConfigureAwait(true);
            await last.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(true);
            Assert.Null(await c.GetPredecessorAsync(token).ConfigureAwait(true));
            await third.CloseAsync(token).ConfigureAwait(true);
            Assert.Equal(ZooKeeperCloseStatus.Acknowledged, third.Session.CloseStatus);
            // This independent observer must catch up with the closing participant.
            await _fixture.Client.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
            Assert.Empty(
                (
                    await _fixture
                        .Client.GetChildrenAsync(_fixture.Root, token)
                        .ConfigureAwait(true)
                ).Children
            );
        }
        finally
        {
            await waits.CancelAsync().ConfigureAwait(true);
            try
            {
                await Task.WhenAll(next, last).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (waits.IsCancellationRequested) { }
        }
    }

    [Fact(Timeout = TimeoutMilliseconds)]
    [Trait("Acceptance", "T24")]
    public async Task PredecessorDeletedBeforeWatchRegistrationDoesNotBlock()
    {
        var token = TestContext.Current.CancellationToken;
        var first = await _fixture.ConnectAsync().ConfigureAwait(true);
        await using var firstLifetime = first.ConfigureAwait(true);
        var second = await _fixture.ConnectAsync().ConfigureAwait(true);
        await using var secondLifetime = second.ConfigureAwait(true);
        var a = await ElectionCandidate.JoinAsync(first, _fixture.Root, token).ConfigureAwait(true);
        var b = await ElectionCandidate
            .JoinAsync(second, _fixture.Root, token)
            .ConfigureAwait(true);
        var predecessor = await b.GetPredecessorAsync(token).ConfigureAwait(true);
        Assert.Equal(a.Path, predecessor);
        await first.CloseAsync(token).ConfigureAwait(true);
        await b.WaitForPredecessorChangeAsync(predecessor!, token)
            .WaitAsync(TimeSpan.FromSeconds(5), token)
            .ConfigureAwait(true);
        await b.WaitForLeadershipAsync(token).ConfigureAwait(true);
        await second.CloseAsync(token).ConfigureAwait(true);
    }

    [Fact(Timeout = TimeoutMilliseconds)]
    [Trait("Acceptance", "T23")]
    public async Task DataInvalidationRequiresRelistingAndAnotherWatch()
    {
        var token = TestContext.Current.CancellationToken;
        var first = await _fixture.ConnectAsync().ConfigureAwait(true);
        await using var firstLifetime = first.ConfigureAwait(true);
        var second = await _fixture.ConnectAsync().ConfigureAwait(true);
        await using var secondLifetime = second.ConfigureAwait(true);
        var a = await ElectionCandidate.JoinAsync(first, _fixture.Root, token).ConfigureAwait(true);
        var b = await ElectionCandidate
            .JoinAsync(second, _fixture.Root, token)
            .ConfigureAwait(true);
        using var waits = CancellationTokenSource.CreateLinkedTokenSource(token);
        var change = b.WaitForPredecessorChangeAsync(a.Path, waits.Token);
        try
        {
            await second.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
            Assert.False(change.IsCompleted);
            await first
                .SetDataAsync(a.Path, "changed"u8.ToArray(), ZNodeVersion.Any, token)
                .ConfigureAwait(true);
            await change.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(true);
            Assert.Equal(a.Path, await b.GetPredecessorAsync(token).ConfigureAwait(true));
            // Reuse the loop after a non-deletion event; it must still wait for deletion.
            change = b.WaitForLeadershipAsync(waits.Token);
            await second.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
            Assert.False(change.IsCompleted);
            await first.CloseAsync(token).ConfigureAwait(true);
            await change.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(true);
            await second.CloseAsync(token).ConfigureAwait(true);
        }
        finally
        {
            await waits.CancelAsync().ConfigureAwait(true);
            try
            {
                await change.ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (waits.IsCancellationRequested) { }
        }
    }

    [Fact(Timeout = TimeoutMilliseconds)]
    [Trait("Acceptance", "T14")]
    public async Task CancelledWaitDoesNotWinAndClosingParticipantRemovesItsNode()
    {
        var token = TestContext.Current.CancellationToken;
        var first = await _fixture.ConnectAsync().ConfigureAwait(true);
        await using var firstLifetime = first.ConfigureAwait(true);
        var second = await _fixture.ConnectAsync().ConfigureAwait(true);
        await using var secondLifetime = second.ConfigureAwait(true);
        var a = await ElectionCandidate.JoinAsync(first, _fixture.Root, token).ConfigureAwait(true);
        var b = await ElectionCandidate
            .JoinAsync(second, _fixture.Root, token)
            .ConfigureAwait(true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var wait = b.WaitForLeadershipAsync(cancellation.Token);
        try
        {
            await second.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait).ConfigureAwait(true);
        Assert.NotNull(await _fixture.Client.ExistsAsync(a.Path, token).ConfigureAwait(true));
        await second.CloseAsync(token).ConfigureAwait(true);
        await _fixture.Client.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
        Assert.Null(await _fixture.Client.ExistsAsync(b.Path, token).ConfigureAwait(true));
        await first.CloseAsync(token).ConfigureAwait(true);
    }

    [Fact(Timeout = TimeoutMilliseconds)]
    public async Task MissingOwnNodeCannotBecomeLeader()
    {
        var token = TestContext.Current.CancellationToken;
        var client = await _fixture.ConnectAsync().ConfigureAwait(true);
        await using var clientLifetime = client.ConfigureAwait(true);
        var candidate = await ElectionCandidate
            .JoinAsync(client, _fixture.Root, token)
            .ConfigureAwait(true);
        await _fixture
            .Client.DeleteAsync(candidate.Path, ZNodeVersion.Any, token)
            .ConfigureAwait(true);
        await client.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
        await Assert
            .ThrowsAsync<InvalidOperationException>(() => candidate.WaitForLeadershipAsync(token))
            .ConfigureAwait(true);
        await client.CloseAsync(token).ConfigureAwait(true);
    }

    [Fact(Timeout = TimeoutMilliseconds)]
    [Trait("Acceptance", "T10")]
    public async Task ReconnectedSessionCannotReuseTheOldElectionObservation()
    {
        var token = TestContext.Current.CancellationToken;
        var proxy = new WatchReconnectProxy(_fixture.ConnectionString, token);
        await using var proxyLifetime = proxy.ConfigureAwait(true);
        var client = await _fixture
            .ConnectAsync(connectionString: proxy.Endpoint)
            .ConfigureAwait(true);
        await using var clientLifetime = client.ConfigureAwait(true);
        var candidate = await ElectionCandidate
            .JoinAsync(client, _fixture.Root, token)
            .ConfigureAwait(true);
        var generation = client.Session.ConnectionGeneration;
        var states = client.SubscribeStateChanges();
        await using var statesLifetime = states.ConfigureAwait(true);
        var reader = states.ReadStatesAsync(token).GetAsyncEnumerator(token);
        await using var readerLifetime = reader.ConfigureAwait(true);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        proxy.DropAndPause();
        try
        {
            do
            {
                Assert.True(
                    await reader
                        .MoveNextAsync()
                        .AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(10), token)
                        .ConfigureAwait(true)
                );
            } while (reader.Current.State != ZooKeeperSessionState.Disconnected);
            await Assert
                .ThrowsAsync<InvalidOperationException>(() =>
                    candidate.WaitForLeadershipAsync(token)
                )
                .ConfigureAwait(true);
        }
        finally
        {
            proxy.Resume();
        }
        await client
            .WaitForConnectionAsync(token)
            .WaitAsync(TimeSpan.FromSeconds(10), token)
            .ConfigureAwait(true);
        Assert.True(client.Session.ConnectionGeneration > generation);
        await Assert
            .ThrowsAsync<InvalidOperationException>(() => candidate.WaitForLeadershipAsync(token))
            .ConfigureAwait(true);
        await client.CloseAsync(token).ConfigureAwait(true);
    }
}
