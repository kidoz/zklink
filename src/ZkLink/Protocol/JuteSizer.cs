using System.Text;
using ZkLink.Configuration;

namespace ZkLink.Protocol;

// Uses the same protocol visitor as the writer, without allocating encoded storage.
internal struct JuteSizer(ProtocolLimits limits, ChrootPath? paths = null) : IJuteOutput
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal int WrittenCount { get; private set; }

    public void WriteInt32(int value) => Add(4);

    public void WriteInt64(long value) => Add(8);

    public void WriteBoolean(bool value) => Add(1);

    public void WriteNullBuffer() => Add(4);

    public void WriteStat(WireStat stat) => Add(WireStat.EncodedLength);

    public void WriteBuffer(ReadOnlySpan<byte> value) => Add(checked(4 + value.Length));

    public void WriteString(string? value)
    {
        if (value is null)
        {
            Add(4);
            return;
        }
        if (value.Length > limits.MaxFrameBytes - WrittenCount)
        {
            throw new InvalidDataException("Encoded string exceeds frame bounds.");
        }
        try
        {
            Add(checked(4 + StrictUtf8.GetByteCount(value)));
        }
        catch (EncoderFallbackException)
        {
            throw new InvalidDataException("Invalid UTF-16 in protocol string.");
        }
    }

    public void WritePath(string path, bool sequential)
    {
        if (paths is not null)
        {
            Add(paths.AddedBytes(path, sequential));
        }
        WriteString(path);
    }

    private void Add(int length)
    {
        if (length < 0 || length > limits.MaxFrameBytes - WrittenCount)
        {
            throw new InvalidDataException("Encoded record exceeds frame bounds.");
        }
        WrittenCount += length;
    }
}
