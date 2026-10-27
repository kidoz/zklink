using ZkLink;

namespace ZkLink.Samples.Basic;

/// <summary>Connects one session, runs the selected demonstrations, syncs and closes.</summary>
internal sealed class SampleSession(TextWriter output, TextWriter error)
{
    internal async Task<int> RunAsync(
        ZooKeeperClientOptions options,
        IReadOnlyList<ISampleStep> steps,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var client = await ZooKeeperClient
                .ConnectAsync(options, cancellationToken)
                .ConfigureAwait(false);
            await using var clientLifetime = client.ConfigureAwait(false);
            foreach (var step in steps)
            {
                var message = await step.RunAsync(client, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(message).ConfigureAwait(false);
            }

            await client.SyncAsync("/", cancellationToken).ConfigureAwait(false);
            await output
                .WriteLineAsync(
                    FormattableString.Invariant(
                        $"State: {client.Session.State}; generation: {client.Session.ConnectionGeneration}; negotiated timeout: {client.Session.NegotiatedTimeout}"
                    )
                )
                .ConfigureAwait(false);
            await client.CloseAsync(cancellationToken).ConfigureAwait(false);
            await output
                .WriteLineAsync($"Close: {client.Session.CloseStatus}")
                .ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
            when (exception
                    is ZooKeeperConnectionException
                        or ZooKeeperSessionException
                        or ZooKeeperException
                        or OperationCanceledException
            )
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }
}
