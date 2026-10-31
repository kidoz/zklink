namespace ZkLink.FaultTests;

internal enum ProxyConnectionEventKind
{
    Accepted,
    UpstreamConnected,
    UpstreamFailed,
    EarlyDisconnect,
    HandshakeRead,
    EndOfStream,
    TransportError,
    ForwardingEnded,
    ReadyCheckpoint,
    RestoreCheckpoint,
    StopRequested,
}

internal sealed record ProxyConnectionEvent(
    DateTimeOffset ObservedAtUtc,
    int Attempt,
    ProxyConnectionEventKind Kind,
    bool? Incoming,
    int? SocketError
)
{
    internal string ToJson() =>
        FormattableString.Invariant(
            $"{{\"observed_at_utc\":\"{ObservedAtUtc:O}\",\"attempt\":{Attempt},\"kind\":\"{Kind}\",\"incoming\":{(Incoming is null ? "null" : Incoming.Value ? "true" : "false")},\"socket_error\":{(SocketError?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")}}}"
        );
}

internal sealed record ProxyConnectionSnapshot(
    int Attempts,
    int DroppedEvents,
    IReadOnlyList<ProxyConnectionEvent> Events
)
{
    internal string ToJson() =>
        FormattableString.Invariant(
            $"{{\"attempts\":{Attempts},\"dropped_events\":{DroppedEvents},\"events\":[{string.Join(",", Events.Select(static item => item.ToJson()))}]}}"
        );
}

internal sealed record SoakConnectionDiagnostics(
    int SourceReadyAttempts,
    int RecoveryRestoreAttempts,
    ProxyConnectionSnapshot? Source,
    ProxyConnectionSnapshot? Recovery
)
{
    internal string ToJson() =>
        FormattableString.Invariant(
            $"{{\"source_ready_attempts\":{SourceReadyAttempts},\"recovery_restore_attempts\":{RecoveryRestoreAttempts},\"source\":{Source?.ToJson() ?? "null"},\"recovery\":{Recovery?.ToJson() ?? "null"}}}"
        );
}
