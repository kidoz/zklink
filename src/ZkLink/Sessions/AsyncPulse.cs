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

    internal async Task WaitAsync(TimeSpan delay, ITimer timer, CancellationToken cancellationToken)
    {
        var rounded = TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(delay.TotalMilliseconds)));
        timer.Change(rounded, Timeout.InfiniteTimeSpan);
        try
        {
            _ = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }
}
