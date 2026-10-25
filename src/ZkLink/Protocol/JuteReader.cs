using System.Buffers.Binary;
using System.Text;

namespace ZkLink.Protocol;

// Borrows a single complete frame. Returned buffers are owned copies.
internal ref struct JuteReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ReadOnlySpan<byte> _payload;
    private readonly ProtocolLimits _limits;
    private int _position;

    internal JuteReader(ReadOnlySpan<byte> payload, ProtocolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (payload.Length > limits.MaxFrameBytes)
        {
            throw new InvalidDataException("Frame exceeds the configured limit.");
        }
        _payload = payload;
        _limits = limits;
    }

    internal readonly int Remaining => _payload.Length - _position;

    internal int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Take(sizeof(int)));

    internal long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(Take(sizeof(long)));

    internal bool ReadBoolean() => Take(1)[0] != 0;

    internal string? ReadString()
    {
        var length = ReadLength();
        if (length == -1)
        {
            return null;
        }
        try
        {
            return StrictUtf8.GetString(Take(length));
        }
        catch (DecoderFallbackException)
        {
            // Never retain packet contents in exception diagnostics.
            throw new InvalidDataException("Malformed UTF-8 in protocol string.");
        }
    }

    internal byte[]? ReadBuffer(int? maxBytes = null)
    {
        var length = ReadLength();
        if (maxBytes is { } maximum && length > maximum)
        {
            throw new InvalidDataException("Buffer exceeds the configured bound.");
        }
        return length == -1 ? null : Take(length).ToArray();
    }

    internal int ReadVectorCount(int minimumElementBytes, bool allowNull = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumElementBytes, 1);
        var count = ReadInt32();
        if (count == -1 && allowNull)
        {
            return -1;
        }
        if (
            count < 0
            || count > _limits.MaxCollectionElements
            || count > Remaining / minimumElementBytes
        )
        {
            throw new InvalidDataException("Invalid or excessive vector count.");
        }
        return count;
    }

    internal string?[]? ReadStringVector(bool allowNull = false)
    {
        var count = ReadVectorCount(sizeof(int), allowNull);
        if (count == -1)
        {
            return null;
        }
        var values = new string?[count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = ReadString();
        }
        return values;
    }

    internal WireStat ReadStat() =>
        new(
            ReadInt64(),
            ReadInt64(),
            ReadInt64(),
            ReadInt64(),
            ReadInt32(),
            ReadInt32(),
            ReadInt32(),
            ReadInt64(),
            ReadInt32(),
            ReadInt32(),
            ReadInt64()
        );

    internal RequestHeader ReadRequestHeader() => new(ReadInt32(), ReadInt32());

    internal ReplyHeader ReadReplyHeader() => new(ReadInt32(), ReadInt64(), ReadInt32());

    internal readonly void EnsureEnd()
    {
        if (Remaining != 0)
        {
            throw new InvalidDataException("Unexpected trailing protocol fields.");
        }
    }

    private int ReadLength()
    {
        var length = ReadInt32();
        if (length < -1 || length > Remaining)
        {
            throw new InvalidDataException("Invalid or truncated field length.");
        }
        return length;
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new InvalidDataException("Truncated protocol record.");
        }
        var result = _payload.Slice(_position, count);
        _position += count;
        return result;
    }
}
