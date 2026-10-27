using System.Security.Cryptography;
using ZkLink.Configuration;
using ZkLink.Diagnostics;
using ZkLink.Protocol;
using ZkLink.Transport;
using ZkLink.Watches;

namespace ZkLink.Sessions;

internal sealed class SessionEstablisher(
    SessionManager owner,
    TransportConnection transport,
    ClientOptionsSnapshot options,
    TimeProvider time,
    long sessionId,
    long lastZxid,
    byte[] password
)
{
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var packet = new byte[45];
        try
        {
            var length =
                sessionId == 0
                    ? HandshakeCodec.EncodeNew(
                        packet,
                        checked((int)options.Settings.SessionTimeout.TotalMilliseconds),
                        options.Limits
                    )
                    : HandshakeCodec.Encode(
                        packet,
                        checked((int)options.Settings.SessionTimeout.TotalMilliseconds),
                        sessionId,
                        lastZxid,
                        password,
                        options.Limits
                    );
            await transport
                .Writer.WriteAsync(packet.AsMemory(0, length), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(packet);
        }
        var response =
            await transport.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new EndOfStreamException();
        TimeSpan negotiated;
        try
        {
            using var handshake = HandshakeCodec.Decode(response, options.Limits);
            if (!handshake.Accepted)
            {
                throw new ZooKeeperConnectionException(
                    ZooKeeperConnectionFailure.SessionRejected,
                    transport.Endpoint
                );
            }
            negotiated = owner.AcceptHandshake(handshake);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(response);
        }
        var authBudget = negotiated - owner.InboundSilence;
        if (authBudget <= TimeSpan.Zero)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        using var deadline = new CancellationTokenSource(authBudget, time);
        using var authentication = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token
        );
        await AuthenticateAsync(authentication.Token).ConfigureAwait(false);
        owner.BeginWatchRestoration();
        await RestoreWatchesAsync(authentication.Token).ConfigureAwait(false);
    }

    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        var authSpan = ZooKeeperTelemetry.Begin(time, TelemetryKind.Authenticate);
        try
        {
            foreach (var credentials in options.Authentication)
            {
                var request = new AuthRequest("digest", credentials);
                var encoded = new byte[RequestCodec.Measure(-4, request, options.Limits)];
                try
                {
                    RequestCodec.Encode(encoded, -4, request, options.Limits);
                    await transport
                        .Writer.WriteAsync(encoded, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encoded);
                }
                while (true)
                {
                    var reply =
                        await transport.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new EndOfStreamException();
                    var reader = new JuteReader(reply, options.Limits);
                    var header = reader.ReadReplyHeader();
                    if (header.Xid == -1)
                    {
                        var notification = ReplyCodec.DecodeNotification(reply, options.Limits);
                        if (notification.Type == -1 && notification.State == -112)
                        {
                            throw new ZooKeeperConnectionException(
                                ZooKeeperConnectionFailure.SessionRejected,
                                transport.Endpoint
                            );
                        }
                        if (notification.Type == -1 && notification.State == 4)
                        {
                            throw new ZooKeeperConnectionException(
                                ZooKeeperConnectionFailure.Authentication,
                                transport.Endpoint
                            );
                        }
                        if (notification.Type != -1 || notification.State != 3)
                        {
                            throw new InvalidDataException(
                                "Unexpected authentication notification."
                            );
                        }
                        owner.EstablishmentInbound();
                        continue;
                    }
                    var acknowledgment = ReplyCodec.Decode(reply, -4, request, options.Limits);
                    owner.EstablishmentInbound();
                    if (acknowledgment.Header.Error == -112)
                    {
                        throw new ZooKeeperConnectionException(
                            ZooKeeperConnectionFailure.SessionRejected,
                            transport.Endpoint,
                            acknowledgment.Header.Error
                        );
                    }
                    if (acknowledgment.Header.Error is -4 or -7 or -12 or -118 or -122)
                    {
                        throw new ZooKeeperConnectionException(
                            ZooKeeperConnectionFailure.Unreachable,
                            transport.Endpoint,
                            acknowledgment.Header.Error
                        );
                    }
                    if (acknowledgment.Header.Error != 0)
                    {
                        throw new ZooKeeperConnectionException(
                            ZooKeeperConnectionFailure.Authentication,
                            transport.Endpoint,
                            acknowledgment.Header.Error
                        );
                    }
                    break;
                }
            }
        }
        catch (Exception error)
        {
            authSpan.Complete(error);
            throw;
        }
        authSpan.Complete();
    }

    private Task RestoreWatchesAsync(CancellationToken token)
    {
        var span = ZooKeeperTelemetry.Begin(time, TelemetryKind.Restore);
        return ZooKeeperTelemetry.ObserveAsync(RestoreWatchesCoreAsync(token), span);
    }

    private async Task RestoreWatchesCoreAsync(CancellationToken token)
    {
        var batches = owner.WatchRestoration();
        foreach (var batch in batches)
        {
            var translated = NamespaceTranslator.ToServer(batch, options.Paths, options.Limits);
            var bytes = new byte[
                RequestCodec.Measure(WatchProtocol.ResetXid, translated, options.Limits)
            ];
            try
            {
                RequestCodec.Encode(bytes, WatchProtocol.ResetXid, translated, options.Limits);
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
                var input = new JuteReader(packet, options.Limits);
                var header = input.ReadReplyHeader();
                if (header.Xid == WatchProtocol.NotificationXid)
                {
                    var notification = ReplyCodec.DecodeNotification(packet, options.Limits);
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
                        owner.DispatchRestoreNotification(notification);
                    }
                    owner.EstablishmentInbound();
                    continue;
                }
                var reply = ReplyCodec.Decode(
                    packet,
                    WatchProtocol.ResetXid,
                    translated,
                    options.Limits
                );
                owner.EstablishmentInbound();
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
