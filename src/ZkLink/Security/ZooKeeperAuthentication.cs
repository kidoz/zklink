using System.Security.Cryptography;
using System.Text;

namespace ZkLink;

/// <summary>Application-owned digest credentials, copied by ConnectAsync before it returns its Task.</summary>
public sealed class ZooKeeperAuthentication : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object _gate = new();
    private byte[]? _credentials;

    private ZooKeeperAuthentication(byte[] credentials) => _credentials = credentials;

    /// <summary>Creates UTF-8 digest credentials; no ACL is installed implicitly.</summary>
    public static ZooKeeperAuthentication Digest(string userName, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(userName);
        ArgumentNullException.ThrowIfNull(password);
        if (userName.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Digest user names cannot contain a colon.",
                nameof(userName)
            );
        }
        if ((long)userName.Length + password.Length > 1_000_000)
        {
            throw new ArgumentException("Authentication input exceeds its finite bound.");
        }
        try
        {
            var userBytes = StrictUtf8.GetByteCount(userName);
            var passwordBytes = StrictUtf8.GetByteCount(password);
            var length = checked(userBytes + 1 + passwordBytes);
            if (length > 1_000_000)
            {
                throw new ArgumentException("Authentication input exceeds its finite bound.");
            }
            var bytes = new byte[length];
            StrictUtf8.GetBytes(userName, bytes);
            bytes[userBytes] = (byte)':';
            StrictUtf8.GetBytes(password, bytes.AsSpan(userBytes + 1));
            return new ZooKeeperAuthentication(bytes);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Authentication input contains invalid UTF-16.");
        }
    }

    internal int Length
    {
        get
        {
            lock (_gate)
            {
                return (
                    _credentials
                    ?? throw new ObjectDisposedException(nameof(ZooKeeperAuthentication))
                ).Length;
            }
        }
    }

    internal byte[] Copy()
    {
        lock (_gate)
        {
            return (byte[])
                (
                    _credentials
                    ?? throw new ObjectDisposedException(nameof(ZooKeeperAuthentication))
                ).Clone();
        }
    }

    /// <summary>Clears this configuration's owned bytes; connected clients retain their independent copies.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_credentials is not null)
            {
                CryptographicOperations.ZeroMemory(_credentials);
            }
            _credentials = null;
        }
    }
}
