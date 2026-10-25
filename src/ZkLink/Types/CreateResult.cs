namespace ZkLink;

/// <summary>The actual client-relative created path and its stat.</summary>
public sealed class CreateResult
{
    /// <summary>Creates a result without interpreting the server sequence suffix.</summary>
    public CreateResult(string path, ZNodeStat stat)
    {
        ArgumentNullException.ThrowIfNull(path);
        Path = path;
        Stat = stat;
    }

    /// <summary>Gets the created path with the server suffix preserved.</summary>
    public string Path { get; }

    /// <summary>Gets the created node stat.</summary>
    public ZNodeStat Stat { get; }
}
