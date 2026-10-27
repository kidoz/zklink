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

    /// <summary>Reads the initial snapshot and transitions. Buffered snapshots precede any terminal session failure; cancellation/disposal ends this subscription only.</summary>
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
                while (_owner.TryRead(this, out var value))
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
