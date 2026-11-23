using System.Buffers.Binary;
using System.Security.Cryptography;

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
        if (length <= 0)
        {
            throw new InvalidDataException("Invalid frame length.");
        }
        // A well-formed frame can exceed the local capacity, for example a large
        // child list or multi reply. That is transport loss, not invalid protocol.
        if (length > _limits.MaxFrameBytes)
        {
            throw new FrameLimitException(length, _limits.MaxFrameBytes);
        }
        // Validate before allocating. Result ownership transfers to the decoder.
        var payload = new byte[length];
        try
        {
            await _stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            return payload;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(payload);
            throw;
        }
    }
}
