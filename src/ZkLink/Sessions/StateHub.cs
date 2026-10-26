using System.Threading.Channels;

namespace ZkLink.Sessions;

internal sealed class StateHub(object gate, EventBudget budget, ZooKeeperSessionSnapshot initial)
{
    private readonly HashSet<ZooKeeperStateSubscription> _subscriptions = [];
    private ZooKeeperSessionSnapshot _current = initial;
    private bool _completed;
    private Exception? _completionError;
    internal ZooKeeperSessionSnapshot Current => Volatile.Read(ref _current);

    // All mutation, including dequeue accounting, is linearized by the session gate.
    internal ZooKeeperStateSubscription? Subscribe()
    {
        lock (gate)
        {
            if (_subscriptions.Count >= budget.Limit || !budget.Reserve(1))
            {
                return null;
            }
            var subscription = new ZooKeeperStateSubscription(this, budget.Limit);
            _subscriptions.Add(subscription);
            subscription.Queue.Writer.TryWrite(_current);
            if (_completed)
            {
                subscription.Queue.Writer.TryComplete(_completionError);
            }
            return subscription;
        }
    }

    internal bool Publish(ZooKeeperSessionSnapshot value, bool notify = true)
    {
        lock (gate)
        {
            if (notify && !_completed && !budget.Reserve(_subscriptions.Count))
            {
                return false;
            }
            Volatile.Write(ref _current, value);
            if (notify && !_completed)
            {
                foreach (var subscription in _subscriptions)
                {
                    if (!subscription.Queue.Writer.TryWrite(value))
                    {
                        throw new InvalidOperationException("State budget invariant violated.");
                    }
                }
            }
            return true;
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
            if (subscription.Queue.Reader.TryRead(out value))
            {
                budget.Release(1);
                return true;
            }
            return false;
        }
    }

    internal void Remove(ZooKeeperStateSubscription subscription)
    {
        lock (gate)
        {
            if (!_subscriptions.Remove(subscription))
            {
                return;
            }
            while (subscription.Queue.Reader.TryRead(out _))
            {
                budget.Release(1);
            }
            subscription.Queue.Writer.TryComplete();
        }
    }
}
