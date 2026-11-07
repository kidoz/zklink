using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ZkLink.FaultTests;

[Collection("FaultProxy")]
[Trait("Fixture", "ProxyUnit")]
public sealed class FaultProxySignalTests
{
    private const int TestTimeoutMilliseconds = 15_000;

    // Attribution can use its two-second capture guard and two-second reap
    // guard before the original framing failure completes the forwarding task.
    private static readonly TimeSpan SignalGuard = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MissingSignalGuard = TimeSpan.FromMilliseconds(100);

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FramingFailureReachesPendingAndLaterSignalWaitsBeforeDisposal(
        bool incoming,
        bool handshake
    )
    {
        var token = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        InvalidDataException? primary = null;
        Exception? checkFailure = null;
        var disposal = await Record
            .ExceptionAsync(async () =>
            {
                var proxy = new FaultProxy(
                    $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
                    holdRestore: true,
                    token
                );
                await using var lifetime = proxy.ConfigureAwait(true);
                // Capture assertions separately so the expected disposal exception
                // cannot replace a failed assertion with a passing cleanup check.
                checkFailure = await Record
                    .ExceptionAsync(async () =>
                    {
                        using var client = new TcpClient();
                        var endpoint = new Uri("tcp://" + proxy.Endpoint);
                        await client
                            .ConnectAsync(endpoint.Host, endpoint.Port, token)
                            .ConfigureAwait(true);
                        using var upstream = await listener
                            .AcceptTcpClientAsync(token)
                            .ConfigureAwait(true);
                        var sender = incoming ? upstream.GetStream() : client.GetStream();
                        var receiver = incoming ? client.GetStream() : upstream.GetStream();
                        if (!handshake)
                        {
                            const int HandshakeLength = 2 * sizeof(int);
                            var frame = new byte[sizeof(int) + HandshakeLength];
                            BinaryPrimitives.WriteInt32BigEndian(frame, HandshakeLength);
                            await sender.WriteAsync(frame, token).ConfigureAwait(true);
                            await receiver
                                .ReadExactlyAsync(new byte[frame.Length], token)
                                .ConfigureAwait(true);
                        }
                        Task[] signals =
                        [
                            proxy.ResponseWithheld,
                            proxy.RestoreHeld,
                            proxy.SyncReplyHeld,
                            proxy.DeleteReplyHeld,
                            proxy.ServerClosed,
                            proxy.CreateDropped,
                        ];
                        var pending = signals
                            .Select(
                                (signal, index) =>
                                    index == 0
                                        ? SoakDiagnostics.ObserveAsync(
                                            proxy.WaitForSignalAsync(signal, SignalGuard, token),
                                            SoakWaitSite.CreateReplyWithheld
                                        )
                                        : proxy.WaitForSignalAsync(signal, SignalGuard, token)
                            )
                            .ToArray();
                        Assert.All(pending, wait => Assert.False(wait.IsCompleted));
                        var before = DateTimeOffset.UtcNow;
                        var prefix = new byte[sizeof(int)];
                        BinaryPrimitives.WriteInt32BigEndian(
                            prefix,
                            FaultProxy.MaximumFrameBytes + 1
                        );
                        await sender.WriteAsync(prefix, token).ConfigureAwait(true);
                        primary = await Assert
                            .ThrowsAsync<InvalidDataException>(() => Task.WhenAll(pending))
                            .ConfigureAwait(true);
                        var detail = Assert.IsType<FaultProxyFrameFailure>(
                            primary.Data[typeof(FaultProxyFrameFailure)]
                        );
                        Assert.InRange(detail.ObservedAtUtc, before, DateTimeOffset.UtcNow);
                        Assert.Equal(incoming, detail.Incoming);
                        Assert.Equal(handshake, detail.Handshake);
                        Assert.Equal(FaultProxy.MaximumFrameBytes + 1, detail.Length);
                        foreach (var wait in pending)
                        {
                            Assert.Same(
                                primary,
                                await Assert
                                    .ThrowsAsync<InvalidDataException>(() => wait)
                                    .ConfigureAwait(true)
                            );
                        }
                        foreach (var signal in signals)
                        {
                            Assert.False(signal.IsCompleted);
                            var late = await Assert
                                .ThrowsAsync<InvalidDataException>(() =>
                                    proxy.WaitForSignalAsync(signal, SignalGuard, token)
                                )
                                .ConfigureAwait(true);
                            Assert.Same(primary, late);
                            Assert.Same(detail, late.Data[typeof(FaultProxyFrameFailure)]);
                        }
                        Assert.Equal(nameof(InvalidDataException), proxy.RunFailure);
                        Assert.Equal(
                            SoakWaitSite.CreateReplyWithheld,
                            primary.Data[typeof(SoakWaitSite)]
                        );
                    })
                    .ConfigureAwait(true);
            })
            .ConfigureAwait(true);
        Assert.Null(checkFailure);
        Assert.NotNull(primary);
        Assert.Same(primary, disposal);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task MissingSignalRetainsGuardCancellationAndDisposalOutcomes()
    {
        var token = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxy = new FaultProxy(
            $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
            holdRestore: false,
            token
        );
        await using var lifetime = proxy.ConfigureAwait(true);
        await proxy.WaitForSignalAsync(Task.CompletedTask, SignalGuard, token).ConfigureAwait(true);
        await Assert
            .ThrowsAsync<TimeoutException>(() =>
                proxy.WaitForSignalAsync(proxy.RestoreHeld, MissingSignalGuard, token)
            )
            .ConfigureAwait(true);
        using var cancellation = new CancellationTokenSource();
        var cancelled = proxy.WaitForSignalAsync(
            proxy.RestoreHeld,
            SignalGuard,
            cancellation.Token
        );
        await cancellation.CancelAsync().ConfigureAwait(true);
        var error = await Assert
            .ThrowsAnyAsync<OperationCanceledException>(() => cancelled)
            .ConfigureAwait(true);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Null(proxy.RunFailure);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task NormalDisposalCancelsPendingSignalWaitWithoutReportingSuccess()
    {
        var token = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxy = new FaultProxy(
            $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
            holdRestore: false,
            token
        );
        Task wait;
        await using (proxy.ConfigureAwait(true))
        {
            wait = proxy.WaitForSignalAsync(proxy.RestoreHeld, SignalGuard, token);
        }
        var error = await Assert
            .ThrowsAnyAsync<OperationCanceledException>(() => wait)
            .ConfigureAwait(true);
        Assert.True(error.CancellationToken.IsCancellationRequested);
        Assert.False(proxy.RestoreHeld.IsCompleted);
        Assert.Null(proxy.RunFailure);
    }
}
