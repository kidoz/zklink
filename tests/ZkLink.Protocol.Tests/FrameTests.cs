using System.Buffers.Binary;
using NSubstitute;
using ZkLink.Protocol;

namespace ZkLink.Protocol.Tests;

public sealed class FrameTests
{
    private static readonly ProtocolLimits Limits = new();
    private static readonly byte[] Payload = Convert.FromHexString(
        "000000017fffffffffffffff00000000"
    );

    private static byte[] Frame(byte[] payload)
    {
        var bytes = new byte[payload.Length + sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, payload.Length);
        payload.CopyTo(bytes, sizeof(int));
        return bytes;
    }

    [Fact]
    [Trait("Acceptance", "T02")]
    public async Task EveryPrefixHeaderAndBodySplitProducesIdenticalFrames()
    {
        var frame = Frame(Payload);
        for (var split = 1; split < frame.Length; split++)
        {
            using var stream = new FragmentedStream(frame, split);
            var reader = new FrameReader(stream, Limits);
            Assert.Equal(Payload, await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    [Trait("Acceptance", "T02")]
    public async Task ConcatenatedAndBytewiseFramesStaySeparateAndOwned()
    {
        var frame = Frame(Payload);
        using var stream = new FragmentedStream([.. frame, .. frame], maxChunk: 1);
        var reader = new FrameReader(stream, Limits);
        var first = Assert.IsType<byte[]>(
            await reader.ReadAsync(TestContext.Current.CancellationToken)
        );
        var second = Assert.IsType<byte[]>(
            await reader.ReadAsync(TestContext.Current.CancellationToken)
        );
        first[0] = 255;
        Assert.Equal(Payload, second);
        Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Acceptance", "T03")]
    public async Task EveryMidFrameEofFails()
    {
        var frame = Frame(Payload);
        for (var length = 1; length < frame.Length; length++)
        {
            using var stream = new FragmentedStream(frame[..length], maxChunk: 1);
            var reader = new FrameReader(stream, Limits);
            await Assert.ThrowsAsync<EndOfStreamException>(() =>
                reader.ReadAsync(TestContext.Current.CancellationToken)
            );
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    [InlineData(ProtocolLimits.DefaultMaxFrameBytes + 1)]
    [Trait("Acceptance", "T03")]
    public async Task InvalidFrameLengthDoesNotReadBody(int length)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(bytes, length);
        using var stream = new MemoryStream(bytes);
        var reader = new FrameReader(stream, Limits);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    [Trait("Acceptance", "T02")]
    public async Task WriterEmitsLengthExcludingPrefix()
    {
        using var stream = new MemoryStream();
        var writer = new FrameWriter(stream, new ProtocolLimits(maxFrameBytes: Payload.Length));
        await writer.WriteAsync(Payload, TestContext.Current.CancellationToken);
        Assert.Equal(Frame(Payload), stream.ToArray());
    }

    [Fact]
    [Trait("Acceptance", "T03")]
    public async Task WriterRejectsOversizeBeforeWritingPrefix()
    {
        using var stream = new MemoryStream();
        var writer = new FrameWriter(stream, new ProtocolLimits(maxFrameBytes: 1));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            writer.WriteAsync(Payload, TestContext.Current.CancellationToken)
        );
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            writer.WriteAsync(ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken)
        );
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task IoFailureIsPropagatedWithoutRetry()
    {
        using var stream = Substitute.For<Stream>();
        var calls = 0;
#pragma warning disable CA2012 // NSubstitute consumes this ValueTask call to configure the substitute.
        stream
            .WriteAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
                ++calls == 1
                    ? ValueTask.CompletedTask
                    : ValueTask.FromException(new IOException("Injected body failure"))
            );
#pragma warning restore CA2012
        var writer = new FrameWriter(stream, Limits);
        await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteAsync(Payload, TestContext.Current.CancellationToken)
        );
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ReadAndWriteHonorCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var stream = new MemoryStream(Frame(Payload));
        var reader = new FrameReader(stream, Limits);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadAsync(cancellation.Token)
        );
        var writer = new FrameWriter(stream, Limits);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            writer.WriteAsync(Payload, cancellation.Token)
        );
        Assert.Equal(0, stream.Position);
    }

    private sealed class FragmentedStream(
        byte[] bytes,
        int split = int.MaxValue,
        int maxChunk = int.MaxValue
    ) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            var count = Math.Min(buffer.Length, maxChunk);
            if (Position < split)
            {
                count = (int)Math.Min(count, split - Position);
            }
            return base.ReadAsync(buffer[..count], cancellationToken);
        }
    }
}
