namespace ZkLink.Configuration;

// Match PathUtils code-unit rules without normalizing or echoing caller paths.
internal static class ZNodePath
{
    internal static void ValidateName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || name.Contains('/'))
        {
            throw new ArgumentException("Invalid child name.");
        }
        ValidateComponent(name);
        foreach (var c in name)
        {
            if (IsForbidden(c))
            {
                throw new ArgumentException("Forbidden child-name code unit.");
            }
        }
    }

    internal static void Validate(string path, bool sequential = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path[0] != '/')
        {
            throw new ArgumentException("A znode path must be absolute.", nameof(path));
        }
        if (!sequential && path.Length > 1 && path[^1] == '/')
        {
            throw new ArgumentException("A znode path cannot end with a slash.", nameof(path));
        }
        var componentStart = 1;
        for (var i = 1; i < path.Length; i++)
        {
            var c = path[i];
            if (IsForbidden(c))
            {
                throw new ArgumentException(
                    "A znode path contains a forbidden code unit.",
                    nameof(path)
                );
            }
            if (c == '/')
            {
                if (i == componentStart)
                {
                    throw new ArgumentException(
                        "A znode path cannot contain empty components.",
                        nameof(path)
                    );
                }
                ValidateComponent(path.AsSpan(componentStart, i - componentStart));
                componentStart = i + 1;
            }
        }
        // Upstream appends a sequence suffix before validation, including to '.' and '..' prefixes.
        if (!sequential)
        {
            ValidateComponent(path.AsSpan(componentStart));
        }
    }

    private static void ValidateComponent(ReadOnlySpan<char> component)
    {
        if (component.SequenceEqual(".") || component.SequenceEqual(".."))
        {
            throw new ArgumentException("A znode path cannot contain relative components.");
        }
    }

    private static bool IsForbidden(char c) =>
        c <= '\u001f'
        || c is >= '\u007f' and <= '\u009f' or >= '\ud800' and <= '\uf8ff' or >= '\ufff0';
}
