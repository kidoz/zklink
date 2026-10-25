using System.Security.Cryptography;

namespace ZkLink.Protocol;

// This is a wire result, not an established session; its secret belongs to the future session owner.
internal sealed class ConnectResponse(int timeoutMilliseconds, long sessionId, byte[] password)
    : IDisposable
{
    internal int TimeoutMilliseconds { get; } = timeoutMilliseconds;
    internal long SessionId { get; } = sessionId;
    internal ReadOnlyMemory<byte> Password => password;
    internal bool Accepted => TimeoutMilliseconds > 0;

    public void Dispose() => CryptographicOperations.ZeroMemory(password);
}

internal static class HandshakeCodec
{
    internal const int PasswordLength = 16;

    internal static int Encode(
        Span<byte> destination,
        int timeoutMilliseconds,
        long sessionId,
        long lastZxid,
        ReadOnlySpan<byte> password,
        ProtocolLimits limits
    )
    {
        if (timeoutMilliseconds <= 0 || lastZxid < 0 || password.Length != PasswordLength)
            throw new InvalidDataException("Invalid handshake parameters.");
        if (sessionId == 0 && (lastZxid != 0 || password.ContainsAnyExcept((byte)0)))
            throw new InvalidDataException("A new session requires a zero watermark and password.");
        var writer = new JuteWriter(destination, limits);
        writer.WriteInt32(0);
        writer.WriteInt64(lastZxid);
        writer.WriteInt32(timeoutMilliseconds);
        writer.WriteInt64(sessionId);
        writer.WriteBuffer(password);
        writer.WriteBoolean(false);
        return writer.WrittenCount;
    }

    internal static int EncodeNew(
        Span<byte> destination,
        int timeoutMilliseconds,
        ProtocolLimits limits
    )
    {
        Span<byte> password = stackalloc byte[PasswordLength];
        password.Clear();
        return Encode(destination, timeoutMilliseconds, 0, 0, password, limits);
    }

    internal static ConnectResponse Decode(ReadOnlySpan<byte> payload, ProtocolLimits limits)
    {
        var reader = new JuteReader(payload, limits);
        if (reader.ReadInt32() != 0)
            throw new InvalidDataException("Unsupported handshake protocol version.");
        int timeout = reader.ReadInt32();
        long sessionId = reader.ReadInt64();
        byte[]? password = reader.ReadBuffer(PasswordLength);
        try
        {
            // Only this exact historical boundary may omit readOnly.
            bool readOnly = reader.Remaining != 0 && reader.ReadBoolean();
            reader.EnsureEnd();
            if (readOnly)
                throw new InvalidDataException("Read-only sessions are unsupported.");
            if (
                timeout > 0
                && (password is null || password.Length != PasswordLength || sessionId == 0)
            )
                throw new InvalidDataException("Invalid accepted-session credentials.");
            return new ConnectResponse(timeout, sessionId, password ?? []);
        }
        catch
        {
            if (password is not null)
                CryptographicOperations.ZeroMemory(password);
            throw;
        }
    }
}
