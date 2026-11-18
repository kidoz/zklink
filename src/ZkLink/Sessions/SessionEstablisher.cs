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
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2025",
        Justification = "The liveness task is cancelled and awaited in finally before its source is disposed."
    )]
    internal async Task<long> RunAsync(
        Action handshakeAccepted,
        CancellationToken cancellationToken
    )
    {
        var packet = FrameWriter.AllocateFrame(45, options.Limits);
        try
        {
            var length =
                sessionId == 0
                    ? HandshakeCodec.EncodeNew(
                        packet.AsSpan(sizeof(int)),
                        checked((int)options.Settings.SessionTimeout.TotalMilliseconds),
                        options.Limits
                    )
                    : HandshakeCodec.Encode(
                        packet.AsSpan(sizeof(int)),
                        checked((int)options.Settings.SessionTimeout.TotalMilliseconds),
                        sessionId,
                        lastZxid,
                        password,
                        options.Limits
                    );
            await transport
                .Writer.WriteFrameAsync(packet.AsMemory(0, sizeof(int) + length), cancellationToken)
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
        handshakeAccepted();
        using var establishment = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var liveness = MonitorInboundAsync(negotiated, establishment);
        try
        {
            await AuthenticateAsync(establishment.Token).ConfigureAwait(false);
            owner.BeginWatchRestoration();
            return await RestoreWatchesAsync(establishment.Token).ConfigureAwait(false);
        }
        finally
        {
            await establishment.CancelAsync().ConfigureAwait(false);
            await liveness.ConfigureAwait(false);
        }
    }

    private async Task MonitorInboundAsync(
        TimeSpan negotiated,
        CancellationTokenSource establishment
    )
    {
        try
        {
            while (true)
            {
                var remaining = TimeSpan.FromTicks(negotiated.Ticks * 2 / 3) - owner.InboundSilence;
                if (remaining <= TimeSpan.Zero)
                {
                    await establishment.CancelAsync().ConfigureAwait(false);
                    return;
                }
                await Task.Delay(remaining, time, establishment.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (establishment.IsCancellationRequested) { }
    }

    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        var authSpan = ZooKeeperTelemetry.Begin(time, TelemetryKind.Authenticate);
        try
        {
            foreach (var credentials in options.Authentication)
            {
                var request = new AuthRequest("digest", credentials);
                var encoded = FrameWriter.AllocateFrame(
                    RequestCodec.Measure(Xids.Auth, request, options.Limits),
                    options.Limits
                );
                try
                {
                    RequestCodec.Encode(
                        encoded.AsSpan(sizeof(int)),
                        Xids.Auth,
                        request,
                        options.Limits
                    );
                    await transport
                        .Writer.WriteFrameAsync(encoded, cancellationToken)
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
                    if (header.Xid == Xids.Notification)
                    {
                        var notification = ReplyCodec.DecodeNotification(reply, options.Limits);
                        if (notification.Type != WatchProtocol.SessionEvent)
                        {
                            throw new InvalidDataException(
                                "Unexpected authentication notification."
                            );
                        }
                        CheckSessionNotification(notification.State);
                        owner.EstablishmentInbound();
                        continue;
                    }
                    var acknowledgment = ReplyCodec.DecodeForValidatedRequest(
                        reply,
                        Xids.Auth,
                        request,
                        options.Limits
                    );
                    owner.EstablishmentInbound();
                    CheckAcknowledgment(acknowledgment.Header.Error, ReplyPhase.Authentication);
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

    private Task<long> RestoreWatchesAsync(CancellationToken token)
    {
        var span = ZooKeeperTelemetry.Begin(time, TelemetryKind.Restore);
        return ZooKeeperTelemetry.ObserveAsync(RestoreWatchesCoreAsync(token), span);
    }

    private async Task<long> RestoreWatchesCoreAsync(CancellationToken token)
    {
        var restoredZxid = lastZxid;
        var batches = owner.WatchRestoration();
        foreach (var batch in batches)
        {
            var translated = NamespaceTranslator.ToServer(batch, options.Paths, options.Limits);
            var bytes = FrameWriter.AllocateFrame(
                RequestCodec.Measure(WatchProtocol.ResetXid, translated, options.Limits),
                options.Limits
            );
            try
            {
                RequestCodec.Encode(
                    bytes.AsSpan(sizeof(int)),
                    WatchProtocol.ResetXid,
                    translated,
                    options.Limits
                );
                await transport.Writer.WriteFrameAsync(bytes, token).ConfigureAwait(false);
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
                    {
                        throw new InvalidDataException("Invalid restore notification.");
                    }
                    if (notification.Type == WatchProtocol.SessionEvent)
                    {
                        CheckSessionNotification(notification.State);
                    }
                    else
                    {
                        if (notification.State != WatchProtocol.ServerConnected)
                        {
                            throw new InvalidDataException("Invalid restore event state.");
                        }
                        owner.DispatchRestoreNotification(notification);
                    }
                    owner.EstablishmentInbound();
                    continue;
                }
                var reply = ReplyCodec.DecodeForValidatedRequest(
                    packet,
                    WatchProtocol.ResetXid,
                    translated,
                    options.Limits
                );
                owner.EstablishmentInbound();
                CheckAcknowledgment(reply.Header.Error, ReplyPhase.WatchRestore);
                if (reply.Header.Zxid > 0)
                {
                    restoredZxid = reply.Header.Zxid;
                }
                break;
            }
        }
        return restoredZxid;
    }

    private void CheckSessionNotification(int state)
    {
        if (ErrorClassifier.NotificationImpact(state) is { } failure)
        {
            throw new ZooKeeperConnectionException(
                failure,
                transport.Endpoint,
                state switch
                {
                    WatchProtocol.ServerExpired => (int)ZooKeeperErrorCode.SessionExpired,
                    WatchProtocol.ServerAuthFailed => (int)ZooKeeperErrorCode.AuthFailed,
                    _ => null,
                }
            );
        }
    }

    private void CheckAcknowledgment(int errorCode, ReplyPhase phase)
    {
        if (ErrorClassifier.ConnectionImpact(errorCode, phase) is { } failure)
        {
            throw new ZooKeeperConnectionException(failure, transport.Endpoint, errorCode);
        }
    }
}
