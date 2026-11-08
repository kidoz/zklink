using System.Net;
using System.Net.Sockets;

namespace ZkLink.FaultTests;

// A fixture-owned, single-client byte relay. It never decodes, retains or logs credentials.
// Its lifetime follows fixture disposal rather than the short fault-proxy deadline.
internal sealed class LoopbackFixtureRelay : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop;
    private readonly Task _run;
    private readonly IPEndPoint _target;
    private Task? _dispose;
    internal string Endpoint { get; }

    internal LoopbackFixtureRelay(string endpoint, CancellationToken token)
    {
        if (!IPEndPoint.TryParse(endpoint, out var target) || target.Port == 0)
            throw new ArgumentException(
                "A literal fixture endpoint is required.",
                nameof(endpoint)
            );
        _target = target;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _listener.Start(1);
        Endpoint = $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _run = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                using var client = await _listener
                    .AcceptTcpClientAsync(_stop.Token)
                    .ConfigureAwait(false);
                using var upstream = new TcpClient(_target.AddressFamily);
                await upstream.ConnectAsync(_target, _stop.Token).ConfigureAwait(false);
                client.NoDelay = true;
                upstream.NoDelay = true;
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                Task outgoing = client
                    .GetStream()
                    .CopyToAsync(upstream.GetStream(), connection.Token);
                Task incoming = upstream
                    .GetStream()
                    .CopyToAsync(client.GetStream(), connection.Token);
                await Task.WhenAny(outgoing, incoming).ConfigureAwait(false);
                await connection.CancelAsync().ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(outgoing, incoming).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (connection.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync() => new(_dispose ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _run.ConfigureAwait(false);
        }
        finally
        {
            _listener.Dispose();
            _stop.Dispose();
        }
    }
}
