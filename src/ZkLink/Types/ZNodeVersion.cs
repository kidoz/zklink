namespace ZkLink;

/// <summary>A raw signed version condition; -1 means any version on the wire.</summary>
public readonly record struct ZNodeVersion
{
    private ZNodeVersion(int value) => Value = value;

    /// <summary>Gets the raw version value. The default value is exact zero.</summary>
    public int Value { get; }

    /// <summary>Gets the wildcard condition, encoded as -1.</summary>
    public static ZNodeVersion Any => new(-1);

    /// <summary>Preserves any signed version, including overflow and the -1 wildcard.</summary>
    public static ZNodeVersion Exact(int value) => new(value);
}
