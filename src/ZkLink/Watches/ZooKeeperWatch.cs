using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ZkLink.Watches;

namespace ZkLink;

/// <summary>An owned single-consumer one-shot watch; disposal suppresses local delivery immediately.</summary>
public sealed class ZooKeeperWatch : IAsyncDisposable
{
    private readonly WatchManager _owner;
    private int _started;

    internal ZooKeeperWatch(WatchManager owner, long id, string path, ZooKeeperWatchMode mode)
    {
        _owner = owner;
        RegistrationId = id;
        Path = path;
        Mode = mode;
    }

    internal Channel<ZooKeeperWatchEvent> Queue { get; } =
        Channel.CreateBounded<ZooKeeperWatchEvent>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                AllowSynchronousContinuations = false,
            }
        );
    internal bool Pending = true;
    internal bool Active;
    internal bool Suppressed;
    internal bool Absent;

    /// <summary>Gets a stable positive logical registration identity.</summary>
    public long RegistrationId { get; }

    /// <summary>Gets the client-relative watched path.</summary>
    public string Path { get; }

    /// <summary>Gets the logical registration mode.</summary>
    public ZooKeeperWatchMode Mode { get; }

    /// <summary>Reads at most one event; ending enumeration removes this logical watch locally.</summary>
    public async IAsyncEnumerable<ZooKeeperWatchEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A watch has one consumer.");
        try
        {
            while (await Queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_owner.TryRead(this, out var value))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return value!;
                }
            }
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Suppresses this handle immediately, optionally awaiting server removal; a failed wait never revives it.</summary>
    public Task<ZooKeeperWatchRemovalResult> RemoveAsync(
        WatchRemovalMode mode,
        CancellationToken cancellationToken = default
    ) => _owner.Remove(this, mode, cancellationToken);

    /// <summary>Suppresses delivery and schedules bounded best-effort cleanup without waiting for server acknowledgment.</summary>
    public ValueTask DisposeAsync()
    {
        _owner.Dispose(this);
        return ValueTask.CompletedTask;
    }
}
