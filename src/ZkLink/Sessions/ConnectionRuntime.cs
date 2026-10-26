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
                    if (!scheduler.ConsumePing(Generation))
                    {
                        return new(RuntimeStop.Stopped);
                    }
                    owner.Inbound(Generation, null);
                    if (acknowledgment.Header.Error != 0)
                    {
                        return Stop(
                            new(
                                acknowledgment.Header.Error switch
                                {
                                    -112 => RuntimeStop.Expired,
                                    -115 or -124 => RuntimeStop.AuthFailed,
                                    -4 or -7 or -12 or -118 or -122 => RuntimeStop.Disconnected,
                                    _ => RuntimeStop.Protocol,
                                },
                                acknowledgment.Header.Error
                            )
                        );
                    }
                }
                else
                {
                    var pending = scheduler.Expected(Generation, header.Xid, out var expectedShape);
                    if (pending is null)
                    {
                        return new(RuntimeStop.Stopped);
                    }
                    var shape =
                        expectedShape
                        ?? throw new InvalidDataException("Missing expected reply shape.");
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
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (var pending, var ping) = scheduler.Take(Generation);
                if (pending is null && !ping)
                {
                    await work.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (ping)
                {
                    await transport
                        .Writer.WriteAsync(PingPacket, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        await transport
                            .Writer.WriteAsync(pending!.Packet!, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        scheduler.WriteFinished(pending!);
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
                var silence = owner.InboundSilence;
                if (silence >= timeout)
                {
                    return Stop(new(RuntimeStop.Lost));
                }
                var readRemaining = TimeSpan.FromTicks(timeout.Ticks * 2 / 3) - silence;
                if (readRemaining <= TimeSpan.Zero)
                {
                    return Stop(new(RuntimeStop.Disconnected));
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
