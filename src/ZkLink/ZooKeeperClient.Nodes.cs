using System.Collections;
using ZkLink.Protocol;

namespace ZkLink;

public sealed partial class ZooKeeperClient
{
    /// <summary>Reads stat without a watch, returning null only when the node does not exist.</summary>
    public Task<ZNodeStat?> ExistsAsync(
        string path,
        CancellationToken cancellationToken = default
    ) =>
        ReadExistsAsync(
            SubmitNode(
                new ReadRequest(ZooKeeperOperation.Exists, path, false),
                ZooKeeperOperation.Exists,
                path,
                cancellationToken
            )
        );

    /// <summary>Reads an owned nullable data snapshot and stat without a watch.</summary>
    public Task<ZNodeData> GetDataAsync(
        string path,
        CancellationToken cancellationToken = default
    ) =>
        ReadDataAsync(
            SubmitNode(
                new ReadRequest(ZooKeeperOperation.GetData, path, false),
                ZooKeeperOperation.GetData,
                path,
                cancellationToken
            )
        );

    /// <summary>Reads child names and parent stat without a watch; child order is unspecified.</summary>
    public Task<ZNodeChildren> GetChildrenAsync(
        string path,
        CancellationToken cancellationToken = default
    ) =>
        ReadChildrenAsync(
            SubmitNode(
                new ReadRequest(ZooKeeperOperation.GetChildren, path, false),
                ZooKeeperOperation.GetChildren,
                path,
                cancellationToken
            )
        );

    /// <summary>Creates one node with explicit ACLs and returns the actual client-relative path and stat.</summary>
    /// <remarks>Parents are not created implicitly. TTL modes require EnableTtlNodes and server extended node types.</remarks>
    public Task<CreateResult> CreateAsync(
        string path,
        ReadOnlyMemory<byte>? data,
        CreateOptions options,
        CancellationToken cancellationToken = default
    )
    {
        var request = CreateNodeRequest(path, data, options);
        ValidateCreateFeatures(request);
        return ReadCreateAsync(
            SubmitNode(request, (ZooKeeperOperation)request.Opcode, path, cancellationToken)
        );
    }

    internal static CreateRequest CreateNodeRequest(
        string path,
        ReadOnlyMemory<byte>? data,
        CreateOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        var operation = options.Mode switch
        {
            CreateMode.Container => ZooKeeperOperation.CreateContainer,
            CreateMode.PersistentWithTtl or CreateMode.PersistentSequentialWithTtl =>
                ZooKeeperOperation.CreateTtl,
            _ => ZooKeeperOperation.CreateWithStat,
        };
        return new CreateRequest(
            operation,
            path,
            data,
            new PublicAclView(options.Acl),
            (int)options.Mode,
            options.Ttl?.Ticks / TimeSpan.TicksPerMillisecond
        );
    }

    private WireRequest ValidateCreateFeatures(WireRequest request)
    {
        if (request is CreateRequest { Ttl: not null } && !_enableTtlNodes)
        {
            throw new NotSupportedException(
                "TTL creation requires EnableTtlNodes and server extended node types on every endpoint."
            );
        }
        return request;
    }

    /// <summary>Writes an owned snapshot of data under a data-version condition and returns the updated stat.</summary>
    public Task<ZNodeStat> SetDataAsync(
        string path,
        ReadOnlyMemory<byte>? data,
        ZNodeVersion version,
        CancellationToken cancellationToken = default
    ) =>
        ReadStatAsync(
            SubmitNode(
                new SetDataRequest(path, data, version.Value),
                ZooKeeperOperation.SetData,
                path,
                cancellationToken
            )
        );

    /// <summary>Deletes one node under a data-version condition; nonempty nodes are rejected by the server.</summary>
    public Task DeleteAsync(
        string path,
        ZNodeVersion version,
        CancellationToken cancellationToken = default
    ) =>
        SyncCoreAsync(
            SubmitNode(
                new VersionRequest(ZooKeeperOperation.Delete, path, version.Value),
                ZooKeeperOperation.Delete,
                path,
                cancellationToken
            )
        );

    /// <summary>Reads immutable ACL entries and the corresponding stat, including ACL version.</summary>
    public Task<ZNodeAcl> GetAclAsync(string path, CancellationToken cancellationToken = default) =>
        ReadAclAsync(
            SubmitNode(
                new PathRequest(ZooKeeperOperation.GetAcl, path),
                ZooKeeperOperation.GetAcl,
                path,
                cancellationToken
            )
        );

    /// <summary>Replaces ACLs under an ACL-version condition and returns the updated stat.</summary>
    public Task<ZNodeStat> SetAclAsync(
        string path,
        IReadOnlyList<ZooKeeperAcl> acl,
        ZNodeVersion aclVersion,
        CancellationToken cancellationToken = default
    ) =>
        ReadStatAsync(
            SubmitNode(
                new SetAclRequest(path, new PublicAclView(acl), aclVersion.Value),
                ZooKeeperOperation.SetAcl,
                path,
                cancellationToken
            )
        );

    private Task<WireReply> SubmitNode(
        WireRequest request,
        ZooKeeperOperation operation,
        string path,
        CancellationToken cancellationToken
    ) =>
        _session.Submit(
            request,
            new ZooKeeperRequestInfo(operation, path: path),
            cancellationToken
        );

    private static async Task<ZNodeStat?> ReadExistsAsync(Task<WireReply> task)
    {
        try
        {
            return ToPublic(((StatResult)(await task.ConfigureAwait(false)).Result).Stat);
        }
        catch (ZooKeeperException error) when (error.ErrorCode == (int)ZooKeeperErrorCode.NoNode)
        {
            return null;
        }
    }

    private static async Task<ZNodeStat> ReadStatAsync(Task<WireReply> task) =>
        ToPublic(((StatResult)(await task.ConfigureAwait(false)).Result).Stat);

    private static async Task<ZNodeData> ReadDataAsync(Task<WireReply> task)
    {
        var result = (DataResult)(await task.ConfigureAwait(false)).Result;
        return new ZNodeData(
            result.Data is null ? (ReadOnlyMemory<byte>?)null : result.Data,
            ToPublic(result.Stat)
        );
    }

    private static async Task<ZNodeChildren> ReadChildrenAsync(Task<WireReply> task)
    {
        var result = (StringsResult)(await task.ConfigureAwait(false)).Result;
        return new ZNodeChildren(result.Values, ToPublic(result.Stat!.Value));
    }

    private static async Task<CreateResult> ReadCreateAsync(Task<WireReply> task)
    {
        var result = (PathResult)(await task.ConfigureAwait(false)).Result;
        return new CreateResult(result.Path, ToPublic(result.Stat!.Value));
    }

    private static async Task<ZNodeAcl> ReadAclAsync(Task<WireReply> task)
    {
        var result = (AclResult)(await task.ConfigureAwait(false)).Result;
        return new ZNodeAcl(
            result
                .Acl.Select(entry => new ZooKeeperAcl(
                    (ZooKeeperPermissions)entry.Permissions,
                    entry.Scheme,
                    entry.Id
                ))
                .ToArray(),
            ToPublic(result.Stat)
        );
    }

    internal static ZNodeStat ToPublic(WireStat stat) =>
        new(
            stat.Czxid,
            stat.Mzxid,
            stat.Ctime,
            stat.Mtime,
            stat.Version,
            stat.Cversion,
            stat.Aversion,
            stat.EphemeralOwner,
            stat.DataLength,
            stat.NumChildren,
            stat.Pzxid
        );

    // Measurement visits caller ACLs without first allocating an entire wire list. The scheduler
    // reserves its byte budget, encodes synchronously, then retains independent reply metadata.
    private sealed class PublicAclView : IReadOnlyList<WireAcl>
    {
        private readonly IReadOnlyList<ZooKeeperAcl> _entries;

        internal PublicAclView(IReadOnlyList<ZooKeeperAcl> acl)
        {
            ArgumentNullException.ThrowIfNull(acl);
            if (acl.Count == 0)
                throw new ArgumentException(
                    "At least one explicit ACL entry is required.",
                    nameof(acl)
                );
            _entries = acl;
        }

        public int Count => _entries.Count;
        public WireAcl this[int index]
        {
            get
            {
                ZooKeeperAcl entry =
                    _entries[index] ?? throw new ArgumentException("ACL entries cannot be null.");
                return new WireAcl((int)entry.Permissions, entry.Scheme, entry.Id);
            }
        }

        public IEnumerator<WireAcl> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
                yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
