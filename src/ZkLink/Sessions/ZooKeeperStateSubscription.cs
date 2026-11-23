using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ZkLink.Sessions;

namespace ZkLink;

/// <summary>An atomic initial session snapshot followed by ordered transitions; one consumer per subscription.</summary>
public sealed class ZooKeeperStateSubscription : IAsyncDisposable
{
    private readonly StateHub _owner;
    private int _started;

    internal ZooKeeperStateSubscription(StateHub owner, int capacity)
    {
        this._owner = owner;
        Queue = Channel.CreateBounded<ZooKeeperSessionSnapshot>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                AllowSynchronousContinuations = false,
            }
        );
    }

    internal Channel<ZooKeeperSessionSnapshot> Queue { get; }

    /// <summary>Reads the initial snapshot and transitions. Buffered snapshots are delivered before a subscription overflow or terminal session error.</summary>
    /// <exception cref="InvalidOperationException">This subscription already has a consumer.</exception>
    /// <exception cref="ZooKeeperEventOverflowException">The subscription queue overflowed; buffered snapshots were delivered first.</exception>
    /// <exception cref="ZooKeeperSessionException">The session ended after buffered snapshots were delivered.</exception>
    /// <exception cref="ZooKeeperConnectionException">The session ended as Faulted after an endpoint attempt failure; buffered snapshots were delivered first.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled the enumeration.</exception>
    public async IAsyncEnumerable<ZooKeeperSessionSnapshot> ReadStatesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("A state subscription has one consumer.");
        }
        try
        {
            while (await Queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_owner.TryRead(this, out var value))
                {
                    yield return value!;
                }
            }
        }
        finally
        {
            _owner.Remove(this);
        }
    }

    /// <summary>Releases queued state events and removes this subscription locally.</summary>
    public ValueTask DisposeAsync()
    {
        _owner.Remove(this);
        return ValueTask.CompletedTask;
    }
}
