namespace ZkLink.Protocol.Tests.Sessions;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    internal async Task WaitForTimerAsync(TimeSpan absoluteDue, CancellationToken token)
    {
        while (true)
        {
            Task change;
            lock (_gate)
            {
                if (_timers.Any(timer => timer.Next == absoluteDue.Ticks))
                {
                    return;
                }
                change = _changed.Task;
            }
            await change.WaitAsync(token).ConfigureAwait(false);
        }
    }

    internal void Advance(TimeSpan amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);
        var target = checked(GetTimestamp() + amount.Ticks);
        while (true)
        {
            ManualTimer? due;
            lock (_gate)
            {
                due = _timers.Where(timer => timer.Next <= target).MinBy(timer => timer.Next);
                if (due is null)
                {
                    _ticks = target;
                    return;
                }
                _ticks = due.Next;
                due.Next = due.Period > 0 ? checked(due.Next + due.Period) : long.MaxValue;
            }
            due.Callback(due.State);
        }
    }

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state
    ) : ITimer
    {
        internal TimerCallback Callback { get; } = callback;
        internal object? State { get; } = state;
        internal long Next = long.MaxValue;
        internal long Period;
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (_disposed)
                {
                    return false;
                }
                Next =
                    dueTime == Timeout.InfiniteTimeSpan
                        ? long.MaxValue
                        : checked(owner._ticks + dueTime.Ticks);
                Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                if (!owner._timers.Contains(this))
                {
                    owner._timers.Add(this);
                }
                owner._changed.TrySetResult();
                owner._changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _disposed = true;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
