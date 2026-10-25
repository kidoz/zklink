using System.Buffers.Binary;

namespace ZkLink.Protocol;

// One serialized reader per transport. The stream remains owned by the transport.
internal sealed class FrameReader(Stream stream, ProtocolLimits limits)
{
    private readonly Stream stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly ProtocolLimits limits =
        limits ?? throw new ArgumentNullException(nameof(limits));
    private readonly byte[] prefix = new byte[sizeof(int)];

    internal async Task<byte[]?> ReadAsync(CancellationToken cancellationToken = default)
    {
        int firstRead = await stream.ReadAsync(prefix, cancellationToken).ConfigureAwait(false);
        if (firstRead == 0)
            return null;
        await stream
            .ReadExactlyAsync(prefix.AsMemory(firstRead), cancellationToken)
            .ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length <= 0 || length > limits.MaxFrameBytes)
            throw new InvalidDataException("Invalid or excessive frame length.");
        // Validate before allocating. Result ownership transfers to the decoder.
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }
}
