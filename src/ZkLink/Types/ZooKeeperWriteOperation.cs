using ZkLink.Configuration;
using ZkLink.Protocol;

namespace ZkLink;

/// <summary>A closed write descriptor. Data memory must remain stable until MultiAsync returns its Task.</summary>
public abstract class ZooKeeperWriteOperation
{
    private ZooKeeperWriteOperation(string path, WireRequest request)
    {
        ZNodePath.Validate(path, request is CreateRequest { Flags: 2 or 3 or 6 });
        Path = path;
        Request = request;
    }

    internal WireRequest Request { get; }

    /// <summary>Gets the client-relative path.</summary>
    public string Path { get; }

    /// <summary>Gets the encoded member operation.</summary>
    public ZooKeeperOperation Operation => (ZooKeeperOperation)Request.Opcode;

    /// <summary>Describes a create with explicit ACLs; no payload copy or network admission occurs here.</summary>
    /// <remarks>TTL descriptors require EnableTtlNodes on the client that submits the transaction.</remarks>
    public static ZooKeeperWriteOperation Create(
        string path,
        ReadOnlyMemory<byte>? data,
        CreateOptions options
    ) => new Descriptor(path, ZooKeeperClient.CreateNodeRequest(path, data, options));

    /// <summary>Describes a data-version-conditional update without copying the supplied memory.</summary>
    public static ZooKeeperWriteOperation SetData(
        string path,
        ReadOnlyMemory<byte>? data,
        ZNodeVersion version
    ) => new Descriptor(path, new SetDataRequest(path, data, version.Value));

    /// <summary>Describes a data-version-conditional delete.</summary>
    public static ZooKeeperWriteOperation Delete(string path, ZNodeVersion version) =>
        new Descriptor(path, new VersionRequest(ZooKeeperOperation.Delete, path, version.Value));

    /// <summary>Describes a data-version check within the atomic transaction.</summary>
    public static ZooKeeperWriteOperation Check(string path, ZNodeVersion version) =>
        new Descriptor(path, new VersionRequest(ZooKeeperOperation.Check, path, version.Value));

    private sealed class Descriptor(string path, WireRequest request)
        : ZooKeeperWriteOperation(path, request);
}
