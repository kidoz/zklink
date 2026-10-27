using ZkLink;

namespace ZkLink.Samples.Basic;

/// <summary>Disposable authenticated nodes used by the demonstrations.</summary>
internal static class SampleNodes
{
    internal static string UniquePath(string prefix) => prefix + Guid.NewGuid().ToString("N");

    internal static CreateOptions EphemeralCreatorAcl() =>
        new([new ZooKeeperAcl(ZooKeeperPermissions.All, "auth", "")], CreateMode.Ephemeral);
}
