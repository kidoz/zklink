using System.Threading.Channels;

namespace ZkLink.Sessions;

internal sealed class AsyncPulse
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { SingleReader = true, AllowSynchronousContinuations = false }
    );

    internal void Set() => _channel.Writer.TryWrite(0);

    internal async Task WaitAsync(CancellationToken cancellationToken) =>
        _ = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    internal async Task WaitAsync(
        TimeSpan delay,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signal = _channel.Reader.ReadAsync(stop.Token).AsTask();
        var rounded = TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(delay.TotalMilliseconds)));
        var timer = Task.Delay(rounded, timeProvider, stop.Token);
        var winner = await Task.WhenAny(signal, timer).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await winner.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        try
        {
            await signal.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        try
        {
            await timer.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
