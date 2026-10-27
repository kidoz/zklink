using System.Buffers.Binary;
using ZkLink.Protocol;

namespace ZkLink.Benchmarks;

// MemoryStream deliberately completes synchronously: this measures framing/copying,
// not sockets, scheduling or transport backpressure. Buffers and framing objects are reused.
internal sealed class FrameWorkload : IDisposable
{
    private readonly byte[] _payload;
    private readonly MemoryStream _input;
    private readonly MemoryStream _output;
    private readonly FrameReader _reader;
    private readonly FrameWriter _writer;

    internal FrameWorkload(int payloadBytes)
    {
        _payload = new byte[payloadBytes];
        _payload.AsSpan().Fill(42);
        byte[] frame = new byte[sizeof(int) + payloadBytes];
        BinaryPrimitives.WriteInt32BigEndian(frame, payloadBytes);
        _payload.CopyTo(frame, sizeof(int));
        _input = new MemoryStream(frame, writable: false);
        _output = new MemoryStream(new byte[frame.Length], writable: true);
        var limits = new ProtocolLimits();
        _reader = new FrameReader(_input, limits);
        _writer = new FrameWriter(_output, limits);
    }

    internal Task<byte[]?> ReadAsync()
    {
        _input.Position = 0;
        return _reader.ReadAsync();
    }

    internal Task WriteAsync()
    {
        _output.Position = 0;
        return _writer.WriteAsync(_payload);
    }

    internal async Task ValidateAsync()
    {
        var read = await ReadAsync().ConfigureAwait(true);
        if (read is null || !read.AsSpan().SequenceEqual(_payload))
        {
            throw new InvalidDataException("Benchmark frame read mismatch.");
        }
        await WriteAsync().ConfigureAwait(true);
        if (
            _output.Position != _output.Length
            || !_output.ToArray().AsSpan().SequenceEqual(_input.ToArray())
        )
        {
            throw new InvalidDataException("Benchmark frame write mismatch.");
        }
    }

    public void Dispose()
    {
        _input.Dispose();
        _output.Dispose();
    }
}
