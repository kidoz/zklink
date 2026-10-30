using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace ZkLink.FaultTests;

[Collection("WatchPressure")]
[Trait("Fixture", "SoakUnit")]
public sealed class SoakEvidenceTests
{
    private const int TestTimeoutMilliseconds = 30_000;
    private const int RetainedAllocationBytes = 32 * 1024 * 1024;
    private const int DurationSeconds = 30;
    private const int Seed = 1729;

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureJournalPreservesSafeTimingAndOutcomeWithoutSensitiveRequestMetadata(
        bool timeout
    )
    {
        const string SensitiveMarker = "soak-failure-private-marker";
        const int FailureElapsedMilliseconds = 1234;
        string directory = Path.Combine(
            Path.GetTempPath(),
            "zklink-soak-unit-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "failure.jsonl");
        var before = DateTimeOffset.UtcNow;
        var elapsed = TimeSpan.FromMilliseconds(FailureElapsedMilliseconds);
        try
        {
            var evidence = new SoakEvidence(new SoakSettings(DurationSeconds, Seed, file));
            await using (evidence.ConfigureAwait(true))
            {
                var request = new ZooKeeperRequestInfo(
                    timeout ? ZooKeeperOperation.Sync : ZooKeeperOperation.Delete,
                    path: "/" + SensitiveMarker
                );
                ZooKeeperException failure = timeout
                    ? new ZooKeeperTimeoutException(request, RequestOutcome.Unknown)
                    : new ZooKeeperException(
                        request,
                        RequestOutcome.Rejected,
                        (int)ZooKeeperErrorCode.NotEmpty
                    );
                await evidence
                    .FailureAsync(failure, new SoakTotals(), elapsed)
                    .ConfigureAwait(true);
                // Outer propagation must not duplicate the same error. A different
                // cleanup failure must retain the original error and add its own record.
                await evidence
                    .FailureAsync(failure, new SoakTotals(), elapsed)
                    .ConfigureAwait(true);
                await evidence
                    .FailureAsync(new IOException(SensitiveMarker), new SoakTotals(), elapsed)
                    .ConfigureAwait(true);
            }
            string[] lines = await File.ReadAllLinesAsync(
                    file,
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
            Assert.Equal(2, lines.Length);
            Assert.DoesNotContain(
                SensitiveMarker,
                string.Join(Environment.NewLine, lines),
                StringComparison.Ordinal
            );
            using var cleanup = JsonDocument.Parse(lines[1]);
            Assert.Equal(
                nameof(IOException),
                cleanup.RootElement.GetProperty("error_type").GetString()
            );
            Assert.Equal(
                JsonValueKind.Null,
                cleanup.RootElement.GetProperty("request_operation").ValueKind
            );
            Assert.Equal(
                JsonValueKind.Null,
                cleanup.RootElement.GetProperty("request_outcome").ValueKind
            );
            using var json = JsonDocument.Parse(lines[0]);
            var code = json.RootElement.GetProperty("error_code");
            if (timeout)
            {
                Assert.Equal(JsonValueKind.Null, code.ValueKind);
            }
            else
            {
                Assert.Equal((int)ZooKeeperErrorCode.NotEmpty, code.GetInt32());
            }
            Assert.Equal(
                timeout ? nameof(RequestOutcome.Unknown) : nameof(RequestOutcome.Rejected),
                json.RootElement.GetProperty("request_outcome").GetString()
            );
            Assert.Equal(
                timeout ? nameof(ZooKeeperOperation.Sync) : nameof(ZooKeeperOperation.Delete),
                json.RootElement.GetProperty("request_operation").GetString()
            );
            Assert.Equal(
                elapsed.TotalMilliseconds,
                json.RootElement.GetProperty("elapsed_ms").GetDouble()
            );
            Assert.InRange(
                json.RootElement.GetProperty("reported_at_utc").GetDateTimeOffset(),
                before,
                DateTimeOffset.UtcNow
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task FailureJournalKeepsWaitSiteAndOriginalFrameObservationWithoutMessages()
    {
        const string SensitiveMarker = "private-diagnostic-marker";
        const int RejectedLength = -1;
        string directory = Path.Combine(
            Path.GetTempPath(),
            "zklink-soak-unit-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "failure.jsonl");
        var observed = DateTimeOffset.UtcNow;
        var timeout = new TimeoutException(SensitiveMarker);
        var frameFailure = new InvalidDataException(SensitiveMarker);
        var detail = new FaultProxyFrameFailure(
            observed,
            true,
            false,
            1,
            2,
            RejectedLength,
            FaultProxy.MaximumFrameBytes
        );
        frameFailure.Data[typeof(FaultProxyFrameFailure)] = detail;
        try
        {
            var propagated = await Assert
                .ThrowsAsync<TimeoutException>(() =>
                    SoakDiagnostics.ObserveAsync(
                        Task.FromException(timeout),
                        SoakWaitSite.RestoreAcknowledgmentHeld
                    )
                )
                .ConfigureAwait(true);
            Assert.Same(timeout, propagated);
            var evidence = new SoakEvidence(new SoakSettings(DurationSeconds, Seed, file));
            await using (evidence.ConfigureAwait(true))
            {
                await evidence
                    .FailureAsync(timeout, new SoakTotals(), TimeSpan.Zero)
                    .ConfigureAwait(true);
                await evidence
                    .FailureAsync(frameFailure, new SoakTotals(), TimeSpan.Zero)
                    .ConfigureAwait(true);
            }
            string[] lines = await File.ReadAllLinesAsync(
                    file,
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
            Assert.Equal(2, lines.Length);
            Assert.DoesNotContain(
                SensitiveMarker,
                string.Join(Environment.NewLine, lines),
                StringComparison.Ordinal
            );
            using var primary = JsonDocument.Parse(lines[0]);
            Assert.Equal(
                nameof(SoakWaitSite.RestoreAcknowledgmentHeld),
                primary.RootElement.GetProperty("wait_site").GetString()
            );
            Assert.Equal(
                JsonValueKind.Null,
                primary.RootElement.GetProperty("frame_failure").ValueKind
            );
            using var cleanup = JsonDocument.Parse(lines[1]);
            Assert.Equal(
                JsonValueKind.Null,
                cleanup.RootElement.GetProperty("wait_site").ValueKind
            );
            var frame = cleanup.RootElement.GetProperty("frame_failure");
            Assert.Equal(observed, frame.GetProperty("observed_at_utc").GetDateTimeOffset());
            Assert.True(
                observed <= primary.RootElement.GetProperty("reported_at_utc").GetDateTimeOffset()
            );
            Assert.Equal("incoming", frame.GetProperty("direction").GetString());
            Assert.False(frame.GetProperty("handshake").GetBoolean());
            Assert.Equal(
                detail.ConnectionAttempt,
                frame.GetProperty("connection_attempt").GetInt32()
            );
            Assert.Equal(detail.FrameNumber, frame.GetProperty("frame_number").GetInt64());
            Assert.Equal(RejectedLength, frame.GetProperty("length").GetInt32());
            Assert.Equal(
                FaultProxy.MaximumFrameBytes,
                frame.GetProperty("maximum_length").GetInt32()
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task WaitObservationPreservesResultsCancellationAndOtherFailures()
    {
        const int Result = 42;
        Assert.Equal(
            Result,
            await SoakDiagnostics
                .ObserveAsync(Task.FromResult(Result), SoakWaitSite.OneShotEvent)
                .ConfigureAwait(true)
        );
        var timeout = new TimeoutException();
        Assert.Same(
            timeout,
            await Assert
                .ThrowsAsync<TimeoutException>(() =>
                    SoakDiagnostics.ObserveAsync(
                        Task.FromException<bool>(timeout),
                        SoakWaitSite.PersistentEvent
                    )
                )
                .ConfigureAwait(true)
        );
        Assert.Equal(SoakWaitSite.PersistentEvent, timeout.Data[typeof(SoakWaitSite)]);
        var other = new IOException();
        Assert.Same(
            other,
            await Assert
                .ThrowsAsync<IOException>(() =>
                    SoakDiagnostics.ObserveAsync(
                        Task.FromException(other),
                        SoakWaitSite.ConnectionReady
                    )
                )
                .ConfigureAwait(true)
        );
        Assert.False(other.Data.Contains(typeof(SoakWaitSite)));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        await canceled.CancelAsync().ConfigureAwait(true);
        var cancellation = await Assert
            .ThrowsAnyAsync<OperationCanceledException>(() =>
                SoakDiagnostics.ObserveAsync(
                    Task.FromCanceled(canceled.Token),
                    SoakWaitSite.ConnectionReady
                )
            )
            .ConfigureAwait(true);
        Assert.Equal(canceled.Token, cancellation.CancellationToken);
        Assert.False(cancellation.Data.Contains(typeof(SoakWaitSite)));
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task RetainedAllocationBreachesTheRecordedHeapBound()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "zklink-soak-unit-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            var evidence = new SoakEvidence(
                new SoakSettings(DurationSeconds, Seed, Path.Combine(directory, "samples.jsonl"))
            );
            await using var lifetime = evidence.ConfigureAwait(true);
            var totals = new SoakTotals();
            await evidence
                .SampleAsync(
                    "baseline",
                    totals,
                    TimeSpan.Zero,
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
            var retained = new byte[RetainedAllocationBytes];
            var failure = await Record
                .ExceptionAsync(() =>
                    evidence.SampleAsync(
                        "checkpoint",
                        totals,
                        TimeSpan.FromSeconds(1),
                        TestContext.Current.CancellationToken
                    )
                )
                .ConfigureAwait(true);
            GC.KeepAlive(retained);
            Assert.NotNull(failure);
            Assert.Contains(
                "Retained managed heap exceeded",
                failure.Message,
                StringComparison.Ordinal
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task SocketObservationWaitsForOwnedConnectionAndIgnoresAnotherDestination()
    {
        var token = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var unrelated = new TcpListener(IPAddress.Loopback, 0);
        using var client = new TcpClient();
        listener.Start();
        unrelated.Start();
        int target = ((IPEndPoint)listener.LocalEndpoint).Port;
        int otherTarget = ((IPEndPoint)unrelated.LocalEndpoint).Port;
        await client.ConnectAsync(IPAddress.Loopback, target, token).ConfigureAwait(true);
        using var accepted = await listener.AcceptTcpClientAsync(token).ConfigureAwait(true);
        int local = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        var closed = SoakMetrics.PortsClosedAsync([], [local], target, token);
        try
        {
            Assert.False(closed.IsCompleted);
            await SoakMetrics
                .PortsClosedAsync([], [local], otherTarget, token)
                .ConfigureAwait(true);
        }
        finally
        {
            client.Dispose();
            accepted.Dispose();
            listener.Stop();
            await closed.ConfigureAwait(true);
        }
    }
}
