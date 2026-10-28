using Microsoft.Extensions.Hosting;

namespace ZkLink.Extensions.DependencyInjection;

internal sealed class ZkLinkConnectionHost(
    ZooKeeperClientOptions options,
    IZkLinkConnectionFactory connectionFactory
) : IHostedService, IAsyncDisposable
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2213",
        Justification = "SemaphoreSlim needs disposal only after AvailableWaitHandle is used; retaining it keeps concurrent and repeated disposal safe."
    )]
    private readonly SemaphoreSlim _lifetimeGate = new(1, 1);
    private IZkLinkConnection? _connection;
    private LifetimeState _state;

    public ZooKeeperClient Client =>
        (
            Volatile.Read(ref _connection)
            ?? throw new InvalidOperationException(
                "The ZkLink client is available only after host startup and before host shutdown."
            )
        ).Client;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifetimeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != LifetimeState.Created)
            {
                throw new InvalidOperationException(
                    "The ZkLink host lifetime has already started."
                );
            }

            var connection = await connectionFactory
                .ConnectAsync(options, cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _connection, connection);
            _state = LifetimeState.Started;
        }
        finally
        {
            _lifetimeGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifetimeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_state is LifetimeState.Stopped or LifetimeState.Disposed)
            {
                return;
            }

            var connection = Interlocked.Exchange(ref _connection, null);
            _state = LifetimeState.Stopped;
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
        finally
        {
            _lifetimeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetimeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state == LifetimeState.Disposed)
            {
                return;
            }

            var connection = Interlocked.Exchange(ref _connection, null);
            _state = LifetimeState.Disposed;
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lifetimeGate.Release();
        }
    }

    private enum LifetimeState
    {
        Created,
        Started,
        Stopped,
        Disposed,
    }
}
