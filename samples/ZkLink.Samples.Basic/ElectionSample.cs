using ZkLink;

namespace ZkLink.Samples.Basic;

internal sealed class ElectionSample(ZooKeeperClientOptions options) : ISampleStep
{
    public async Task<string> RunAsync(ZooKeeperClient client, CancellationToken cancellationToken)
    {
        var root = SampleNodes.UniquePath("/zklink-election-sample-");
        var participants = new List<ZooKeeperClient>(3);
        try
        {
            await client
                .CreateAsync(
                    root,
                    null,
                    new CreateOptions([new(ZooKeeperPermissions.All, "auth", "")]),
                    cancellationToken
                )
                .ConfigureAwait(false);
            var candidates = new List<ElectionCandidate>(3);
            for (var index = 0; index < 3; index++)
            {
                var participant = await ZooKeeperClient
                    .ConnectAsync(options, cancellationToken)
                    .ConfigureAwait(false);
                participants.Add(participant);
                candidates.Add(
                    await ElectionCandidate
                        .JoinAsync(participant, root, cancellationToken)
                        .ConfigureAwait(false)
                );
            }

            if (
                participants
                    .Select(static participant => participant.Session.SessionId)
                    .Distinct()
                    .Count() != 3
            )
            {
                throw new InvalidOperationException(
                    "Election participants must have separate sessions."
                );
            }

            await candidates[0].WaitForLeadershipAsync(cancellationToken).ConfigureAwait(false);
            using var waits = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var second = candidates[1].WaitForLeadershipAsync(waits.Token);
            var third = candidates[2].WaitForLeadershipAsync(waits.Token);
            try
            {
                if (second.IsCompleted || third.IsCompleted)
                {
                    throw new InvalidOperationException(
                        "A follower completed before its predecessor left."
                    );
                }
                await participants[0].CloseAsync(cancellationToken).ConfigureAwait(false);
                await second.ConfigureAwait(false);
                if (third.IsCompleted)
                {
                    throw new InvalidOperationException(
                        "The third participant overtook the second."
                    );
                }
                await participants[1].CloseAsync(cancellationToken).ConfigureAwait(false);
                await third.ConfigureAwait(false);
                await participants[2].CloseAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await waits.CancelAsync().ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(second, third).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (waits.IsCancellationRequested) { }
            }

            // The participants closed different sessions; synchronize this observer.
            await client.SyncAsync(root, cancellationToken).ConfigureAwait(false);
            var remaining = await client
                .GetChildrenAsync(root, cancellationToken)
                .ConfigureAwait(false);
            if (remaining.Children.Count != 0)
            {
                throw new InvalidOperationException(
                    "Closed participants left candidate nodes behind."
                );
            }
        }
        finally
        {
            foreach (var participant in participants)
            {
                await participant.DisposeAsync().ConfigureAwait(false);
            }
            // Cancellation and ambiguous creates still need cleanup. This private parent is
            // owned only by this run, so remove any candidate whose create reply was lost.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.SyncAsync(root, cleanup.Token).ConfigureAwait(false);
            if (await client.ExistsAsync(root, cleanup.Token).ConfigureAwait(false) is not null)
            {
                var remaining = await client
                    .GetChildrenAsync(root, cleanup.Token)
                    .ConfigureAwait(false);
                foreach (var child in remaining.Children)
                {
                    try
                    {
                        await client
                            .DeleteAsync(root + "/" + child, ZNodeVersion.Any, cleanup.Token)
                            .ConfigureAwait(false);
                    }
                    catch (ZooKeeperException error)
                        when (error.KnownErrorCode == ZooKeeperErrorCode.NoNode) { }
                }
                await client
                    .DeleteAsync(root, ZNodeVersion.Any, cleanup.Token)
                    .ConfigureAwait(false);
            }
        }
        return "Leader election: three separate sessions, predecessor watches, ordered handoff and ephemeral cleanup verified.";
    }
}
