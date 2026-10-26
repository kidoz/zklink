using System.Security.Cryptography;
using ZkLink.Protocol;
using ZkLink.Requests;
using ZkLink.Transport;
using ZkLink.Watches;

namespace ZkLink.Sessions;

internal sealed partial class SessionManager
{
    internal int WatchCount => _watches.Count;

    internal void WakeWriter() => _work.Set();

    internal (ZooKeeperWatch Watch, Task<WireReply> Reply) WatchRead(
        string path,
        ZooKeeperWatchMode mode,
        CancellationToken token
    )
    {
        var operation = mode switch
        {
            ZooKeeperWatchMode.OneShotExists => ZooKeeperOperation.Exists,
            ZooKeeperWatchMode.OneShotData => ZooKeeperOperation.GetData,
            ZooKeeperWatchMode.OneShotChildren => ZooKeeperOperation.GetChildren,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        var info = new ZooKeeperRequestInfo(operation, path: path);
        lock (_gate)
        {
            var intent = _watches.Begin(path, mode, info);
            try
            {
                return (
                    intent.Watch,
                    Scheduler.Submit(
                        new ReadRequest(operation, path, true),
                        info,
                        token,
                        intent,
                        watchOperation: true
                    )
                );
            }
            catch
            {
                _watches.Abandon(intent.Watch);
                throw;
            }
        }
    }

    internal void WatchReadFailed(ZooKeeperWatch watch)
    {
        lock (_gate)
        {
            if (watch.Active)
                _watches.Dispose(watch);
            else if (!watch.Suppressed)
                _watches.Abandon(watch);
        }
    }

    internal Task<WireReply> SubmitWatchRemoval(WatchRequest request, CancellationToken token) =>
        Scheduler.Submit(
            request,
            new ZooKeeperRequestInfo(ZooKeeperOperation.RemoveWatches, path: request.Path),
            token,
            watchOperation: true
        );

    internal bool TryWatchCleanup(
        WatchRequest request,
        IRequestObserver observer,
        out Task<WireReply>? task
    )
    {
        if (
            Session.State != ZooKeeperSessionState.Connected
            || _closing
            || !Scheduler.MaintenanceAvailable
        )
        {
            task = null;
            return false;
        }
        task = Scheduler.Submit(
            request,
            new ZooKeeperRequestInfo(ZooKeeperOperation.RemoveWatches, path: request.Path),
            default,
            observer,
            watchOperation: true,
            maintenance: true
        );
        return true;
    }

    internal void PrepareWatchCleanup(long generation)
    {
        lock (_gate)
        {
            if (_active?.Generation == generation && Scheduler.IsAttached(generation) && !_closing)
                _watches.PrepareCleanup();
        }
    }

    private void DetachRequests()
    {
        Scheduler.Detach();
        _watches.Detached();
    }

    private async Task RestoreWatchesAsync(TransportConnection transport, CancellationToken token)
    {
        IReadOnlyList<RestoreRequest> batches;
        lock (_gate)
            batches = _watches.Restoration(_lastZxid);
        foreach (var batch in batches)
        {
            var translated = NamespaceTranslator.ToServer(batch, _options.Paths, _options.Limits);
            var bytes = new byte[
                RequestCodec.Measure(WatchProtocol.ResetXid, translated, _options.Limits)
            ];
            try
            {
                RequestCodec.Encode(bytes, WatchProtocol.ResetXid, translated, _options.Limits);
                await transport.Writer.WriteAsync(bytes, token).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
            while (true)
            {
                var packet =
                    await transport.Reader.ReadAsync(token).ConfigureAwait(false)
                    ?? throw new EndOfStreamException();
                var input = new JuteReader(packet, _options.Limits);
                var header = input.ReadReplyHeader();
                if (header.Xid == WatchProtocol.NotificationXid)
                {
                    var notification = ReplyCodec.DecodeNotification(packet, _options.Limits);
                    if (!notification.IsRecognized)
                        throw new InvalidDataException("Invalid restore notification.");
                    if (notification.Type == WatchProtocol.SessionEvent)
                    {
                        if (notification.State != WatchProtocol.ServerConnected)
                            throw new ZooKeeperConnectionException(
                                notification.State == (int)ZooKeeperErrorCode.SessionExpired
                                    ? ZooKeeperConnectionFailure.SessionRejected
                                    : ZooKeeperConnectionFailure.Unreachable,
                                transport.Endpoint
                            );
                    }
                    else
                    {
                        if (notification.State != WatchProtocol.ServerConnected)
                            throw new InvalidDataException("Invalid restore event state.");
                        lock (_gate)
                        {
                            if (!_watches.Dispatch(notification))
                            {
                                FaultOverflow();
                                throw new OperationCanceledException(_stop.Token);
                            }
                        }
                    }
                    _lastInbound = _time.GetTimestamp();
                    continue;
                }
                var reply = ReplyCodec.Decode(
                    packet,
                    WatchProtocol.ResetXid,
                    translated,
                    _options.Limits
                );
                _lastInbound = _time.GetTimestamp();
                if (reply.Header.Error != WatchProtocol.Success)
                    throw new ZooKeeperConnectionException(
                        reply.Header.Error
                                is (int)ZooKeeperErrorCode.ConnectionLoss
                                    or (int)ZooKeeperErrorCode.SessionMoved
                                ? ZooKeeperConnectionFailure.Unreachable
                            : reply.Header.Error == (int)ZooKeeperErrorCode.SessionExpired
                                ? ZooKeeperConnectionFailure.SessionRejected
                            : ZooKeeperConnectionFailure.Protocol,
                        transport.Endpoint,
                        reply.Header.Error
                    );
                break;
            }
        }
        _watches.Restored();
    }
}
