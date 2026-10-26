using ZkLink.Protocol;

namespace ZkLink;

public sealed partial class ZooKeeperClient
{
    /// <summary>Lists this session's ephemeral paths matching a textual prefix, restricted to the configured chroot.</summary>
    /// <remarks>The prefix need not identify an existing node. Result order is unspecified.</remarks>
    public Task<IReadOnlyList<string>> GetEphemeralsAsync(
        string prefixPath,
        CancellationToken cancellationToken = default
    ) =>
        ReadEphemeralsAsync(
            SubmitNode(
                new PathRequest(ZooKeeperOperation.GetEphemerals, prefixPath),
                ZooKeeperOperation.GetEphemerals,
                prefixPath,
                cancellationToken
            )
        );

    /// <summary>Counts all descendants, excluding the queried node, without registering a watch.</summary>
    public Task<int> GetAllChildrenNumberAsync(
        string path,
        CancellationToken cancellationToken = default
    ) =>
        ReadCountAsync(
            SubmitNode(
                new PathRequest(ZooKeeperOperation.GetAllChildrenNumber, path),
                ZooKeeperOperation.GetAllChildrenNumber,
                path,
                cancellationToken
            )
        );

    /// <summary>Reads an immutable snapshot of server-reported authentication identities.</summary>
    /// <remarks>Identity strings are sensitive and are not included in their default diagnostic formatting.</remarks>
    public Task<IReadOnlyList<ZooKeeperClientIdentity>> WhoAmIAsync(
        CancellationToken cancellationToken = default
    ) =>
        ReadIdentitiesAsync(
            _session.Submit(
                new EmptyRequest(ZooKeeperOperation.WhoAmI),
                new ZooKeeperRequestInfo(ZooKeeperOperation.WhoAmI),
                cancellationToken
            )
        );

    private static async Task<IReadOnlyList<string>> ReadEphemeralsAsync(Task<WireReply> task) =>
        Array.AsReadOnly(((StringsResult)(await task.ConfigureAwait(false)).Result).Values);

    private static async Task<int> ReadCountAsync(Task<WireReply> task) =>
        ((CountResult)(await task.ConfigureAwait(false)).Result).Count;

    private static async Task<IReadOnlyList<ZooKeeperClientIdentity>> ReadIdentitiesAsync(
        Task<WireReply> task
    ) => Array.AsReadOnly(((IdentityResult)(await task.ConfigureAwait(false)).Result).Identities);
}
