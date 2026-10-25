using System.Buffers.Binary;
using System.Text;

namespace ZkLink.Protocol;

// Writes into caller-owned storage; no growth or hidden pooling.
internal ref struct JuteWriter : IJuteOutput
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Span<byte> destination;
    private readonly ProtocolLimits limits;
    private int position;

    internal JuteWriter(Span<byte> destination, ProtocolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        this.destination = destination[..Math.Min(destination.Length, limits.MaxFrameBytes)];
        this.limits = limits;
    }

    internal readonly int WrittenCount => position;

    public void WriteInt32(int value) =>
        BinaryPrimitives.WriteInt32BigEndian(Take(sizeof(int)), value);

    public void WriteInt64(long value) =>
        BinaryPrimitives.WriteInt64BigEndian(Take(sizeof(long)), value);

    public void WriteBoolean(bool value) => Take(1)[0] = value ? (byte)1 : (byte)0;

    public void WriteString(string? value)
    {
        if (value is null)
        {
            WriteInt32(-1);
            return;
        }
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw new InvalidDataException("Invalid UTF-16 in protocol string.");
        }
        EnsureCapacity(checked(sizeof(int) + byteCount));
        WriteInt32(byteCount);
        StrictUtf8.GetBytes(value, Take(byteCount));
    }

    public void WriteBuffer(ReadOnlySpan<byte> value)
    {
        EnsureCapacity(checked(sizeof(int) + value.Length));
        WriteInt32(value.Length);
        value.CopyTo(Take(value.Length));
    }

    public void WriteNullBuffer() => WriteInt32(-1);

    public void WriteStringVector(IReadOnlyList<string?>? values, bool allowNull = false)
    {
        if (values is null)
        {
            if (!allowNull)
                throw new ArgumentNullException(nameof(values));
            WriteInt32(-1);
            return;
        }
        if (values.Count > limits.MaxCollectionElements)
            throw new InvalidDataException("Vector exceeds the configured limit.");
        // Every string requires at least its length prefix.
        EnsureCapacity(checked(sizeof(int) + checked(values.Count * sizeof(int))));
        WriteInt32(values.Count);
        foreach (string? value in values)
            WriteString(value);
    }

    public void WriteStat(WireStat stat)
    {
        EnsureCapacity(WireStat.EncodedLength);
        WriteInt64(stat.Czxid);
        WriteInt64(stat.Mzxid);
        WriteInt64(stat.Ctime);
        WriteInt64(stat.Mtime);
        WriteInt32(stat.Version);
        WriteInt32(stat.Cversion);
        WriteInt32(stat.Aversion);
        WriteInt64(stat.EphemeralOwner);
        WriteInt32(stat.DataLength);
        WriteInt32(stat.NumChildren);
        WriteInt64(stat.Pzxid);
    }

    internal void WriteRequestHeader(RequestHeader header)
    {
        WriteInt32(header.Xid);
        WriteInt32(header.Opcode);
    }

    internal void WriteReplyHeader(ReplyHeader header)
    {
        WriteInt32(header.Xid);
        WriteInt64(header.Zxid);
        WriteInt32(header.Error);
    }

    private readonly void EnsureCapacity(int count)
    {
        if (count < 0 || count > destination.Length - position)
            throw new InvalidDataException(
                "Encoded record exceeds the destination or frame limit."
            );
    }

    private Span<byte> Take(int count)
    {
        EnsureCapacity(count);
        Span<byte> result = destination.Slice(position, count);
        position += count;
        return result;
    }
}
