using ZkLink.Configuration;
using ZkLink.Protocol;

namespace ZkLink;

/// <summary>A closed unwatched multi-read descriptor.</summary>
public abstract class ZooKeeperReadOperation
{
    private ZooKeeperReadOperation(string path, ZooKeeperOperation operation)
    {
        ZNodePath.Validate(path);
        Path = path;
        Request = new ReadRequest(operation, path, false);
    }

    internal WireRequest Request { get; }

    /// <summary>Gets the client-relative path.</summary>
    public string Path { get; }

    /// <summary>Gets the encoded member operation.</summary>
    public ZooKeeperOperation Operation => (ZooKeeperOperation)Request.Opcode;

    /// <summary>Describes a data/stat read without a watch.</summary>
    public static ZooKeeperReadOperation GetData(string path) =>
        new Descriptor(path, ZooKeeperOperation.GetData);

    /// <summary>Describes a child-name read without a parent Stat or watch.</summary>
    public static ZooKeeperReadOperation GetChildren(string path) =>
        new Descriptor(path, ZooKeeperOperation.GetChildrenNames);

    private sealed class Descriptor(string path, ZooKeeperOperation operation)
        : ZooKeeperReadOperation(path, operation);
}
