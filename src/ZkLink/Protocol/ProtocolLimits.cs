namespace ZkLink.Protocol;

// Internal safety ceilings, not negotiated server limits.
internal sealed class ProtocolLimits
{
    internal const int DefaultMaxFrameBytes = 1_048_575;
    internal const int AbsoluteMaxFrameBytes = 64 * 1024 * 1024;
    internal const int AbsoluteMaxCollectionElements = 1_000_000;

    internal ProtocolLimits(
        int maxFrameBytes = DefaultMaxFrameBytes,
        int maxCollectionElements = 100_000
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxFrameBytes, AbsoluteMaxFrameBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCollectionElements, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maxCollectionElements,
            AbsoluteMaxCollectionElements
        );
        MaxFrameBytes = maxFrameBytes;
        MaxCollectionElements = maxCollectionElements;
    }

    internal int MaxFrameBytes { get; }
    internal int MaxCollectionElements { get; }
}
