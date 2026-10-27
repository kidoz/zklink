namespace ZkLink.FaultTests;

// Keep the underlying read as well as its guard so abort can cancel and observe
// it before disposing an async iterator with MoveNextAsync still in progress.
internal sealed class EventProbe<T> : IAsyncDisposable
{
    private static readonly TimeSpan ReadGuard = TimeSpan.FromSeconds(15);
    private readonly CancellationTokenSource _stop;
    private readonly IAsyncEnumerator<T> _reader;
    private readonly TimeSpan _readGuard;
    private Task<bool>? _read;
    private Task<bool>? _bounded;
    internal T Current => _reader.Current;

    internal EventProbe(
        IAsyncEnumerable<T> source,
        CancellationToken token,
        TimeSpan? readGuard = null
    )
    {
        _readGuard = readGuard ?? ReadGuard;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _reader = source.GetAsyncEnumerator(_stop.Token);
    }

    internal Task<bool> MoveNextAsync()
    {
        if (_read is { IsCompleted: false })
        {
            throw new InvalidOperationException("Only one event read may be outstanding.");
        }
        _read = _reader.MoveNextAsync().AsTask();
        return _bounded = _read.WaitAsync(_readGuard, _stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_read is not null && _bounded is not null)
            {
                await Task.WhenAll(_read, _bounded).ConfigureAwait(false);
            }
        }
        catch (Exception error)
            when (error
                    is OperationCanceledException
                        or TimeoutException
                        or ZooKeeperSessionException
                        or ZooKeeperEventOverflowException
            ) { }
        finally
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
            _stop.Dispose();
        }
    }
}
