namespace ZkLink.Configuration;

internal sealed class ChrootPath
{
    private readonly string? root;
    private readonly int rootUtf8Bytes;
    private readonly string? descendantPrefix;

    internal ChrootPath(string? root)
    {
        if (root is not null)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                root.Length,
                ConnectionStringParser.MaxConnectionStringCharacters
            );
            ZNodePath.Validate(root);
        }
        this.root = root == "/" ? null : root;
        rootUtf8Bytes = this.root is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(this.root);
        descendantPrefix = this.root is null ? null : this.root + "/";
    }

    internal int AddedBytes(string path, bool sequential = false) =>
        root is null ? 0 : rootUtf8Bytes - (path == "/" && !sequential ? 1 : 0);

    internal string ToServer(string path, bool sequential = false)
    {
        ZNodePath.Validate(path, sequential);
        if (root is null)
            return path;
        // A sequential '/' is a prefix for a child, rather than the chroot node itself.
        return path == "/" && !sequential ? root : string.Concat(root, path);
    }

    internal bool Contains(string serverPath) =>
        root is null
        || string.Equals(serverPath, root, StringComparison.Ordinal)
        || serverPath.StartsWith(descendantPrefix!, StringComparison.Ordinal);

    internal string ToClient(string serverPath)
    {
        ZNodePath.Validate(serverPath);
        if (!Contains(serverPath))
            throw new InvalidDataException("Server path is outside the configured namespace.");
        return root is null ? serverPath
            : serverPath.Length == root.Length ? "/"
            : serverPath[root.Length..];
    }
}
