namespace ZkLink.FaultTests;

internal sealed record SoakCleanupSnapshot(
    int ReturnedChildren,
    int StatChildren,
    int ChildVersion,
    long ParentZxid,
    int Attempts,
    int Leases,
    int WatchNodes,
    int WorkNodes,
    int OtherNodes
)
{
    internal static SoakCleanupSnapshot Capture(ZNodeChildren children)
    {
        int attempts = 0;
        int leases = 0;
        int watches = 0;
        int work = 0;
        foreach (string child in children.Children)
        {
            if (child.StartsWith("attempt-", StringComparison.Ordinal))
            {
                attempts++;
            }
            else if (child == "lease")
            {
                leases++;
            }
            else if (child is "once" or "persistent")
            {
                watches++;
            }
            else if (child.StartsWith("n-", StringComparison.Ordinal))
            {
                work++;
            }
        }
        return new(
            children.Children.Count,
            children.Stat.NumChildren,
            children.Stat.Cversion,
            children.Stat.Pzxid,
            attempts,
            leases,
            watches,
            work,
            children.Children.Count - attempts - leases - watches - work
        );
    }

    internal string ToJson() =>
        FormattableString.Invariant(
            $"{{\"returned_children\":{ReturnedChildren},\"stat_children\":{StatChildren},\"child_version\":{ChildVersion},\"parent_zxid\":{ParentZxid},\"attempts\":{Attempts},\"leases\":{Leases},\"watch_nodes\":{WatchNodes},\"work_nodes\":{WorkNodes},\"other_nodes\":{OtherNodes}}}"
        );
}

internal sealed record SoakCleanupFailure(
    DateTimeOffset ObservedAtUtc,
    int Depth,
    SoakCleanupSnapshot Before,
    SoakCleanupSnapshot? After
)
{
    internal string ToJson() =>
        FormattableString.Invariant(
            $"{{\"observed_at_utc\":\"{ObservedAtUtc:O}\",\"depth\":{Depth},\"before\":{Before.ToJson()},\"after\":{After?.ToJson() ?? "null"}}}"
        );
}

// Capture only framing metadata, before allocating or reading the rejected body.
// The exception carries the original observation through delayed proxy disposal.
internal sealed record FaultProxyFrameFailure(
    DateTimeOffset ObservedAtUtc,
    bool Incoming,
    bool Handshake,
    int ConnectionAttempt,
    long FrameNumber,
    int Length,
    int MaximumLength,
    ProxyConnectionIdentity? Connection = null,
    LoopbackPeerAttribution? PeerAttribution = null
)
{
    internal string ToJson() =>
        FormattableString.Invariant(
            $"{{\"observed_at_utc\":\"{ObservedAtUtc:O}\",\"direction\":\"{(Incoming ? "incoming" : "outgoing")}\",\"handshake\":{(Handshake ? "true" : "false")},\"connection_attempt\":{ConnectionAttempt},\"frame_number\":{FrameNumber},\"length\":{Length},\"maximum_length\":{MaximumLength},\"connection\":{Connection?.ToJson() ?? "null"},\"peer_attribution\":{PeerAttribution?.ToJson() ?? "null"}}}"
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
        catch (Exception failure) when (failure is TimeoutException or InvalidDataException)
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
