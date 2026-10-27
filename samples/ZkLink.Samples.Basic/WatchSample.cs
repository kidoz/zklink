using ZkLink;

namespace ZkLink.Samples.Basic;

internal sealed class WatchSample : ISampleStep
{
    private static readonly PersistentWatchMode[] PersistentModes =
    [
        PersistentWatchMode.Persistent,
        PersistentWatchMode.PersistentRecursive,
    ];

    public async Task<string> RunAsync(ZooKeeperClient client, CancellationToken cancellationToken)
    {
        var path = SampleNodes.UniquePath("/zklink-watch-sample-");
        await VerifyOneShotAsync(client, path, cancellationToken).ConfigureAwait(false);
        foreach (var mode in PersistentModes)
        {
            await VerifyPersistentAsync(client, path, mode, cancellationToken)
                .ConfigureAwait(false);
        }

        return "One-shot, persistent and recursive watch delivery, removal and stream completion verified.";
    }

    private static async Task VerifyOneShotAsync(
        ZooKeeperClient client,
        string path,
        CancellationToken cancellationToken
    )
    {
        var watched = await client
            .ExistsAndWatchAsync(path, cancellationToken)
            .ConfigureAwait(false);
        await using var watchLifetime = watched.Watch.ConfigureAwait(false);
        if (watched.Value is not null)
        {
            throw new InvalidOperationException("The sample path already exists.");
        }

        await client
            .CreateAsync(path, null, SampleNodes.EphemeralCreatorAcl(), cancellationToken)
            .ConfigureAwait(false);
        var events = 0;
        await foreach (
            var change in watched.Watch.ReadEventsAsync(cancellationToken).ConfigureAwait(false)
        )
        {
            if (change.Kind != ZooKeeperWatchEventKind.NodeCreated)
            {
                throw new InvalidOperationException("The watch returned an unexpected event.");
            }

            events++;
        }

        if (events != 1)
        {
            throw new InvalidOperationException("The one-shot watch did not deliver exactly once.");
        }

        await client.DeleteAsync(path, ZNodeVersion.Any, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyPersistentAsync(
        ZooKeeperClient client,
        string path,
        PersistentWatchMode mode,
        CancellationToken cancellationToken
    )
    {
        var persistent = await client
            .AddWatchAsync(path, mode, cancellationToken)
            .ConfigureAwait(false);
        var persistentEvents = persistent
            .ReadEventsAsync(cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        await using var persistentLifetime = persistentEvents.ConfigureAwait(false);
        await client
            .CreateAsync(path, null, SampleNodes.EphemeralCreatorAcl(), cancellationToken)
            .ConfigureAwait(false);
        await VerifyWatchEventAsync(persistentEvents, ZooKeeperWatchEventKind.NodeCreated)
            .ConfigureAwait(false);
        await client
            .SetDataAsync(path, "updated"u8.ToArray(), ZNodeVersion.Any, cancellationToken)
            .ConfigureAwait(false);
        await VerifyWatchEventAsync(persistentEvents, ZooKeeperWatchEventKind.NodeDataChanged)
            .ConfigureAwait(false);
        await persistent
            .RemoveAsync(WatchRemovalMode.ServerConfirmed, cancellationToken)
            .ConfigureAwait(false);
        if (await persistentEvents.MoveNextAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException("The removed persistent stream did not complete.");
        }

        await client.DeleteAsync(path, ZNodeVersion.Any, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyWatchEventAsync(
        IAsyncEnumerator<ZooKeeperWatchEvent> events,
        ZooKeeperWatchEventKind expected
    )
    {
        if (!await events.MoveNextAsync().ConfigureAwait(false) || events.Current.Kind != expected)
        {
            throw new InvalidOperationException(
                "The persistent watch returned an unexpected event."
            );
        }
    }
}
