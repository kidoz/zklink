using ZkLink.Diagnostics;

namespace ZkLink.Sessions;

internal sealed class StateHub(
    object gate,
    int limit,
    ZooKeeperSessionSnapshot initial,
    TelemetryState telemetry
)
{
    private readonly HashSet<ZooKeeperStateSubscription> _subscriptions = [];
    private ZooKeeperSessionSnapshot _current = initial;
    private bool _completed;
    private Exception? _completionError;
    internal ZooKeeperSessionSnapshot Current => Volatile.Read(ref _current);

    internal ZooKeeperStateSubscription Subscribe()
    {
        lock (gate)
        {
            if (_subscriptions.Count >= limit)
            {
                if (_completed)
                {
                    throw new ZooKeeperSessionException(_current.State, _current.ErrorCode);
                }
                throw new ZooKeeperEventOverflowException(_current.State);
            }
            var subscription = new ZooKeeperStateSubscription(this, limit);
            _subscriptions.Add(subscription);
            subscription.Queue.Writer.TryWrite(_current);
            telemetry.BacklogDelta(1);
            if (_completed)
            {
                subscription.Queue.Writer.TryComplete(_completionError);
            }
            return subscription;
        }
    }

    internal void Publish(ZooKeeperSessionSnapshot value, bool notify = true)
    {
        lock (gate)
        {
            Volatile.Write(ref _current, value);
            if (!notify || _completed)
            {
                return;
            }
            List<ZooKeeperStateSubscription>? overflowed = null;
            foreach (var subscription in _subscriptions)
            {
                if (!subscription.Queue.Writer.TryWrite(value))
                {
                    subscription.Queue.Writer.TryComplete(
                        new ZooKeeperEventOverflowException(value.State)
                    );
                    (overflowed ??= []).Add(subscription);
                    ZooKeeperTelemetry.Overflow();
                }
            }
            if (overflowed is not null)
            {
                foreach (var subscription in overflowed)
                {
                    // Release the client's ownership. The caller may still drain the buffered prefix.
                    _subscriptions.Remove(subscription);
                    telemetry.BacklogDelta(-subscription.Queue.Reader.Count);
                }
            }
            telemetry.BacklogDelta(_subscriptions.Count);
        }
    }

    internal void Finish(ZooKeeperSessionSnapshot value, Exception? error)
    {
        lock (gate)
        {
            Volatile.Write(ref _current, value);
            _completed = true;
            _completionError = error;
            foreach (var subscription in _subscriptions)
            {
                subscription.Queue.Writer.TryComplete(error);
            }
        }
    }

    internal bool TryRead(
        ZooKeeperStateSubscription subscription,
        out ZooKeeperSessionSnapshot? value
    )
    {
        lock (gate)
        {
            if (!subscription.Queue.Reader.TryRead(out value))
            {
                return false;
            }
            if (_subscriptions.Contains(subscription))
            {
                telemetry.BacklogDelta(-1);
            }
            return true;
        }
    }

    internal void Remove(ZooKeeperStateSubscription subscription)
    {
        lock (gate)
        {
            if (_subscriptions.Remove(subscription))
            {
                telemetry.BacklogDelta(-subscription.Queue.Reader.Count);
            }
            while (subscription.Queue.Reader.TryRead(out _)) { }
            subscription.Queue.Writer.TryComplete();
        }
    }
}
