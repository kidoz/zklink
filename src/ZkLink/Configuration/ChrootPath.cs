namespace ZkLink.Configuration;

internal sealed class ChrootPath
{
    private readonly string? _root;
    private readonly int _rootUtf8Bytes;
    private readonly string? _descendantPrefix;

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
        _root = root == "/" ? null : root;
        _rootUtf8Bytes = _root is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(_root);
        _descendantPrefix = _root is null ? null : _root + "/";
    }

    internal int AddedBytes(string path, bool sequential = false) =>
        _root is null ? 0 : _rootUtf8Bytes - (path == "/" && !sequential ? 1 : 0);

    internal string ToServer(string path, bool sequential = false)
    {
        ZNodePath.Validate(path, sequential);
        if (_root is null)
        {
            // A sequential '/' is a prefix for a child, rather than the chroot node itself.
            return path;
        }
        return path == "/" && !sequential ? _root : string.Concat(_root, path);
    }

    internal bool Contains(string serverPath) =>
        _root is null
        || string.Equals(serverPath, _root, StringComparison.Ordinal)
        || serverPath.StartsWith(_descendantPrefix!, StringComparison.Ordinal);

    internal string ToClient(string serverPath)
    {
        ZNodePath.Validate(serverPath);
        if (!Contains(serverPath))
        {
            throw new InvalidDataException("Server path is outside the configured namespace.");
        }
        return _root is null ? serverPath
            : serverPath.Length == _root.Length ? "/"
            : serverPath[_root.Length..];
    }
}
