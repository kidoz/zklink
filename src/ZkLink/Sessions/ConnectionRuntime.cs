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
    private static readonly byte[] PingPacket = [255, 255, 255, 254, 0, 0, 0, 11];
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
                    var shape = expectedShape!;
                    var reply = ReplyCodec.Decode(packet, header.Xid, shape, options.Limits);
                    if (reply.Header.Error == 0)
                    {
                        reply = reply with
                        {
                            Result = NamespaceTranslator.ToClient(
                                reply.Result,
                                shape.Opcode,
                                options.Paths
                            ),
                        };
                    }
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

    private async Task<RuntimeFailure> WriteAsync(CancellationToken cancellationToken)
    {
        Func<PendingRequest?, Task> write = pending =>
            transport.Writer.WriteAsync(
                pending is null ? PingPacket : pending.Packet!,
                cancellationToken
            );
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.PrepareWatchCleanup(Generation);
                (var pending, var writing) = scheduler.WriteNext(
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

    private async Task<RuntimeFailure> HeartbeatAsync(CancellationToken cancellationToken)
    {
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
                await _activity.WaitAsync(next, time, cancellationToken).ConfigureAwait(false);
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
