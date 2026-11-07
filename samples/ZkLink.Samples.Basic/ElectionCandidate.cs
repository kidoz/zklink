using ZkLink;

namespace ZkLink.Samples.Basic;

/// <summary>A bounded teaching example; a successful observation is not a leadership lease.</summary>
internal sealed class ElectionCandidate
{
    private const string Prefix = "candidate-";
    private readonly ZooKeeperClient _client;
    private readonly string _root;
    private readonly long _generation;
    private readonly CreateResult _created;

    private ElectionCandidate(
        ZooKeeperClient client,
        string root,
        CreateResult created,
        long generation
    )
    {
        _client = client;
        _root = root;
        _created = created;
        _generation = generation;
    }

    internal string Path => _created.Path;

    internal static async Task<ElectionCandidate> JoinAsync(
        ZooKeeperClient client,
        string root,
        CancellationToken cancellationToken
    )
    {
        var generation = client.Session.ConnectionGeneration;
        var created = await client
            .CreateAsync(
                root + "/" + Prefix,
                null,
                new CreateOptions(
                    [new(ZooKeeperPermissions.All, "auth", "")],
                    CreateMode.EphemeralSequential
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        // Never retry this create: an Unknown result might already have created a node.
        var candidate = new ElectionCandidate(client, root, created, generation);
        candidate.RequireOriginalConnection();
        return candidate;
    }

    internal async Task<string?> GetPredecessorAsync(CancellationToken cancellationToken)
    {
        RequireOriginalConnection();
        var children = await _client
            .GetChildrenAsync(_root, cancellationToken)
            .ConfigureAwait(false);
        // A private, short-lived parent contains only our fixed-prefix candidates. Abort
        // before signed sequence rollover; ordinal order then matches sequence order.
        if (
            children.Children.Any(static name =>
                name.Length != Prefix.Length + 10
                || !name.StartsWith(Prefix, StringComparison.Ordinal)
                || name.AsSpan(Prefix.Length).ContainsAnyExceptInRange('0', '9')
            )
        )
        {
            throw new InvalidOperationException(
                "The election contains an unsupported candidate name."
            );
        }

        var ordered = children.Children.Order(StringComparer.Ordinal).ToArray();
        var ownName = Path[(_root.Length + 1)..];
        var index = Array.IndexOf(ordered, ownName);
        if (index < 0)
        {
            throw new InvalidOperationException("The participant's candidate node is missing.");
        }

        var own = await _client.GetDataAsync(Path, cancellationToken).ConfigureAwait(false);
        RequireOriginalConnection();
        if (
            own.Stat.EphemeralOwner != _client.Session.SessionId
            || own.Stat.Czxid != _created.Stat.Czxid
        )
        {
            throw new InvalidOperationException(
                "The participant no longer owns its original candidate."
            );
        }

        return index == 0 ? null : _root + "/" + ordered[index - 1];
    }

    internal async Task WaitForLeadershipAsync(CancellationToken cancellationToken)
    {
        while (
            await GetPredecessorAsync(cancellationToken).ConfigureAwait(false) is { } predecessor
        )
        {
            await WaitForPredecessorChangeAsync(predecessor, cancellationToken)
                .ConfigureAwait(false);
        }
        RequireOriginalConnection();
    }

    internal async Task WaitForPredecessorChangeAsync(
        string predecessor,
        CancellationToken cancellationToken
    )
    {
        RequireOriginalConnection();
        var watched = await _client
            .ExistsAndWatchAsync(predecessor, cancellationToken)
            .ConfigureAwait(false);
        await using var watchLifetime = watched.Watch.ConfigureAwait(false);
        RequireOriginalConnection();
        if (watched.Value is null)
        {
            // It disappeared between listing and watch registration. Dispose the missing-node
            // existence watch and relist immediately rather than waiting for a future creation.
            return;
        }

        await foreach (
            var change in watched.Watch.ReadEventsAsync(cancellationToken).ConfigureAwait(false)
        )
        {
            RequireOriginalConnection();
            // Every invalidation requires another list, even a data change. A one-shot
            // existence watch is consumed by data changes as well as deletion.
            if (
                change.Kind
                is ZooKeeperWatchEventKind.NodeDeleted
                    or ZooKeeperWatchEventKind.NodeDataChanged
            )
            {
                return;
            }
            throw new InvalidOperationException("The predecessor watch ended unexpectedly.");
        }
        throw new InvalidOperationException(
            "The predecessor watch completed without an invalidation."
        );
    }

    private void RequireOriginalConnection()
    {
        var session = _client.Session;
        if (
            session.State != ZooKeeperSessionState.Connected
            || session.ConnectionGeneration != _generation
        )
        {
            throw new InvalidOperationException(
                "The election requires its original connected session; abort this run."
            );
        }
    }
}
