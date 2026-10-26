using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ZkLink.IntegrationTests;

// Full-duplex bytes, including unsolicited notifications; only this test's connection is cut.
internal sealed class WatchReconnectProxy : IAsyncDisposable
{
    private const int BufferBytes = 16 * 1024;
    private const int DefaultPort = 2181;
    private readonly object _gate = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop;
    private readonly Task _run;
    private readonly Uri _target;
    private TaskCompletionSource _resume = Completed();
    private TcpClient? _client;
    private TcpClient? _upstream;
    internal string Endpoint { get; }

    internal WatchReconnectProxy(string connection, CancellationToken token)
    {
        _target = new Uri("tcp://" + connection.Split(',')[0]);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _stop.CancelAfter(TimeSpan.FromSeconds(45));
        _listener.Start(1);
        Endpoint = $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _run = RunAsync();
    }

    internal void DropAndPause()
    {
        lock (_gate)
        {
            _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _client?.Dispose();
            _upstream?.Dispose();
        }
    }

    internal void Resume()
    {
        lock (_gate)
            _resume.TrySetResult();
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                using TcpClient client = await _listener
                    .AcceptTcpClientAsync(_stop.Token)
                    .ConfigureAwait(false);
                Task ready;
                lock (_gate)
                    ready = _resume.Task;
                await ready.WaitAsync(_stop.Token).ConfigureAwait(false);
                using var upstream = new TcpClient();
                await upstream
                    .ConnectAsync(
                        _target.Host,
                        _target.Port < 0 ? DefaultPort : _target.Port,
                        _stop.Token
                    )
                    .ConfigureAwait(false);
                client.NoDelay = true;
                upstream.NoDelay = true;
                lock (_gate)
                {
                    _client = client;
                    _upstream = upstream;
                }
                try
                {
                    await ForwardAsync(client.GetStream(), upstream.GetStream())
                        .ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        _client = null;
                        _upstream = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task ForwardAsync(Stream client, Stream upstream)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var outgoing = PumpAsync(client, upstream, lifetime.Token);
        var incoming = PumpAsync(upstream, client, lifetime.Token);
        await Task.WhenAny(outgoing, incoming).ConfigureAwait(false);
        await lifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(outgoing, incoming).ConfigureAwait(false);
    }

    private static async Task PumpAsync(Stream source, Stream destination, CancellationToken token)
    {
        var bytes = new byte[BufferBytes];
        try
        {
            while (true)
            {
                int count = await source.ReadAsync(bytes, token).ConfigureAwait(false);
                if (count == 0)
                    return;
                await destination.WriteAsync(bytes.AsMemory(0, count), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static TaskCompletionSource Completed()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult();
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _run.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _client?.Dispose();
                _upstream?.Dispose();
            }
            _listener.Dispose();
            _stop.Dispose();
        }
    }
}
