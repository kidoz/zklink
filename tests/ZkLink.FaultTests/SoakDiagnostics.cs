namespace ZkLink.FaultTests;

// Capture only framing metadata, before allocating or reading the rejected body.
// The exception carries the original observation through delayed proxy disposal.
internal sealed record FaultProxyFrameFailure(
    DateTimeOffset ObservedAtUtc,
    bool Incoming,
    bool Handshake,
    int ConnectionAttempt,
    long FrameNumber,
    int Length,
    int MaximumLength
)
{
    internal string ToJson() =>
        FormattableString.Invariant(
            $"{{\"observed_at_utc\":\"{ObservedAtUtc:O}\",\"direction\":\"{(Incoming ? "incoming" : "outgoing")}\",\"handshake\":{(Handshake ? "true" : "false")},\"connection_attempt\":{ConnectionAttempt},\"frame_number\":{FrameNumber},\"length\":{Length},\"maximum_length\":{MaximumLength}}}"
        );
}

internal enum SoakWaitSite
{
    QuiescentCounters,
    CreateReplyWithheld,
    RestoreAcknowledgmentHeld,
    ConnectionReady,
    ReconnectGap,
    OneShotEvent,
    OneShotCompletion,
    PersistentEvent,
    ProxyConnectionsClosed,
}

internal static class SoakDiagnostics
{
    internal static async Task ObserveAsync(Task wait, SoakWaitSite site)
    {
        try
        {
            await wait.ConfigureAwait(true);
        }
        catch (TimeoutException failure)
        {
            failure.Data[typeof(SoakWaitSite)] = site;
            throw;
        }
    }

    internal static async Task<T> ObserveAsync<T>(Task<T> wait, SoakWaitSite site)
    {
        await ObserveAsync((Task)wait, site).ConfigureAwait(true);
        return await wait.ConfigureAwait(true);
    }
}
