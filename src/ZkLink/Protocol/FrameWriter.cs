using System.Buffers.Binary;

namespace ZkLink.Protocol;

// The transport serializes calls and owns payload memory until completion.
internal sealed class FrameWriter(Stream stream, ProtocolLimits limits)
{
    private readonly Stream stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly ProtocolLimits limits =
        limits ?? throw new ArgumentNullException(nameof(limits));
    private readonly byte[] prefix = new byte[sizeof(int)];

    internal async Task WriteAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default
    )
    {
        if (payload.IsEmpty || payload.Length > limits.MaxFrameBytes)
            throw new InvalidDataException("Invalid or excessive frame length.");
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        // Stream.WriteAsync completes the entire supplied memory or throws.
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }
}
