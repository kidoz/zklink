namespace ZkLink.Diagnostics;

// All per-owner mutation is serialized by the session gate. Collection reads only atomic totals.
internal sealed class TelemetryState
{
    private long _requests,
        _bytes,
        _tombstones,
        _watches,
        _backlog;
    private long? _disconnectedAt;
    private bool _disposed;

    internal void Requests(int count, int bytes, int tombstones)
    {
        if (_disposed)
            return;
        Update(ref _requests, count, ref ZooKeeperTelemetry.Requests);
        Update(ref _bytes, bytes, ref ZooKeeperTelemetry.Bytes);
        Update(ref _tombstones, tombstones, ref ZooKeeperTelemetry.Tombstones);
    }

    internal void WatchDelta(int delta)
    {
        if (!_disposed)
            Update(ref _watches, _watches + delta, ref ZooKeeperTelemetry.Watches);
    }

    internal void Backlog(int count)
    {
        if (!_disposed)
            Update(ref _backlog, count, ref ZooKeeperTelemetry.Backlog);
    }

    internal void State(ZooKeeperSessionState state, TimeProvider time)
    {
        if (_disposed)
            return;
        if (state == ZooKeeperSessionState.Disconnected && _disconnectedAt is null)
        {
            _disconnectedAt = time.GetTimestamp();
            Interlocked.Increment(ref ZooKeeperTelemetry.DisconnectedSessions);
        }
        if (
            _disconnectedAt is { } start
            && state
                is not (
                    ZooKeeperSessionState.Disconnected
                    or ZooKeeperSessionState.Connecting
                    or ZooKeeperSessionState.Authenticating
                    or ZooKeeperSessionState.Restoring
                )
        )
        {
            _disconnectedAt = null;
            Interlocked.Decrement(ref ZooKeeperTelemetry.DisconnectedSessions);
            ZooKeeperTelemetry.RecordDisconnected(time.GetElapsedTime(start));
        }
    }

    internal void Dispose(TimeProvider time)
    {
        if (_disposed)
            return;
        State(ZooKeeperSessionState.Closed, time);
        Requests(0, 0, 0);
        Update(ref _watches, 0, ref ZooKeeperTelemetry.Watches);
        Backlog(0);
        _disposed = true;
    }

    private static void Update(ref long owned, long value, ref long total)
    {
        long delta = value - owned;
        owned = value;
        if (delta != 0)
            Interlocked.Add(ref total, delta);
    }
}
