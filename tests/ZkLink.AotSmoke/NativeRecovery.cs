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
        await VerifySentCancellationAsync(settings, control, token).ConfigureAwait(false);
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

    private static async Task VerifySentCancellationAsync(
        NativeSettings settings,
        ZooKeeperClient control,
        CancellationToken token
    )
    {
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
        guard.CancelAfter(RecoveryGuard);
        var bounded = guard.Token;
        var proxy = new WatchReconnectProxy(settings.Endpoint, inspectFrames: false, bounded);
        await using var proxyLifetime = proxy.ConfigureAwait(false);
        var owner = await settings.ConnectAsync(proxy.Endpoint, bounded).ConfigureAwait(false);
        await using var ownerLifetime = owner.ConfigureAwait(false);
        string root = SampleNodes.UniquePath("/zklink-native-unknown-");
        const string AttemptPrefix = "attempt-";
        Task<CreateResult>? uncertain = null;
        await control
            .CreateAsync(
                root,
                null,
                new CreateOptions([new ZooKeeperAcl(ZooKeeperPermissions.All, "auth", "")]),
                bounded
            )
            .ConfigureAwait(false);
        try
        {
            long session = owner.Session.SessionId;
            long generation = owner.Session.ConnectionGeneration;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(bounded);
            proxy.HoldIncoming();
            uncertain = owner.CreateAsync(
                root + "/" + AttemptPrefix,
                null,
                new CreateOptions(
                    [new ZooKeeperAcl(ZooKeeperPermissions.All, "auth", "")],
                    CreateMode.PersistentSequential
                ),
                cancellation.Token
            );
            await proxy.IncomingHeld.WaitAsync(bounded).ConfigureAwait(false);
            // An independent authenticated session proves the write reached and
            // committed at the server before caller cancellation is requested.
            while (
                (await control.GetChildrenAsync(root, bounded).ConfigureAwait(false)).Children.Count
                == 0
            )
            {
                await Task.Delay(CleanupPoll, bounded).ConfigureAwait(false);
            }
            await cancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await uncertain.ConfigureAwait(false);
                throw new InvalidOperationException("A committed held reply completed the caller.");
            }
            catch (ZooKeeperOperationCanceledException failure)
            {
                NativeSettings.Require(
                    failure.Outcome == RequestOutcome.Unknown
                        && failure.Request.Operation == ZooKeeperOperation.CreateWithStat
                        && failure.CancellationToken == cancellation.Token
                );
            }
            proxy.DropAndPause();
            proxy.ReleaseIncoming();
            proxy.Resume();
            await owner.WaitForConnectionAsync(bounded).ConfigureAwait(false);
            // WaitForConnection can observe the previous Connected snapshot before
            // the closed socket is read. A protected operation is the next barrier.
            while (owner.Session.ConnectionGeneration == generation)
            {
                await Task.Delay(CleanupPoll, bounded).ConfigureAwait(false);
            }
            await owner.WaitForConnectionAsync(bounded).ConfigureAwait(false);
            NativeSettings.Require(owner.Session.SessionId == session);
            await owner.SyncAsync(root, bounded).ConfigureAwait(false);
            var children = (
                await control.GetChildrenAsync(root, bounded).ConfigureAwait(false)
            ).Children;
            NativeSettings.Require(
                children.Count == 1
                    && children[0].StartsWith(AttemptPrefix, StringComparison.Ordinal)
            );
            await NativeSettings.CloseAsync(owner, bounded).ConfigureAwait(false);
            await Console
                .Out.WriteLineAsync(
                    "Native sent cancellation: PASS (Unknown, same session, one committed child, no replay)."
                )
                .ConfigureAwait(false);
        }
        finally
        {
            proxy.ReleaseIncoming();
            proxy.Resume();
            if (uncertain is not null)
            {
                try
                {
                    await uncertain.ConfigureAwait(false);
                }
                catch (ZooKeeperException) { }
                catch (OperationCanceledException) { }
            }
            using var cleanup = new CancellationTokenSource(RecoveryGuard);
            foreach (
                string child in (
                    await control.GetChildrenAsync(root, cleanup.Token).ConfigureAwait(false)
                ).Children
            )
            {
                await control
                    .DeleteAsync(root + "/" + child, ZNodeVersion.Any, cleanup.Token)
                    .ConfigureAwait(false);
            }
            await control.DeleteAsync(root, ZNodeVersion.Any, cleanup.Token).ConfigureAwait(false);
        }
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
