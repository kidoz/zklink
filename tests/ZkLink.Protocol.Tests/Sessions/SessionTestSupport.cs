using System.Threading.Channels;
using ZkLink.Configuration;
using ZkLink.Requests;
using ZkLink.Sessions;

namespace ZkLink.Protocol.Tests.Sessions;

internal static class SessionTestSupport
{
    internal static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    internal static Task<ReceivedPacket> NextAsync(SessionPeer peer) =>
        peer
            .Requests.ReadAsync(TestContext.Current.CancellationToken)
            .AsTask()
            .WaitAsync(Guard, TestContext.Current.CancellationToken);

    internal static Task ReaderWaitingAsync(SessionPeer peer) =>
        peer.ReaderWaitingAsync(TestContext.Current.CancellationToken)
            .WaitAsync(Guard, TestContext.Current.CancellationToken);

    internal static Task<T> Bounded<T>(Task<T> task) =>
        task.WaitAsync(Guard, TestContext.Current.CancellationToken);

    internal static Task Bounded(Task task) =>
        task.WaitAsync(Guard, TestContext.Current.CancellationToken);

    internal static PendingRequest CreatePendingRequest(
        ZooKeeperRequestInfo info,
        ClientOptionsSnapshot options,
        object gate
    ) =>
        new(
            new RequestScheduler(gate, options, new ManualTimeProvider(), new AsyncPulse()),
            info,
            0,
            1,
            0,
            default
        );

    internal static async Task WaitForBytesReleasedAsync(ZooKeeperClient client)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        stop.CancelAfter(Guard);
        while (client.Manager.Scheduler.OwnedBytes != 0)
        {
            stop.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}

internal sealed class StateObserver : IAsyncDisposable
{
    private readonly ZooKeeperStateSubscription _subscription;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<ZooKeeperSessionSnapshot> _states =
        Channel.CreateBounded<ZooKeeperSessionSnapshot>(256);
    private readonly Task _reader;

    internal StateObserver(ZooKeeperClient client)
    {
        _subscription = client.SubscribeStateChanges();
        _reader = ReadAsync();
    }

    private async Task ReadAsync()
    {
        try
        {
            await foreach (
                var snapshot in _subscription.ReadStatesAsync(_stop.Token).ConfigureAwait(false)
            )
            {
                if (!_states.Writer.TryWrite(snapshot))
                {
                    throw new InvalidOperationException("Test observer budget exceeded.");
                }
            }
            _states.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            _states.Writer.TryComplete();
        }
        catch (ZooKeeperSessionException error)
        {
            _states.Writer.TryComplete(error);
        }
    }

    internal async Task<ZooKeeperSessionSnapshot> WaitAsync(ZooKeeperSessionState state)
    {
        while (true)
        {
            var value = await _states
                .Reader.ReadAsync(TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(SessionTestSupport.Guard, TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
            if (value.State == state)
            {
                return value;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _subscription.DisposeAsync().ConfigureAwait(false);
        await _reader.ConfigureAwait(false);
        _stop.Dispose();
    }
}
