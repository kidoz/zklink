using System.Buffers.Binary;
using System.Threading.Channels;
using ZkLink.Configuration;
using ZkLink.Protocol;
using ZkLink.Protocol.Tests.Peers;
using ZkLink.Sessions;
using ZkLink.Transport;

namespace ZkLink.Protocol.Tests.Sessions;

internal sealed record ReceivedPacket(byte[] Bytes, bool Handshake)
{
    internal int Xid => BinaryPrimitives.ReadInt32BigEndian(Bytes);
    internal int Opcode => BinaryPrimitives.ReadInt32BigEndian(Bytes.AsSpan(4));
}

internal sealed class SessionPeer : Stream
{
    private static readonly ProtocolLimits Limits = new();
    private readonly ScriptedPeerStream _incoming = new();
    private readonly MemoryStream _accumulated = new();
    private int _decoded;
    private int _packets;
    private int _written;
    private int _disposed;
    private readonly Channel<ReceivedPacket> _requests = Channel.CreateBounded<ReceivedPacket>(256);
    private readonly Channel<bool> _readWaits = Channel.CreateBounded<bool>(256);
    private TaskCompletionSource? _heldWrite;
    private readonly TaskCompletionSource _applicationWriteHeld = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    internal Task ApplicationWriteHeld => _applicationWriteHeld.Task;
    private TaskCompletionSource? _heldPingWrite;
    private readonly TaskCompletionSource _pingWriteHeld = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    internal Task PingWriteHeld => _pingWriteHeld.Task;
    internal int NegotiatedTimeout { get; init; } = 600;
    internal long Id { get; init; } = 123;
    internal bool ReplyHandshake { get; set; } = true;
    internal bool ReplyApplications { get; set; } = true;
    internal bool ReplyPings { get; set; } = true;
    internal bool ReplyClose { get; set; } = true;
    internal bool ReplyAuth { get; set; } = true;
    internal bool ReplyRestores { get; set; } = true;
    internal int AuthError { get; init; }
    internal int? FailWriteAt { get; set; }
    internal bool HoldApplicationWrite { get; set; }
    internal bool HoldPingWrite { get; set; }
    internal ReadOnlyMemory<byte> HeldWriterMemory { get; private set; }
    internal int WrittenCount => _written;
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    internal ChannelReader<ReceivedPacket> Requests => _requests.Reader;

    internal Task ReaderWaitingAsync(CancellationToken token) =>
        _readWaits.Reader.ReadAsync(token).AsTask();

    internal bool DrainReadWaits() => _readWaits.Reader.TryRead(out _);

    internal void ReleaseWrite()
    {
        HoldApplicationWrite = false;
        _heldWrite?.TrySetResult();
    }

    internal void ReleasePingWrite()
    {
        HoldPingWrite = false;
        _heldPingWrite?.TrySetResult();
    }

    internal void Drop() => _incoming.CompleteInput();

    internal static byte[] Packet(int xid, int error = 0, long zxid = 12, string? path = null)
    {
        var storage = new byte[1024];
        var writer = new JuteWriter(storage, Limits);
        writer.WriteReplyHeader(new ReplyHeader(xid, zxid, error));
        if (error == 0 && path is not null)
        {
            writer.WriteString(path);
        }
        return storage[..writer.WrittenCount];
    }

    internal void Reply(ReceivedPacket request, int error = 0, long zxid = 12)
    {
        string? path = null;
        if (request.Opcode == 9 && error == 0)
        {
            var input = new JuteReader(request.Bytes, Limits);
            input.ReadRequestHeader();
            path = input.ReadString();
        }
        _incoming.Enqueue(PeerTests.Frame(Packet(request.Xid, error, zxid, path)));
    }

    internal void Inject(byte[] payload) => _incoming.Enqueue(PeerTests.Frame(payload));

    internal void HandshakeReply()
    {
        var bytes = new byte[64];
        var output = new JuteWriter(bytes, Limits);
        output.WriteInt32(0);
        output.WriteInt32(NegotiatedTimeout);
        output.WriteInt64(Id);
        var secret = new byte[16];
        Array.Fill(secret, (byte)0x5a);
        output.WriteBuffer(secret);
        output.WriteBoolean(false);
        _incoming.Enqueue(PeerTests.Frame(bytes[..output.WrittenCount]));
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = FailWriteAt is { } limit
            ? Math.Min(buffer.Length, Math.Max(0, limit - _written))
            : buffer.Length;
        if (count > 4 * 1024 * 1024 - _accumulated.Length)
        {
            throw new IOException("Test peer output bound exceeded.");
        }
        _accumulated.Write(buffer.Span[..count]);
        _written += count;
        if (count != buffer.Length)
        {
            throw new IOException("Injected partial write.");
        }
        var data = _accumulated.GetBuffer();
        while (_accumulated.Length - _decoded >= 4)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(_decoded));
            if (length <= 0 || length > Limits.MaxFrameBytes)
            {
                throw new IOException("Invalid test request frame.");
            }
            if (_accumulated.Length - _decoded - 4 < length)
            {
                break;
            }
            var payload = data.AsSpan(_decoded + 4, length).ToArray();
            _decoded += 4 + length;
            var request = new ReceivedPacket(payload, _packets++ == 0);
            if (!_requests.Writer.TryWrite(request))
            {
                throw new IOException("Test request count bound exceeded.");
            }
            if (request.Handshake)
            {
                if (ReplyHandshake)
                {
                    HandshakeReply();
                }
            }
            else
            {
                if (request.Opcode == 100)
                {
                    if (ReplyAuth)
                    {
                        Reply(request, AuthError);
                    }
                }
                else if (request.Opcode == 11)
                {
                    if (ReplyPings)
                    {
                        Reply(request);
                    }
                    if (HoldPingWrite)
                    {
                        _heldPingWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _pingWriteHeld.TrySetResult();
                        await _heldPingWrite
                            .Task.WaitAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                else if (request.Opcode == -11)
                {
                    if (ReplyClose)
                    {
                        Reply(request);
                    }
                }
                else if (request.Opcode == 101 || request.Opcode == 105)
                {
                    if (ReplyRestores)
                        Reply(request);
                }
                else
                {
                    if (ReplyApplications)
                    {
                        Reply(request);
                    }
                    if (HoldApplicationWrite)
                    {
                        HeldWriterMemory = buffer;
                        _heldWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _applicationWriteHeld.TrySetResult();
                        await _heldWrite.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (_incoming.PendingBytes == 0 && !_readWaits.Writer.TryWrite(true))
        {
            throw new IOException("Test reader signal bound exceeded.");
        }
        return _incoming.ReadAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ReleaseWrite();
            ReleasePingWrite();
            _incoming.Dispose();
            _accumulated.Dispose();
            _requests.Writer.TryComplete();
            _readWaits.Writer.TryComplete();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

internal sealed class ScriptedConnector : ITransportConnector
{
    private readonly Queue<SessionPeer?> _peers = new();
    internal readonly Channel<ZooKeeperEndpoint> Attempts =
        Channel.CreateBounded<ZooKeeperEndpoint>(256);

    internal void Add(SessionPeer? peer)
    {
        lock (_peers)
        {
            _peers.Enqueue(peer);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000",
        Justification = "The fixture owns peers; the returned connection also disposes the selected peer on transport teardown."
    )]
    public async Task<TransportConnection> ConnectAsync(
        ZooKeeperEndpoint endpoint,
        ClientOptionsSnapshot options,
        CancellationToken cancellationToken
    )
    {
        if (!Attempts.Writer.TryWrite(endpoint))
        {
            throw new IOException("Too many test connection attempts.");
        }
        SessionPeer? peer;
        lock (_peers)
        {
            if (_peers.TryDequeue(out peer))
            {
                return peer is null
                    ? throw new IOException("Injected connection failure.")
                    : new TransportConnection(peer, endpoint, options.Limits);
            }
        }
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new OperationCanceledException(cancellationToken);
    }
}

internal sealed class SessionFixture : IAsyncDisposable
{
    internal ManualTimeProvider Time { get; } = new();
    internal ScriptedConnector Connector { get; } = new();
    internal ZooKeeperClient? Client { get; private set; }

    internal Task<ZooKeeperClient> ConnectAsync(
        SessionPeer peer,
        ZooKeeperClientOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        Connector.Add(peer);
        return ConnectCoreAsync(options ?? Options(), cancellationToken);
    }

    private async Task<ZooKeeperClient> ConnectCoreAsync(
        ZooKeeperClientOptions options,
        CancellationToken cancellationToken
    )
    {
        Client = await ZooKeeperClient
            .ConnectCoreAsync(
                ClientOptionsSnapshot.Capture(options),
                Time,
                Connector,
                new SessionRandom(new Random(42)),
                cancellationToken
            )
            .ConfigureAwait(false);
        return Client;
    }

    internal static ZooKeeperClientOptions Options(
        int inFlight = 4,
        int queued = 1024,
        int bytes = 32 * 1024 * 1024,
        int events = 64,
        string connection = "first:2181",
        IReadOnlyList<ZooKeeperAuthentication>? authentication = null,
        int collections = 100_000,
        bool enableTtlNodes = false,
        int watches = 10_000
    ) =>
        new()
        {
            ConnectionString = connection,
            Tls = ZooKeeperTlsOptions.Disabled(),
            AllowUnsafePlaintextDigest = true,
            Authentication = authentication ?? [],
            SessionTimeout = TimeSpan.FromSeconds(30),
            InitialConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectAttemptTimeout = TimeSpan.FromSeconds(1),
            OperationTimeout = TimeSpan.FromMilliseconds(100),
            CloseTimeout = TimeSpan.FromMilliseconds(100),
            MaxInFlightRequests = inFlight,
            MaxQueuedRequests = queued,
            MaxPendingRequestBytes = bytes,
            MaxBufferedEvents = events,
            MaxCollectionElements = collections,
            EnableTtlNodes = enableTtlNodes,
            MaxWatches = watches,
        };

    public async ValueTask DisposeAsync()
    {
        if (Client is null)
        {
            return;
        }
        if (
            Client.Session.State
            is ZooKeeperSessionState.Connected
                or ZooKeeperSessionState.Connecting
                or ZooKeeperSessionState.Disconnected
                or ZooKeeperSessionState.Closing
        )
        {
            await Client.Manager.AbortInitialAsync().ConfigureAwait(false);
        }
        await Client.DisposeAsync().ConfigureAwait(false);
    }
}
