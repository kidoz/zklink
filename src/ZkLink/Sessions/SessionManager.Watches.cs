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

    internal (ZooKeeperWatch Watch, Task<WireReply> Reply) AddWatch(
        string path,
        PersistentWatchMode mode,
        CancellationToken token
    )
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        var logical =
            mode == PersistentWatchMode.Persistent
                ? ZooKeeperWatchMode.Persistent
                : ZooKeeperWatchMode.PersistentRecursive;
        var info = new ZooKeeperRequestInfo(ZooKeeperOperation.AddWatch, path: path);
        lock (_gate)
        {
            var intent = _watches.Begin(path, logical, info);
            try
            {
                return (
                    intent.Watch,
                    Scheduler.Submit(
                        new WatchRequest(ZooKeeperOperation.AddWatch, path, (int)mode),
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

    internal Task<WireReply> SubmitWatchRemoval(
        WatchRequest request,
        IRequestObserver observer,
        CancellationToken token
    ) =>
        Scheduler.Submit(
            request,
            new ZooKeeperRequestInfo(ZooKeeperOperation.RemoveWatches, path: request.Path),
            token,
            observer,
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
                                notification.State switch
                                {
                                    (int)ZooKeeperErrorCode.SessionExpired =>
                                        ZooKeeperConnectionFailure.SessionRejected,
                                    WatchProtocol.ServerAuthFailed =>
                                        ZooKeeperConnectionFailure.Authentication,
                                    WatchProtocol.ServerDisconnected =>
                                        ZooKeeperConnectionFailure.Unreachable,
                                    _ => ZooKeeperConnectionFailure.Protocol,
                                },
                                transport.Endpoint,
                                notification.State switch
                                {
                                    (int)ZooKeeperErrorCode.SessionExpired => (int)
                                        ZooKeeperErrorCode.SessionExpired,
                                    WatchProtocol.ServerAuthFailed => (int)
                                        ZooKeeperErrorCode.AuthFailed,
                                    _ => null,
                                }
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
                        reply.Header.Error switch
                        {
                            (int)ZooKeeperErrorCode.ConnectionLoss
                            or (int)ZooKeeperErrorCode.OperationTimeout
                            or (int)ZooKeeperErrorCode.UnknownSession
                            or (int)ZooKeeperErrorCode.SessionMoved
                            or (int)ZooKeeperErrorCode.RequestTimeout =>
                                ZooKeeperConnectionFailure.Unreachable,
                            (int)ZooKeeperErrorCode.SessionExpired =>
                                ZooKeeperConnectionFailure.SessionRejected,
                            (int)ZooKeeperErrorCode.AuthFailed
                            or (int)ZooKeeperErrorCode.SessionClosedRequiresSaslAuth =>
                                ZooKeeperConnectionFailure.Authentication,
                            _ => ZooKeeperConnectionFailure.Protocol,
                        },
                        transport.Endpoint,
                        reply.Header.Error
                    );
                break;
            }
        }
    }
}
