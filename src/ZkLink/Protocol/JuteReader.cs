using System.Buffers.Binary;
using System.Text;

namespace ZkLink.Protocol;

// Borrows a single complete frame. Returned buffers are owned copies.
internal ref struct JuteReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ReadOnlySpan<byte> payload;
    private readonly ProtocolLimits limits;
    private int position;

    internal JuteReader(ReadOnlySpan<byte> payload, ProtocolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (payload.Length > limits.MaxFrameBytes)
            throw new InvalidDataException("Frame exceeds the configured limit.");
        this.payload = payload;
        this.limits = limits;
    }

    internal readonly int Remaining => payload.Length - position;

    internal int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Take(sizeof(int)));

    internal long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(Take(sizeof(long)));

    internal bool ReadBoolean() => Take(1)[0] != 0;

    internal string? ReadString()
    {
        int length = ReadLength();
        if (length == -1)
            return null;
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

    internal byte[]? ReadBuffer()
    {
        int length = ReadLength();
        return length == -1 ? null : Take(length).ToArray();
    }

    internal int ReadVectorCount(int minimumElementBytes, bool allowNull = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumElementBytes, 1);
        int count = ReadInt32();
        if (count == -1 && allowNull)
            return -1;
        if (
            count < 0
            || count > limits.MaxCollectionElements
            || count > Remaining / minimumElementBytes
        )
            throw new InvalidDataException("Invalid or excessive vector count.");
        return count;
    }

    internal string?[]? ReadStringVector(bool allowNull = false)
    {
        int count = ReadVectorCount(sizeof(int), allowNull);
        if (count == -1)
            return null;
        var values = new string?[count];
        for (int i = 0; i < values.Length; i++)
            values[i] = ReadString();
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
            throw new InvalidDataException("Unexpected trailing protocol fields.");
    }

    private int ReadLength()
    {
        int length = ReadInt32();
        if (length < -1 || length > Remaining)
            throw new InvalidDataException("Invalid or truncated field length.");
        return length;
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
            throw new InvalidDataException("Truncated protocol record.");
        ReadOnlySpan<byte> result = payload.Slice(position, count);
        position += count;
        return result;
    }
}
