using ZkLink.IntegrationTests;
using ZkLink.Samples.Basic;

namespace ZkLink.AotSmoke;

internal static class NativeRecovery
{
    private static readonly TimeSpan RecoveryGuard = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CleanupPoll = TimeSpan.FromMilliseconds(25);

    internal static async Task VerifyAsync(
        NativeSettings settings,
        ZooKeeperClient control,
        CancellationToken token
    )
    {
        foreach (
            var mode in new[]
            {
                PersistentWatchMode.Persistent,
                PersistentWatchMode.PersistentRecursive,
            }
        )
        {
            await VerifyModeAsync(settings, control, mode, token).ConfigureAwait(false);
        }

        await Console
            .Out.WriteLineAsync(
                "Native same-session persistent/recursive restore/gap/removal, cancellation and acknowledged-close ephemeral cleanup: verified."
            )
            .ConfigureAwait(false);
    }

    private static async Task VerifyModeAsync(
        NativeSettings settings,
        ZooKeeperClient control,
        PersistentWatchMode mode,
        CancellationToken token
    )
    {
        // Reuse the existing bounded opaque TLS tunnel; no credentials or Jute are inspected.
        var proxy = new WatchReconnectProxy(settings.Endpoint, inspectFrames: false, token);
        await using var proxyLifetime = proxy.ConfigureAwait(false);
        var owner = await settings.ConnectAsync(proxy.Endpoint, token).ConfigureAwait(false);
        await using var ownerLifetime = owner.ConfigureAwait(false);
        var path = "/zklink-native-recovery-" + Guid.NewGuid().ToString("N");
        var oncePath = path + "-once";
        await owner
            .CreateAsync(path, "before"u8.ToArray(), SampleNodes.EphemeralCreatorAcl(), token)
            .ConfigureAwait(false);
        await owner
            .CreateAsync(oncePath, "before"u8.ToArray(), SampleNodes.EphemeralCreatorAcl(), token)
            .ConfigureAwait(false);
        var acl = await owner.GetAclAsync(path, token).ConfigureAwait(false);
        await owner
            .SetAclAsync(
                path,
                [acl.Acl.Single(item => item.Scheme == "digest")],
                ZNodeVersion.Any,
                token
            )
            .ConfigureAwait(false);
        await owner
            .SetAclAsync(
                oncePath,
                [acl.Acl.Single(item => item.Scheme == "digest")],
                ZNodeVersion.Any,
                token
            )
            .ConfigureAwait(false);
        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(token);
        recovery.CancelAfter(RecoveryGuard);
        var recoveryToken = recovery.Token;
        var watch = await owner.AddWatchAsync(path, mode, recoveryToken).ConfigureAwait(false);
        await using var watchLifetime = watch.ConfigureAwait(false);
        var watched = await owner
            .GetDataAndWatchAsync(oncePath, recoveryToken)
            .ConfigureAwait(false);
        await using var onceLifetime = watched.Watch.ConfigureAwait(false);
        var states = owner.SubscribeStateChanges();
        await using var statesLifetime = states.ConfigureAwait(false);
        var stateReader = states.ReadStatesAsync(recoveryToken).GetAsyncEnumerator(recoveryToken);
        await using var stateLifetime = stateReader.ConfigureAwait(false);
        NativeSettings.Require(await stateReader.MoveNextAsync().ConfigureAwait(false));
        var session = owner.Session.SessionId;
        var generation = owner.Session.ConnectionGeneration;
        try
        {
            proxy.DropAndPause();
            do
            {
                NativeSettings.Require(await stateReader.MoveNextAsync().ConfigureAwait(false));
            } while (stateReader.Current.State != ZooKeeperSessionState.Disconnected);

            try
            {
                await owner.SyncAsync("/", recoveryToken).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "Disconnected admission accepted an ordinary request."
                );
            }
            catch (ZooKeeperException error)
            {
                NativeSettings.Require(error.Outcome == RequestOutcome.NotSent);
            }

            using (var cancelled = new CancellationTokenSource())
            {
                var wait = owner.WaitForConnectionAsync(cancelled.Token);
                NativeSettings.Require(!wait.IsCompleted);
                await cancelled.CancelAsync().ConfigureAwait(false);
                try
                {
                    await wait.ConfigureAwait(false);
                    throw new InvalidOperationException("Cancelled readiness wait succeeded.");
                }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            }

            await control
                .SetDataAsync(oncePath, "offline"u8.ToArray(), ZNodeVersion.Any, recoveryToken)
                .ConfigureAwait(false);
            proxy.Resume();
            await owner.WaitForConnectionAsync(recoveryToken).ConfigureAwait(false);
            NativeSettings.Require(
                owner.Session.SessionId == session
                    && owner.Session.ConnectionGeneration == generation + 1
            );
            var events = watch.ReadEventsAsync(recoveryToken).GetAsyncEnumerator(recoveryToken);
            await using var eventLifetime = events.ConfigureAwait(false);
            NativeSettings.Require(
                await events.MoveNextAsync().ConfigureAwait(false)
                    && events.Current.Kind == ZooKeeperWatchEventKind.ReconnectedWithGap
                    && events.Current.ConnectionGeneration == generation + 1
            );
            var once = watched
                .Watch.ReadEventsAsync(recoveryToken)
                .GetAsyncEnumerator(recoveryToken);
            await using var onceEventLifetime = once.ConfigureAwait(false);
            NativeSettings.Require(
                await once.MoveNextAsync().ConfigureAwait(false)
                    && once.Current.Kind == ZooKeeperWatchEventKind.NodeDataChanged
            );
            NativeSettings.Require(!await once.MoveNextAsync().ConfigureAwait(false));
            var current = await owner.GetDataAsync(path, recoveryToken).ConfigureAwait(false);
            NativeSettings.Require(
                current.Stat.EphemeralOwner == session
                    && current.Data is { } data
                    && data.Span.SequenceEqual("before"u8)
            );
            await control
                .SetDataAsync(
                    path,
                    "after"u8.ToArray(),
                    ZNodeVersion.Exact(current.Stat.Version),
                    recoveryToken
                )
                .ConfigureAwait(false);
            NativeSettings.Require(
                await events.MoveNextAsync().ConfigureAwait(false)
                    && events.Current.Kind == ZooKeeperWatchEventKind.NodeDataChanged
            );
            var removed = await watch
                .RemoveAsync(WatchRemovalMode.ServerConfirmed, recoveryToken)
                .ConfigureAwait(false);
            NativeSettings.Require(removed.Status == WatchRemovalStatus.ServerConfirmed);
            NativeSettings.Require(!await events.MoveNextAsync().ConfigureAwait(false));
            using var cancelledRequest = new CancellationTokenSource();
            await cancelledRequest.CancelAsync().ConfigureAwait(false);
            try
            {
                await owner.SyncAsync("/", cancelledRequest.Token).ConfigureAwait(false);
                throw new InvalidOperationException("Pre-cancelled request succeeded.");
            }
            catch (ZooKeeperOperationCanceledException error)
            {
                NativeSettings.Require(
                    error.Outcome == RequestOutcome.NotSent
                        && error.CancellationToken == cancelledRequest.Token
                );
            }

            await NativeSettings.CloseAsync(owner, recoveryToken).ConfigureAwait(false);
            while (await control.ExistsAsync(path, recoveryToken).ConfigureAwait(false) is not null)
            {
                await Task.Delay(CleanupPoll, recoveryToken).ConfigureAwait(false);
            }
            while (
                await control.ExistsAsync(oncePath, recoveryToken).ConfigureAwait(false) is not null
            )
            {
                await Task.Delay(CleanupPoll, recoveryToken).ConfigureAwait(false);
            }
        }
        finally
        {
            proxy.Resume();
        }
    }
}
