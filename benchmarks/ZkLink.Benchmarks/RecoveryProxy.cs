using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ZkLink.Protocol;
using ZkLink.Watches;

namespace ZkLink.Benchmarks;

// One initial connection and one paused reconnect. Inspection contributes to reported cost.
internal sealed class RecoveryProxy : IAsyncDisposable
{
    private const int AuthenticationOpcode = 100;
    private const int LegacyResetOpcode = 101;
    private const int ExtendedResetOpcode = 105;
    private const int LegacyVectorCount = 3;
    private const int ExtendedVectorCount = 5;
    private const int PersistentVectorIndex = 3;
    private const int ReplyHeaderBytes = sizeof(int) + sizeof(long) + sizeof(int);
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop;
    private readonly object _gate = new();
    private readonly IPEndPoint _target;
    private readonly int _expectedPaths;
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
    private readonly Task _run;
    private TcpClient? _client;
    private TcpClient? _upstream;
    private TaskCompletionSource _resume = Completed();
    private readonly TaskCompletionSource _paused = NewSignal();
    private readonly TaskCompletionSource _restoreHeld = NewSignal();
    private readonly TaskCompletionSource _releaseRestore = NewSignal();
    private int _connections;
    private int _handshakes;
    private int _authRequests;
    private int _adds;
    private int _syncs;
    private int _resetFrames;
    private int _resetReplies;
    private int _restorePaths;
    private int _maximumResetBytes;
    private long _secondHandshakeAt;
    private long _firstResetAt;
    private long _lastResetReplyAt;
    private long _heldAt;
    internal string Endpoint { get; }
    internal Task ReconnectPaused => _paused.Task;
    internal Task RestoreHeld => _restoreHeld.Task;

    internal RecoveryProxy(string endpoint, int expectedPaths, CancellationToken token)
    {
        _target = IPEndPoint.Parse(endpoint);
        _expectedPaths = expectedPaths;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _stop.CancelAfter(TimeSpan.FromSeconds(RecoverySettings.CaseGuardSeconds));
        _listener.Start(1);
        Endpoint = $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _run = RunAsync();
    }

    internal void CutAndPause()
    {
        lock (_gate)
        {
            if (_connections != 1 || _client is null || _upstream is null)
            {
                throw new InvalidOperationException(
                    "One established initial connection is required."
                );
            }
            _resume = NewSignal();
            _client.Dispose();
            _upstream.Dispose();
        }
    }

    internal void Resume() => _resume.TrySetResult();

    internal void ReleaseRestore() => _releaseRestore.TrySetResult();

    internal RecoveryWireObservation Observation()
    {
        lock (_gate)
        {
            return new(
                _connections,
                _handshakes,
                _authRequests,
                _adds,
                _syncs,
                _resetFrames,
                _resetReplies,
                _restorePaths,
                _paths.Count,
                _maximumResetBytes,
                _secondHandshakeAt,
                _firstResetAt,
                _lastResetReplyAt,
                _heldAt,
                _run.Exception?.GetBaseException().GetType().Name
            );
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                using var client = await _listener
                    .AcceptTcpClientAsync(_stop.Token)
                    .ConfigureAwait(false);
                int connection = Interlocked.Increment(ref _connections);
                if (connection > 2)
                {
                    throw new InvalidDataException("Unexpected additional reconnect.");
                }
                if (connection == 2)
                {
                    _paused.TrySetResult();
                    await _resume.Task.WaitAsync(_stop.Token).ConfigureAwait(false);
                }
                using var upstream = new TcpClient();
                await upstream
                    .ConnectAsync(_target.Address, _target.Port, _stop.Token)
                    .ConfigureAwait(false);
                client.NoDelay = true;
                upstream.NoDelay = true;
                var downstreamStream = client.GetStream();
                var upstreamStream = upstream.GetStream();
                lock (_gate)
                {
                    _client = client;
                    _upstream = upstream;
                }
                try
                {
                    await ForwardAsync(downstreamStream, upstreamStream).ConfigureAwait(false);
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

    private async Task ForwardAsync(Stream downstream, Stream upstream)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var outgoing = PumpAsync(downstream, upstream, false, stop.Token);
        var incoming = PumpAsync(upstream, downstream, true, stop.Token);
        try
        {
            await Task.WhenAny(outgoing, incoming).ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(outgoing, incoming).ConfigureAwait(false);
        }
    }

    private async Task PumpAsync(
        Stream source,
        Stream destination,
        bool incoming,
        CancellationToken token
    )
    {
        var limits = new ProtocolLimits();
        var reader = new FrameReader(source, limits);
        var writer = new FrameWriter(destination, limits);
        bool handshake = true;
        try
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false) is { } packet)
            {
                try
                {
                    if (handshake)
                    {
                        if (incoming)
                        {
                            Interlocked.Increment(ref _handshakes);
                            if (Volatile.Read(ref _connections) == 2)
                            {
                                Volatile.Write(ref _secondHandshakeAt, Stopwatch.GetTimestamp());
                            }
                        }
                    }
                    else if (incoming)
                    {
                        await InspectReplyAsync(packet, token).ConfigureAwait(false);
                    }
                    else
                    {
                        InspectRequest(packet);
                    }
                    await writer.WriteAsync(packet, token).ConfigureAwait(false);
                    handshake = false;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(packet);
                }
            }
        }
        catch (IOException) { } // Expected socket EOF/reset; malformed-frame errors propagate.
        catch (ObjectDisposedException)
            when (token.IsCancellationRequested || Volatile.Read(ref _connections) == 1) { }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void InspectRequest(byte[] packet)
    {
        var reader = new JuteReader(packet, new ProtocolLimits());
        var header = reader.ReadRequestHeader();
        if (header.Opcode == AuthenticationOpcode)
        {
            Interlocked.Increment(ref _authRequests);
        }
        if (header.Opcode == (int)ZooKeeperOperation.Sync)
        {
            Interlocked.Increment(ref _syncs);
        }
        if (header.Opcode == (int)ZooKeeperOperation.AddWatch)
        {
            Interlocked.Increment(ref _adds);
        }
        if (header.Opcode is not (LegacyResetOpcode or ExtendedResetOpcode))
        {
            return;
        }
        if (
            header.Xid != WatchProtocol.ResetXid
            || packet.Length > WatchProtocol.RestoreTargetBytes
        )
        {
            throw new InvalidDataException("Unexpected restore frame shape or bound.");
        }
        _ = reader.ReadInt64();
        int categories =
            header.Opcode == ExtendedResetOpcode ? ExtendedVectorCount : LegacyVectorCount;
        lock (_gate)
        {
            if (_resetFrames == 0)
            {
                _firstResetAt = Stopwatch.GetTimestamp();
            }
            _resetFrames++;
            _maximumResetBytes = Math.Max(_maximumResetBytes, packet.Length);
            for (int index = 0; index < categories; index++)
            {
                var paths = reader.ReadStringVector()!;
                if (index != PersistentVectorIndex && paths.Length != 0)
                {
                    throw new InvalidDataException(
                        "Only persistent paths are expected in this restore."
                    );
                }
                foreach (string? path in paths)
                {
                    if (path is null || !_paths.Add(path) || ++_restorePaths > _expectedPaths)
                    {
                        throw new InvalidDataException("Duplicate/excess restore path.");
                    }
                }
            }
            reader.EnsureEnd();
        }
    }

    private async Task InspectReplyAsync(byte[] packet, CancellationToken token)
    {
        if (
            packet.Length < ReplyHeaderBytes
            || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(packet)
                != WatchProtocol.ResetXid
        )
        {
            return;
        }
        if (
            packet.Length != ReplyHeaderBytes
            || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(
                packet.AsSpan(sizeof(int) + sizeof(long))
            ) != WatchProtocol.Success
        )
        {
            throw new InvalidDataException("Native restore acknowledgment failed.");
        }
        if (Interlocked.Increment(ref _resetReplies) == 1)
        {
            Volatile.Write(ref _heldAt, Stopwatch.GetTimestamp());
            _restoreHeld.TrySetResult();
            await _releaseRestore.Task.WaitAsync(token).ConfigureAwait(false);
        }
        Volatile.Write(ref _lastResetReplyAt, Stopwatch.GetTimestamp());
    }

    public async ValueTask DisposeAsync()
    {
        Resume();
        ReleaseRestore();
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

    private static TaskCompletionSource Completed()
    {
        var signal = NewSignal();
        signal.SetResult();
        return signal;
    }
}

internal sealed record RecoveryWireObservation(
    int Connections,
    int Handshakes,
    int AuthenticationRequests,
    int AddWatchRequests,
    int SyncRequests,
    int RestoreFrames,
    int RestoreReplies,
    int RestorePaths,
    int DistinctRestorePaths,
    int MaximumRestoreFrameBytes,
    long SecondHandshakeAt,
    long FirstResetAt,
    long LastResetReplyAt,
    long HeldAt,
    string? ErrorType
);
