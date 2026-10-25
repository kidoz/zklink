namespace ZkLink.Protocol;

internal interface IJuteOutput
{
    void WriteInt32(int value);
    void WriteInt64(long value);
    void WriteBoolean(bool value);
    void WriteString(string? value);
    void WriteBuffer(ReadOnlySpan<byte> value);
    void WriteNullBuffer();
    void WriteStat(WireStat stat);
    void WriteStringVector(IReadOnlyList<string?>? values, bool allowNull = false);
}
