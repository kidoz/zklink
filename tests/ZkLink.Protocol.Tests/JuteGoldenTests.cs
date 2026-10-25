using ZkLink.Protocol;

namespace ZkLink.Protocol.Tests;

public sealed class JuteGoldenTests
{
    private static readonly int[] Ints = [int.MinValue, -1, 0, 1, int.MaxValue];
    private static readonly long[] Longs = [long.MinValue, -1, 0, 1, long.MaxValue];
    private static readonly string?[] Strings = [null, "", "ZooKeeper", "Ж中é"];
    private static readonly ProtocolLimits Limits = new();
    private static readonly WireStat Stat = new(
        long.MinValue,
        long.MaxValue,
        -1,
        0,
        int.MinValue,
        -1,
        int.MaxValue,
        0x0102030405060708,
        123,
        456,
        -2
    );

    private static byte[] GoldenBytes() =>
        Convert.FromHexString(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jute-records.hex"))
                .Trim()
        );

    [Fact]
    [Trait("Acceptance", "T01")]
    public void DecodeApacheJavaRecords()
    {
        var reader = new JuteReader(GoldenBytes(), Limits);
        foreach (int value in Ints)
            Assert.Equal(value, reader.ReadInt32());
        foreach (long value in Longs)
            Assert.Equal(value, reader.ReadInt64());
        Assert.False(reader.ReadBoolean());
        Assert.True(reader.ReadBoolean());
        foreach (string? value in Strings)
            Assert.Equal(value, reader.ReadString());
        Assert.Null(reader.ReadBuffer());
        Assert.Empty(Assert.IsType<byte[]>(reader.ReadBuffer()));
        Assert.Equal(new byte[] { 0, 255, 128 }, reader.ReadBuffer());
        Assert.Equal(Strings, reader.ReadStringVector());
        Assert.Null(reader.ReadStringVector(allowNull: true));
        Assert.Equal(Stat, reader.ReadStat());
        Assert.Equal(new RequestHeader(1, 4), reader.ReadRequestHeader());
        Assert.Equal(new ReplyHeader(-2, long.MaxValue, -101), reader.ReadReplyHeader());
        reader.EnsureEnd();
    }

    [Fact]
    [Trait("Acceptance", "T01")]
    public void EncodeMatchesApacheJavaRecords()
    {
        byte[] golden = GoldenBytes();
        var bytes = new byte[golden.Length];
        var writer = new JuteWriter(bytes, Limits);
        foreach (int value in Ints)
            writer.WriteInt32(value);
        foreach (long value in Longs)
            writer.WriteInt64(value);
        writer.WriteBoolean(false);
        writer.WriteBoolean(true);
        foreach (string? value in Strings)
            writer.WriteString(value);
        writer.WriteNullBuffer();
        writer.WriteBuffer([]);
        writer.WriteBuffer([0, 255, 128]);
        writer.WriteStringVector(Strings);
        writer.WriteStringVector(null, allowNull: true);
        writer.WriteStat(Stat);
        writer.WriteRequestHeader(new RequestHeader(1, 4));
        writer.WriteReplyHeader(new ReplyHeader(-2, long.MaxValue, -101));
        Assert.Equal(bytes.Length, writer.WrittenCount);
        Assert.Equal(golden, bytes);

        // The optional oracle runner asks Java to deserialize these C# bytes.
        string? output = Environment.GetEnvironmentVariable("ZKLINK_ORACLE_OUTPUT");
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "jute-records.hex"), Convert.ToHexString(bytes));
        }
    }
}
