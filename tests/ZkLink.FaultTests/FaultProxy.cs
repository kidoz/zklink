using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ZkLink.FaultTests;

// Forwards bounded frames and consumes one selected create reply. Incoming-frame
// suppression and outgoing-frame suppression act independently across reconnects;
// restore acknowledgments can be held separately. Discarded frames are never queued.
internal sealed class FaultProxy : IAsyncDisposable
{
    internal const int Success = 0;
    private const int MaximumFrameBytes = 1_048_575;
    private const int HeaderBytes = sizeof(int) + sizeof(long) + sizeof(int);
    private const int ResetXid = -8;
    private const int PingXid = -2;
    private const int PingOperation = 11;
    private const int RequestHeaderBytes = sizeof(int) + sizeof(int);
    private static readonly TimeSpan LifetimeGuard = TimeSpan.FromSeconds(75);
    private readonly object _gate = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop;
    private readonly Uri _target;
    private readonly bool _holdRestore;
    private readonly Task _run;
    private readonly TaskCompletionSource _restoreRelease = NewSignal();
    private TcpClient? _client;
    private TcpClient? _upstream;
    private bool _armed;
    private bool _replyDiscarded;
    private int _incomingPartition;
    private int _outgoingPartition;
    private int _droppedIncomingFrames;
    private int _droppedOutgoingFrames;
    private int _droppedPings;
    private int _droppedCreates;
    private int _forwardedPings;
    private int? _selectedXid;
    private int _creates;
    private int _connectionAttempts;
    private int _handshakeReplies;
    private int _lastNegotiatedMilliseconds;
    private int _upstreamLocalPort;
    internal int UpstreamLocalPort => Volatile.Read(ref _upstreamLocalPort);
    internal int HandshakeReplies => Volatile.Read(ref _handshakeReplies);
    internal int LastNegotiatedMilliseconds => Volatile.Read(ref _lastNegotiatedMilliseconds);
    internal string? RunFailure => _run.Exception?.GetBaseException().GetType().Name;
    private readonly TaskCompletionSource _withheld = NewSignal();
    private readonly TaskCompletionSource _restoreHeld = NewSignal();
    private readonly TaskCompletionSource _serverClosed = NewSignal();
    private readonly TaskCompletionSource _createDropped = NewSignal();
    internal Task ResponseWithheld => _withheld.Task;
    internal Task RestoreHeld => _restoreHeld.Task;
    internal Task ServerClosed => _serverClosed.Task;
    internal int WithheldError { get; private set; }
    internal int CreateTransmissions => Volatile.Read(ref _creates);
    internal int ConnectionAttempts => Volatile.Read(ref _connectionAttempts);
    internal string Endpoint { get; }
    internal bool IncomingPartitionActive => Volatile.Read(ref _incomingPartition) != 0;
    internal bool OutgoingPartitionActive => Volatile.Read(ref _outgoingPartition) != 0;
    internal int DroppedIncomingFrames => Volatile.Read(ref _droppedIncomingFrames);
    internal int DroppedOutgoingFrames => Volatile.Read(ref _droppedOutgoingFrames);
    internal int DroppedPings => Volatile.Read(ref _droppedPings);
    internal int DroppedCreates => Volatile.Read(ref _droppedCreates);
    internal Task CreateDropped => _createDropped.Task;
    internal int ForwardedPings => Volatile.Read(ref _forwardedPings);
    internal bool HasOpenConnection
    {
        get
        {
            lock (_gate)
            {
                return _client is not null;
            }
        }
    }

    internal void BlockIncoming() => Volatile.Write(ref _incomingPartition, 1);

    internal void HealIncoming() => Volatile.Write(ref _incomingPartition, 0);

    internal void BlockOutgoing() => Volatile.Write(ref _outgoingPartition, 1);

    internal void HealOutgoing() => Volatile.Write(ref _outgoingPartition, 0);

    internal FaultProxy(string endpoint, bool holdRestore, CancellationToken token)
    {
        _target = new Uri("tcp://" + endpoint);
        if (
            !IPAddress.TryParse(_target.Host, out var address)
            || !IPAddress.IsLoopback(address)
            || _target.Port <= 0
        )
        {
            throw new ArgumentException("A loopback fixture target is required.", nameof(endpoint));
        }
        _holdRestore = holdRestore;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _stop.CancelAfter(LifetimeGuard);
        _listener.Start(1);
        Endpoint = $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _run = RunAsync();
    }

    internal void ArmCreateReply()
    {
        lock (_gate)
        {
            if (_armed)
            {
                throw new InvalidOperationException("Only one create may be withheld.");
            }
            _armed = true;
        }
    }

    internal void ReleaseRestore() => _restoreRelease.TrySetResult();

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                _stop.Token.ThrowIfCancellationRequested();
                using var client = await _listener
                    .AcceptTcpClientAsync(_stop.Token)
                    .ConfigureAwait(false);
                using var upstream = new TcpClient();
                Interlocked.Increment(ref _connectionAttempts);
                NetworkStream downstreamStream;
                NetworkStream upstreamStream;
                try
                {
                    await upstream
                        .ConnectAsync(_target.Host, _target.Port, _stop.Token)
                        .ConfigureAwait(false);
                    client.NoDelay = true;
                    upstream.NoDelay = true;
                    // Capture both streams before forwarding can observe EOF or
                    // disposal can close the published sockets. GetStream checks
                    // Connected even when it has already created a stream.
                    downstreamStream = client.GetStream();
                    upstreamStream = upstream.GetStream();
                    Volatile.Write(
                        ref _upstreamLocalPort,
                        ((IPEndPoint)upstream.Client.LocalEndPoint!).Port
                    );
                }
                catch (SocketException)
                {
                    // The member can temporarily close its client port during election.
                    // Close this downstream attempt and accept the driver's next attempt.
                    continue;
                }
                catch (InvalidOperationException) when (!client.Connected || !upstream.Connected)
                {
                    continue;
                }
                lock (_gate)
                {
                    _client = client;
                    _upstream = upstream;
                }
                try
                {
                    await ForwardConnectionAsync(downstreamStream, upstreamStream)
                        .ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        _client = null;
                        _upstream = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch (InvalidOperationException) when (_stop.IsCancellationRequested) { }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task ForwardConnectionAsync(Stream downstream, Stream upstream)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var outgoing = ForwardAsync(downstream, upstream, false, lifetime.Token);
        var incoming = ForwardAsync(upstream, downstream, true, lifetime.Token);
        try
        {
            await Task.WhenAny(outgoing, incoming).ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(outgoing, incoming).ConfigureAwait(false);
        }
    }

    private async Task ForwardAsync(
        Stream source,
        Stream destination,
        bool incoming,
        CancellationToken token
    )
    {
        bool handshake = true;
        var prefix = new byte[sizeof(int)];
        try
        {
            while (true)
            {
                if (await source.ReadAsync(prefix.AsMemory(0, 1), token).ConfigureAwait(false) == 0)
                {
                    if (incoming)
                    {
                        _serverClosed.TrySetResult();
                    }
                    return;
                }
                await source.ReadExactlyAsync(prefix.AsMemory(1), token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32BigEndian(prefix);
                if (length is <= 0 or > MaximumFrameBytes)
                {
                    throw new InvalidDataException("Fault proxy frame bound exceeded.");
                }
                var packet = new byte[length];
                try
                {
                    await source.ReadExactlyAsync(packet, token).ConfigureAwait(false);
                    if (incoming && handshake && length >= sizeof(int) + sizeof(int))
                    {
                        Volatile.Write(
                            ref _lastNegotiatedMilliseconds,
                            BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(sizeof(int)))
                        );
                        Interlocked.Increment(ref _handshakeReplies);
                    }
                    if (incoming && IncomingPartitionActive)
                    {
                        Interlocked.Increment(ref _droppedIncomingFrames);
                        handshake = false;
                        continue;
                    }
                    bool ping =
                        !incoming
                        && !handshake
                        && length == RequestHeaderBytes
                        && BinaryPrimitives.ReadInt32BigEndian(packet) == PingXid
                        && BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(sizeof(int)))
                            == PingOperation;
                    bool create =
                        !incoming
                        && !handshake
                        && length >= RequestHeaderBytes
                        && BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(sizeof(int)))
                            is (int)ZooKeeperOperation.Create
                                or (int)ZooKeeperOperation.CreateWithStat
                                or (int)ZooKeeperOperation.CreateContainer
                                or (int)ZooKeeperOperation.CreateTtl;
                    if (!incoming && OutgoingPartitionActive)
                    {
                        Interlocked.Increment(ref _droppedOutgoingFrames);
                        if (ping)
                        {
                            Interlocked.Increment(ref _droppedPings);
                        }
                        if (create)
                        {
                            Interlocked.Increment(ref _droppedCreates);
                            _createDropped.TrySetResult();
                        }
                        handshake = false;
                        continue;
                    }
                    if (!handshake && length >= HeaderBytes)
                    {
                        int xid = BinaryPrimitives.ReadInt32BigEndian(packet);
                        if (!incoming)
                        {
                            if (create)
                            {
                                Interlocked.Increment(ref _creates);
                                lock (_gate)
                                {
                                    if (_armed && _selectedXid is null)
                                    {
                                        _selectedXid = xid;
                                    }
                                }
                            }
                        }
                        else
                        {
                            bool discard;
                            lock (_gate)
                            {
                                discard = _selectedXid == xid && !_replyDiscarded;
                                if (discard)
                                {
                                    _replyDiscarded = true;
                                }
                            }
                            if (discard)
                            {
                                WithheldError = BinaryPrimitives.ReadInt32BigEndian(
                                    packet.AsSpan(sizeof(int) + sizeof(long))
                                );
                                _withheld.TrySetResult();
                                continue;
                            }
                            if (
                                _holdRestore
                                && xid == ResetXid
                                && BinaryPrimitives.ReadInt32BigEndian(
                                    packet.AsSpan(sizeof(int) + sizeof(long))
                                ) == Success
                            )
                            {
                                _restoreHeld.TrySetResult();
                                await _restoreRelease.Task.WaitAsync(token).ConfigureAwait(false);
                            }
                        }
                    }
                    handshake = false;
                    await destination.WriteAsync(prefix, token).ConfigureAwait(false);
                    await destination.WriteAsync(packet, token).ConfigureAwait(false);
                    if (ping)
                    {
                        Interlocked.Increment(ref _forwardedPings);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(packet);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException)
        {
            if (incoming && !token.IsCancellationRequested)
            {
                _serverClosed.TrySetResult();
            }
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        finally
        {
            CryptographicOperations.ZeroMemory(prefix);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        lock (_gate)
        {
            _client?.Dispose();
            _upstream?.Dispose();
        }
        try
        {
            await _run.ConfigureAwait(false);
        }
        finally
        {
            _listener.Dispose();
            _stop.Dispose();
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
