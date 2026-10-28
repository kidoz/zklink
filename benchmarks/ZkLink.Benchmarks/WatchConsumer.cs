using System.Diagnostics;

namespace ZkLink.Benchmarks;

internal sealed class WatchConsumer : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly IAsyncEnumerator<ZooKeeperWatchEvent> _reader;
    private Task<WatchReceipt?>? _pending;
    internal ZooKeeperWatch Watch { get; }
    internal LatencyHistogram Latency { get; } = new();
    internal long Deliveries;

    internal WatchConsumer(ZooKeeperWatch watch, CancellationToken token)
    {
        Watch = watch;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _reader = watch.ReadEventsAsync(_stop.Token).GetAsyncEnumerator(_stop.Token);
    }

    internal Task<WatchReceipt?> ReadAsync()
    {
        if (_pending is { IsCompleted: false })
        {
            throw new InvalidOperationException("Only one watch read may be pending.");
        }
        return _pending = ReceiveAsync();
    }

    private async Task<WatchReceipt?> ReceiveAsync()
    {
        bool available = await _reader.MoveNextAsync().ConfigureAwait(false);
        long observed = Stopwatch.GetTimestamp();
        return available ? new(_reader.Current, observed) : null;
    }

    internal static async Task ObserveAsync(Task? pending)
    {
        if (pending is null)
        {
            return;
        }
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception error)
            when (error
                    is OperationCanceledException
                        or ZooKeeperSessionException
                        or ZooKeeperEventOverflowException
            ) { }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await ObserveAsync(_pending).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _reader.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await Watch.DisposeAsync().ConfigureAwait(false);
                _stop.Dispose();
            }
        }
    }
}

internal sealed record WatchReceipt(ZooKeeperWatchEvent Event, long ObservedAt);
