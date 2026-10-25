using System.Net;
using System.Net.Sockets;

namespace ZkLink.Protocol.Tests.Peers;

// One bounded loopback connection. The caller supplies the complete server script.
internal sealed class FakeZooKeeperServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation;
    private readonly Task completion;

    internal FakeZooKeeperServer(
        Func<Stream, CancellationToken, Task> script,
        CancellationToken cancellationToken
    )
    {
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        listener.Start(1);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        completion = RunAsync(script);
    }

    internal int Port { get; }
    internal Task Completion => completion;

    private async Task RunAsync(Func<Stream, CancellationToken, Task> script)
    {
        try
        {
            using TcpClient client = await listener
                .AcceptTcpClientAsync(cancellation.Token)
                .ConfigureAwait(false);
            client.NoDelay = true;
            await script(client.GetStream(), cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (SocketException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            listener.Dispose();
            cancellation.Dispose();
        }
    }
}
