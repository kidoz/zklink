using Microsoft.Extensions.Hosting;

namespace ZkLink.Extensions.DependencyInjection;

internal sealed class ZkLinkConnectionHost(
    ZooKeeperClientOptions options,
    IZkLinkConnectionFactory connectionFactory
) : IHostedService, IAsyncDisposable
{
    private IZkLinkConnection? _connection;

    public ZooKeeperClient Client =>
        (
            _connection
            ?? throw new InvalidOperationException(
                "The ZkLink client is available only after host startup and before host shutdown."
            )
        ).Client;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var connection = await connectionFactory
            .ConnectAsync(options, cancellationToken)
            .ConfigureAwait(false);
        if (Interlocked.CompareExchange(ref _connection, connection, null) is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("The ZkLink host lifetime has already started.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            try
            {
                await connection.CloseAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
