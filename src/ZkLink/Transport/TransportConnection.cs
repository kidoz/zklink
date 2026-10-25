using ZkLink.Protocol;

namespace ZkLink.Transport;

internal sealed class TransportConnection(
    Stream stream,
    ZooKeeperEndpoint endpoint,
    ProtocolLimits limits
) : IAsyncDisposable
{
    private int _disposed;
    internal Stream Stream { get; } = stream;
    internal ZooKeeperEndpoint Endpoint { get; } = endpoint;
    internal FrameReader Reader { get; } = new(stream, limits);
    internal FrameWriter Writer { get; } = new(stream, limits);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await Stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
