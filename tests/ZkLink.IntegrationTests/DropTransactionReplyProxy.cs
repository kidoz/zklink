using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ZkLink.IntegrationTests;

// Sequential framed forwarding permits exactly one injected response loss, then a same-session
// reconnect. No packet content is logged; secret-bearing handshake/auth buffers are cleared.
internal sealed class DropTransactionReplyProxy : IAsyncDisposable
{
    private const int MaxFrame = 1_048_575;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop;
    private readonly Task _run;
    private readonly Uri _target;
    private readonly TaskCompletionSource _dropped = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _forwardedTransactions;
    internal int ForwardedTransactions => Volatile.Read(ref _forwardedTransactions);
    internal Task Dropped => _dropped.Task;
    internal string Endpoint { get; }

    internal DropTransactionReplyProxy(string connectionString, CancellationToken token)
    {
        _target = new Uri("tcp://" + connectionString.Split(',')[0]);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _stop.CancelAfter(TimeSpan.FromSeconds(30));
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
                using TcpClient client = await _listener
                    .AcceptTcpClientAsync(_stop.Token)
                    .ConfigureAwait(false);
                using var upstream = new TcpClient();
                await upstream
                    .ConnectAsync(_target.Host, _target.Port < 0 ? 2181 : _target.Port, _stop.Token)
                    .ConfigureAwait(false);
                client.NoDelay = true;
                upstream.NoDelay = true;
                await ForwardConnectionAsync(client.GetStream(), upstream.GetStream(), _stop.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (IOException) when (_stop.IsCancellationRequested) { }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task ForwardConnectionAsync(
        Stream client,
        Stream upstream,
        CancellationToken token
    )
    {
        bool handshake = true;
        while (true)
        {
            byte[]? request = await ReadFrameAsync(client, token).ConfigureAwait(false);
            if (request is null)
                return;
            byte[]? response = null;
            try
            {
                int opcode = handshake ? 0 : BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(8));
                handshake = false;
                if (opcode == 14)
                    Interlocked.Increment(ref _forwardedTransactions);
                await upstream.WriteAsync(request, token).ConfigureAwait(false);
                response =
                    await ReadFrameAsync(upstream, token).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("Upstream omitted a test response.");
                if (opcode == 14 && !_dropped.Task.IsCompleted)
                {
                    // A real server response exists. Do not expose it to the client.
                    _dropped.TrySetResult();
                    return;
                }
                await client.WriteAsync(response, token).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(request);
                if (response is not null)
                    CryptographicOperations.ZeroMemory(response);
            }
        }
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        int read = await stream.ReadAsync(prefix, token).ConfigureAwait(false);
        if (read == 0)
            return null;
        await stream.ReadExactlyAsync(prefix.AsMemory(read), token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is < 8 or > MaxFrame)
            throw new InvalidDataException("Invalid proxy frame bound.");
        var frame = new byte[checked(4 + length)];
        prefix.CopyTo(frame, 0);
        try
        {
            await stream.ReadExactlyAsync(frame.AsMemory(4), token).ConfigureAwait(false);
            return frame;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(frame);
            throw;
        }
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
            _listener.Dispose();
            _stop.Dispose();
        }
    }
}
