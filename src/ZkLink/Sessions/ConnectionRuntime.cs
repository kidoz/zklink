using System.Buffers.Binary;
using System.Net.Sockets;
using ZkLink.Configuration;
using ZkLink.Protocol;
using ZkLink.Requests;
using ZkLink.Transport;

namespace ZkLink.Sessions;

internal enum RuntimeStop
{
    Stopped,
    Disconnected,
    Protocol,
    Expired,
    AuthFailed,
    Lost,
    CloseAcknowledged,
}

internal readonly record struct RuntimeFailure(RuntimeStop Kind, int? ErrorCode = null);

internal sealed class ConnectionRuntime(
    SessionManager owner,
    TransportConnection transport,
    RequestScheduler scheduler,
    ClientOptionsSnapshot options,
    TimeProvider time,
    AsyncPulse work,
    long generation,
    TimeSpan timeout
)
{
    private readonly AsyncPulse _activity = new();
    private long _lastWrite = time.GetTimestamp();
    internal long Generation { get; } = generation;
    internal ZooKeeperEndpoint Endpoint => transport.Endpoint;

    internal async Task<RuntimeFailure> RunAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reader = ReadAsync(lifetime.Token);
        var writer = WriteAsync(lifetime.Token);
        var heartbeat = HeartbeatAsync(lifetime.Token);
        try
        {
            var winner = await Task.WhenAny(reader, writer, heartbeat).ConfigureAwait(false);
            var failure = await winner.ConfigureAwait(false);
            owner.TransportStopped(Generation, failure);
            return failure;
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await transport.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(reader, writer, heartbeat).ConfigureAwait(false);
        }
    }

    private async Task<RuntimeFailure> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var packet = await transport
                    .Reader.ReadAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (packet is null)
                {
                    return Stop(new(RuntimeStop.Disconnected));
                }
                cancellationToken.ThrowIfCancellationRequested();
                var input = new JuteReader(packet, options.Limits);
                var header = input.ReadReplyHeader();
                if (header.Xid == -1)
                {
                    var stop = owner.Notification(
                        Generation,
                        ReplyCodec.DecodeNotification(packet, options.Limits)
                    );
                    if (stop is { } value)
                    {
                        return Stop(value);
                    }
                }
                else if (header.Xid == -2)
                {
                    var acknowledgment = ReplyCodec.Decode(
                        packet,
                        -2,
                        new PingRequest(),
                        options.Limits
                    );
                    var stop = owner.PingReply(Generation, acknowledgment.Header);
                    if (stop is { } value)
                    {
                        return Stop(value);
                    }
                }
                else
                {
                    var pending = owner.Expected(Generation, header.Xid, out var expectedShape);
                    if (pending is null)
                    {
                        return new(RuntimeStop.Stopped);
                    }
                    var reply = DecodeMatchedReply(ref input, header, expectedShape!);
                    var stop = owner.Reply(Generation, pending, reply);
                    if (stop is { } value)
                    {
                        return Stop(value);
                    }
                }
                _activity.Set();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(RuntimeStop.Stopped);
        }
        catch (InvalidDataException)
        {
            return Stop(new(RuntimeStop.Protocol));
        }
        catch (IOException)
        {
            return Stop(new(RuntimeStop.Disconnected));
        }
        catch (SocketException)
        {
            return Stop(new(RuntimeStop.Disconnected));
        }
        catch (ObjectDisposedException)
        {
            return Stop(new(RuntimeStop.Disconnected));
        }
    }

    private WireReply DecodeMatchedReply(
        ref JuteReader input,
        ReplyHeader header,
        WireRequest shape
    )
    {
        RequestCodec.ValidateXid(header.Xid, shape.Opcode);
        int memberSessionError = 0;
        try
        {
            var result = ReplyCodec.ReadReplyBody(
                ref input,
                header,
                shape,
                options.Limits,
                out memberSessionError
            );
            if (header.Error == 0)
            {
                result = NamespaceTranslator.ToClient(result, shape.Opcode, options.Paths);
            }
            return new WireReply(header, result);
        }
        catch (InvalidDataException)
            when (header.Error == 0
                && shape
                    is ReadRequest
                        {
                            Watch: false,
                            Type: ZooKeeperOperation.Exists
                                or ZooKeeperOperation.GetData
                                or ZooKeeperOperation.GetChildren
                        }
                        or PathRequest
                        {
                            Type: ZooKeeperOperation.GetAcl
                                or ZooKeeperOperation.Sync
                                or ZooKeeperOperation.GetEphemerals
                                or ZooKeeperOperation.GetAllChildrenNumber
                        }
                        or EmptyRequest { Type: ZooKeeperOperation.WhoAmI }
                        or CreateRequest
                        or SetDataRequest
                        or SetAclRequest
                        or VersionRequest { Type: ZooKeeperOperation.Delete }
                        or MultiRequest { ReadOnly: true }
            )
        {
            // The complete frame matched the FIFO head. These calls have no watch to reconcile.
            return new WireReply(header, new InvalidReplyBodyResult(memberSessionError));
        }
    }

    private async Task<RuntimeFailure> WriteAsync(CancellationToken cancellationToken)
    {
        Func<ReadOnlyMemory<byte>, Task> write = packet =>
            transport.Writer.WriteFrameAsync(packet, cancellationToken);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.PrepareWatchCleanup(Generation);
                (var pending, var writing) = WriteNext(
                    scheduler,
                    Generation,
                    write,
                    cancellationToken
                );
                if (writing is null)
                {
                    await work.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    await writing.ConfigureAwait(false);
                }
                finally
                {
                    if (pending is not null)
                    {
                        scheduler.WriteFinished(pending);
                    }
                }
                Interlocked.Exchange(ref _lastWrite, time.GetTimestamp());
                _activity.Set();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(RuntimeStop.Stopped);
        }
        catch (InvalidDataException)
        {
            return Stop(new(RuntimeStop.Protocol));
        }
        catch (IOException)
        {
            return Stop(new(RuntimeStop.Disconnected));
        }
        catch (SocketException)
        {
            return Stop(new(RuntimeStop.Disconnected));
        }
        catch (ObjectDisposedException)
        {
            return Stop(new(RuntimeStop.Disconnected));
        }
    }

    internal static (PendingRequest? Request, Task? Write) WriteNext(
        RequestScheduler scheduler,
        long generation,
        Func<ReadOnlyMemory<byte>, Task> write,
        CancellationToken writerToken
    )
    {
        var (pending, packet) = scheduler.Take(generation, writerToken);
        if (packet.IsEmpty)
        {
            return (null, null);
        }
        try
        {
            return (pending, write(packet));
        }
        catch
        {
            if (pending is not null)
            {
                scheduler.WriteFinished(pending);
            }
            throw;
        }
    }

    private async Task<RuntimeFailure> HeartbeatAsync(CancellationToken cancellationToken)
    {
        using var timer = time.CreateTimer(
            static state => ((AsyncPulse)state!).Set(),
            _activity,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
        try
        {
            while (true)
            {
                var inbound = owner.InboundTimestamp;
                var silence = time.GetElapsedTime(inbound);
                if (silence >= timeout)
                {
                    var failure = new RuntimeFailure(RuntimeStop.Lost);
                    if (owner.StopIfInboundUnchanged(Generation, inbound, failure))
                    {
                        return failure;
                    }
                    continue;
                }
                var readRemaining = TimeSpan.FromTicks(timeout.Ticks * 2 / 3) - silence;
                if (readRemaining <= TimeSpan.Zero)
                {
                    var failure = new RuntimeFailure(RuntimeStop.Disconnected);
                    if (owner.StopIfInboundUnchanged(Generation, inbound, failure))
                    {
                        return failure;
                    }
                    continue;
                }
                var unanswered = scheduler.OldestRemaining(timeout);
                if (unanswered is { } overdue && overdue <= TimeSpan.Zero)
                {
                    return Stop(new(RuntimeStop.Disconnected));
                }
                var next = readRemaining;
                if (unanswered is { } remaining && remaining < next)
                {
                    next = remaining;
                }
                if (!scheduler.PingPending)
                {
                    var idle =
                        TimeSpan.FromTicks(timeout.Ticks / 3)
                        - time.GetElapsedTime(Interlocked.Read(ref _lastWrite));
                    if (idle <= TimeSpan.Zero)
                    {
                        scheduler.QueuePing();
                    }
                    else if (idle < next)
                    {
                        next = idle;
                    }
                }
                await _activity.WaitAsync(next, timer, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(RuntimeStop.Stopped);
        }
    }

    private RuntimeFailure Stop(RuntimeFailure value)
    {
        owner.TransportStopped(Generation, value);
        return value;
    }
}
