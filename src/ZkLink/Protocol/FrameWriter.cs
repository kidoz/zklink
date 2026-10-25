using System.Buffers.Binary;

namespace ZkLink.Protocol;

// The transport serializes calls and owns payload memory until completion.
internal sealed class FrameWriter(Stream stream, ProtocolLimits limits)
{
    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly ProtocolLimits _limits =
        limits ?? throw new ArgumentNullException(nameof(limits));
    private readonly byte[] _prefix = new byte[sizeof(int)];

    internal async Task WriteAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default
    )
    {
        if (payload.IsEmpty || payload.Length > _limits.MaxFrameBytes)
        {
            throw new InvalidDataException("Invalid or excessive frame length.");
        }
        BinaryPrimitives.WriteInt32BigEndian(_prefix, payload.Length);
        // Stream.WriteAsync completes the entire supplied memory or throws.
        await _stream.WriteAsync(_prefix, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }
}
