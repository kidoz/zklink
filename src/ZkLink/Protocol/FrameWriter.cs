using System.Buffers.Binary;

namespace ZkLink.Protocol;

// The transport serializes writes. The caller owns the complete frame until the write finishes.
internal sealed class FrameWriter(Stream stream, ProtocolLimits limits)
{
    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly ProtocolLimits _limits =
        limits ?? throw new ArgumentNullException(nameof(limits));

    internal static byte[] AllocateFrame(int payloadLength, ProtocolLimits limits)
    {
        if (payloadLength <= 0 || payloadLength > limits.MaxFrameBytes)
        {
            throw new InvalidDataException("Invalid or excessive frame length.");
        }
        var frame = new byte[checked(sizeof(int) + payloadLength)];
        BinaryPrimitives.WriteInt32BigEndian(frame, payloadLength);
        return frame;
    }

    internal Task WriteFrameAsync(
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken = default
    )
    {
        int payloadLength = frame.Length - sizeof(int);
        if (
            payloadLength <= 0
            || payloadLength > _limits.MaxFrameBytes
            || BinaryPrimitives.ReadInt32BigEndian(frame.Span) != payloadLength
        )
        {
            throw new InvalidDataException("Invalid or excessive frame length.");
        }
        return _stream.WriteAsync(frame, cancellationToken).AsTask();
    }
}
