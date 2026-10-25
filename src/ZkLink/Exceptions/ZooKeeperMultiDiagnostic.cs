namespace ZkLink;

/// <summary>A member diagnostic; apparent success inside a failed multi does not imply commitment.</summary>
public sealed class ZooKeeperMultiDiagnostic
{
    /// <summary>Preserves member order, raw opcode/error and any returned path/stat.</summary>
    public ZooKeeperMultiDiagnostic(
        int index,
        int opcode,
        int errorCode,
        string? path = null,
        ZNodeStat? stat = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        Index = index;
        Opcode = opcode;
        ErrorCode = errorCode;
        Path = path;
        Stat = stat;
    }

    /// <summary>Gets the zero-based input index.</summary>
    public int Index { get; }

    /// <summary>Gets the raw member opcode.</summary>
    public int Opcode { get; }

    /// <summary>Gets the raw member error, including zero for success-looking diagnostics.</summary>
    public int ErrorCode { get; }

    /// <summary>Gets a recognized error, otherwise null.</summary>
    public ZooKeeperErrorCode? KnownErrorCode =>
        Enum.IsDefined((ZooKeeperErrorCode)ErrorCode) ? (ZooKeeperErrorCode)ErrorCode : null;

    /// <summary>Gets an optional returned client-relative path.</summary>
    public string? Path { get; }

    /// <summary>Gets an optional returned stat.</summary>
    public ZNodeStat? Stat { get; }
}
