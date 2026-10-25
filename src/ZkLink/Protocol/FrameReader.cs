using System.Buffers.Binary;

namespace ZkLink.Protocol;

// One serialized reader per transport. The stream remains owned by the transport.
internal sealed class FrameReader(Stream stream, ProtocolLimits limits)
{
    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly ProtocolLimits _limits =
        limits ?? throw new ArgumentNullException(nameof(limits));
    private readonly byte[] _prefix = new byte[sizeof(int)];

    internal async Task<byte[]?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var firstRead = await _stream.ReadAsync(_prefix, cancellationToken).ConfigureAwait(false);
        if (firstRead == 0)
        {
            return null;
        }
        await _stream
            .ReadExactlyAsync(_prefix.AsMemory(firstRead), cancellationToken)
            .ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(_prefix);
        if (length <= 0 || length > _limits.MaxFrameBytes)
        {
            throw new InvalidDataException("Invalid or excessive frame length.");
        }
        // Validate before allocating. Result ownership transfers to the decoder.
        var payload = new byte[length];
        await _stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }
}
