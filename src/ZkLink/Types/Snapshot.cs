using System.Collections.ObjectModel;
using ZkLink.Protocol;

namespace ZkLink;

internal static class Snapshot
{
    internal static ReadOnlyMemory<byte>? Data(ReadOnlyMemory<byte>? source)
    {
        if (source is not { } bytes)
        {
            return null;
        }
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            bytes.Length,
            ProtocolLimits.AbsoluteMaxFrameBytes
        );
        return new ReadOnlyMemory<byte>(bytes.ToArray());
    }

    internal static ReadOnlyCollection<T> List<T>(IReadOnlyList<T> source)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        var count = source.Count;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            count,
            ProtocolLimits.AbsoluteMaxCollectionElements
        );
        var copy = new T[count];
        for (var i = 0; i < count; i++)
        {
            copy[i] =
                source[i]
                ?? throw new ArgumentException(
                    "Collection entries cannot be null.",
                    nameof(source)
                );
        }
        return Array.AsReadOnly(copy);
    }
}
