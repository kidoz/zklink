using System.Buffers.Binary;
using ZkLink.Protocol;

namespace ZkLink.Protocol.Tests;

public sealed class JuteValidationTests
{
    private static readonly ProtocolLimits Limits = new();

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(255, true)]
    [Trait("Acceptance", "T01")]
    public void BooleanMatchesJavaNonzeroSemantics(byte value, bool expected)
    {
        var reader = new JuteReader([value], Limits);
        Assert.Equal(expected, reader.ReadBoolean());
        reader.EnsureEnd();
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    [Trait("Acceptance", "T03")]
    public void InvalidFieldLengthsFailBeforeCopying(int length)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, length);
        Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new JuteReader(bytes, Limits);
            reader.ReadBuffer();
        });
        Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new JuteReader(bytes, Limits);
            reader.ReadString();
        });
    }

    [Theory]
    [InlineData("00000001ff")]
    [InlineData("00000002c080")]
    [InlineData("00000003eda080")]
    [InlineData("00000002e282")]
    [InlineData("00000004f4908080")]
    [Trait("Acceptance", "T03")]
    public void MalformedUtf8IsRejected(string hex)
    {
        byte[] bytes = Convert.FromHexString(hex);
        Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new JuteReader(bytes, Limits);
            reader.ReadString();
        });
    }

    [Theory]
    [InlineData(-2, true)]
    [InlineData(-1, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(100_001, false)]
    [InlineData(1, false)]
    [Trait("Acceptance", "T03")]
    public void InvalidVectorCountsAreRejected(int count, bool allowNull)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, count);
        Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new JuteReader(bytes, Limits);
            reader.ReadStringVector(allowNull);
        });
    }

    [Fact]
    [Trait("Acceptance", "T03")]
    public void ConfiguredVectorLimitIsEnforcedEvenWithEnoughBytes()
    {
        byte[] bytes = Convert.FromHexString("000000020000000000000000");
        Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new JuteReader(bytes, new ProtocolLimits(maxCollectionElements: 1));
            reader.ReadStringVector();
        });
    }

    [Fact]
    [Trait("Acceptance", "T03")]
    public void RejectEveryTruncatedStatAndUnexpectedTail()
    {
        for (int length = 0; length < WireStat.EncodedLength; length++)
        {
            var bytes = new byte[length];
            Assert.Throws<InvalidDataException>(() =>
            {
                var reader = new JuteReader(bytes, Limits);
                reader.ReadStat();
            });
        }
        Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new JuteReader(new byte[WireStat.EncodedLength + 1], Limits);
            reader.ReadStat();
            reader.EnsureEnd();
        });
    }

    [Fact]
    [Trait("Acceptance", "T01")]
    public void ReturnedDataDoesNotAliasSource()
    {
        byte[] source = [0, 0, 0, 2, 12, 34];
        var reader = new JuteReader(source, Limits);
        byte[] result = Assert.IsType<byte[]>(reader.ReadBuffer());
        source[4] = 255;
        Assert.Equal(new byte[] { 12, 34 }, result);
    }

    [Fact]
    [Trait("Acceptance", "T03")]
    public void WriterRejectsInvalidUnicodeAndExcessiveRecords()
    {
        byte[] bytes = new byte[16];
        Assert.Throws<InvalidDataException>(() =>
        {
            var writer = new JuteWriter(bytes, Limits);
            writer.WriteString("\ud800");
        });
        Assert.Throws<InvalidDataException>(() =>
        {
            var writer = new JuteWriter(bytes, new ProtocolLimits(maxFrameBytes: 4));
            writer.WriteBuffer([1]);
        });
        Assert.Throws<InvalidDataException>(() =>
        {
            var writer = new JuteWriter(bytes, new ProtocolLimits(maxCollectionElements: 1));
            writer.WriteStringVector(["a", "b"]);
        });
        Assert.Throws<InvalidDataException>(() =>
        {
            var writer = new JuteWriter(bytes, Limits);
            writer.WriteStat(default);
        });
        Assert.All(bytes, value => Assert.Equal(0, value));
    }

    [Fact]
    [Trait("Acceptance", "T03")]
    public void ReaderAndWriterRespectExactFrameBoundary()
    {
        byte[] bytes = new byte[4];
        var limits = new ProtocolLimits(maxFrameBytes: 4);
        var writer = new JuteWriter(bytes, limits);
        writer.WriteInt32(int.MaxValue);
        var reader = new JuteReader(bytes, limits);
        Assert.Equal(int.MaxValue, reader.ReadInt32());
        reader.EnsureEnd();
        Assert.Throws<InvalidDataException>(() =>
        {
            var oversized = new JuteReader(new byte[5], limits);
            oversized.EnsureEnd();
        });
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(1, 0)]
    [InlineData(1, int.MaxValue)]
    public void LimitsHaveFiniteValidatedBounds(int frameBytes, int elements) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProtocolLimits(frameBytes, elements));
}
