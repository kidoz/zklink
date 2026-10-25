using System.Net;
using System.Net.Sockets;

namespace ZkLink.Protocol.Tests.Peers;

// One bounded loopback connection. The caller supplies the complete server script.
internal sealed class FakeZooKeeperServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancellation;
    private readonly Task _completion;

    internal FakeZooKeeperServer(
        Func<Stream, CancellationToken, Task> script,
        CancellationToken cancellationToken
    )
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        _listener.Start(1);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _completion = RunAsync(script);
    }

    internal int Port { get; }
    internal Task Completion => _completion;

    private async Task RunAsync(Func<Stream, CancellationToken, Task> script)
    {
        try
        {
            using var client = await _listener
                .AcceptTcpClientAsync(_cancellation.Token)
                .ConfigureAwait(false);
            client.NoDelay = true;
            await script(client.GetStream(), _cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (SocketException) when (_cancellation.IsCancellationRequested) { }
        finally
        {
            _listener.Dispose();
            _cancellation.Dispose();
        }
    }
}
