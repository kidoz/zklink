namespace ZkLink;

/// <summary>A read snapshot and the one-shot watch established by the same server request.</summary>
public sealed class WatchedResult<T>
{
    internal WatchedResult(T value, ZooKeeperWatch watch)
    {
        Value = value;
        Watch = watch;
    }

    /// <summary>Gets the observed value; a missing existence read has a null value and an active watch.</summary>
    public T Value { get; }

    /// <summary>Gets the owned watch, which may already have a buffered event.</summary>
    public ZooKeeperWatch Watch { get; }
}
