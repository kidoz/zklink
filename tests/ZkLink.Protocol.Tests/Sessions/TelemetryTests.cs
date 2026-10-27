using System.Diagnostics;
using System.Text;
using ZkLink.Configuration;
using ZkLink.Diagnostics;
using ZkLink.Protocol;
using ZkLink.Requests;
using ZkLink.Sessions;
using ZkLink.Testing;

namespace ZkLink.Protocol.Tests.Sessions;

[CollectionDefinition("Telemetry", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Maintainability",
    "CA1515",
    Justification = "xUnit collection definitions must be public for discovery (xUnit1027). This type is test-only."
)]
public sealed class TelemetryIsolation;

[Collection("Telemetry")]
public sealed class TelemetryTests
{
    private const int TestTimeoutMilliseconds = 20_000;
    private const long Generation = 1;
    private const int NegotiatedMilliseconds = 3000;
    private const string SecretPath = "/private-path-marker";
    private const string SecretData = "private-data-marker";
    private const string SecretUser = "private-user-marker";
    private const string SecretPassword = "private-password-marker";
    private static readonly TimeSpan Elapsed = TimeSpan.FromMilliseconds(25);

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T34")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2025",
        Justification = "The flow-suppression scope controls Task creation only and must end before awaiting. The connection Task is then awaited before its peer and fixture are disposed."
    )]
    public async Task StartupPublicationCallbacksCannotBlockOrPoisonDriver()
    {
        const string ChildFlag = "ZKLINK_TEST_TELEMETRY_STARTUP";
        if (Environment.GetEnvironmentVariable(ChildFlag) != "1")
        {
            // A fresh process is required: the process-wide instruments can already
            // exist when this case runs after other session tests.
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(typeof(TelemetryTests).Assembly.Location);
            start.ArgumentList.Add("-method");
            start.ArgumentList.Add("*StartupPublicationCallbacksCannotBlockOrPoisonDriver");
            start.Environment[ChildFlag] = "1";
            using var child =
                Process.Start(start)
                ?? throw new InvalidOperationException(
                    "Could not start the isolated telemetry check."
                );
            try
            {
                await child
                    .WaitForExitAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(SessionTestSupport.Guard, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                string output = await child
                    .StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                string error = await child
                    .StandardError.ReadToEndAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                Assert.True(child.ExitCode == 0, output + error);
            }
            finally
            {
                if (!child.HasExited)
                    child.Kill(entireProcessTree: true);
            }
            return;
        }

        using var release = new ManualResetEventSlim();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long errors = 0;
        int publicationFinished = 0;
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name != "ZkLink")
                return;
            if (instrument.Name == "zklink.operation.duration")
            {
                try
                {
                    entered.TrySetResult();
                    Assert.True(release.Wait(SessionTestSupport.Guard));
                    throw new InvalidOperationException(SecretPassword);
                }
                finally
                {
                    Volatile.Write(ref publicationFinished, 1);
                }
            }
            owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, value, _, _) =>
            {
                if (instrument.Name == "zklink.telemetry.listener_errors")
                    errors = value;
            }
        );
        listener.Start();
        try
        {
            var fixture = new SessionFixture();
            await using var lifetime = fixture.ConfigureAwait(true);
            using var peer = new SessionPeer();
            Task<ZooKeeperClient> connecting;
            using (ExecutionContext.SuppressFlow())
                connecting = fixture.ConnectAsync(
                    peer,
                    cancellationToken: TestContext.Current.CancellationToken
                );
            var client = await connecting.ConfigureAwait(true);
            await entered
                .Task.WaitAsync(SessionTestSupport.Guard, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await client
                .SyncAsync(SecretPath, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await client.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(ZooKeeperCloseStatus.Acknowledged, client.Session.CloseStatus);
            Assert.Equal(0, Volatile.Read(ref publicationFinished));
        }
        finally
        {
            release.Set();
        }
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        listener.RecordObservableInstruments();
        Assert.True(errors >= 1);
    }

    private enum CompletionCase
    {
        Success,
        Rejected,
        CancelQueued,
        CancelSent,
        TimeoutQueued,
        TimeoutSent,
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(nameof(CompletionCase.Success))]
    [InlineData(nameof(CompletionCase.Rejected))]
    [InlineData(nameof(CompletionCase.CancelQueued))]
    [InlineData(nameof(CompletionCase.CancelSent))]
    [InlineData(nameof(CompletionCase.TimeoutQueued))]
    [InlineData(nameof(CompletionCase.TimeoutSent))]
    [Trait("Acceptance", "T34")]
    public async Task CallerCompletionIsRecordedOnceWhileSentTombstonesRemainOwned(
        string completionName
    )
    {
        var completion = Enum.Parse<CompletionCase>(completionName);
        using var capture = new TelemetryCapture();
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        capture.Reset();
        capture.Collect();
        double requests = capture.Value("zklink.requests.outstanding");
        double bytes = capture.Value("zklink.requests.bytes");
        double tombstones = capture.Value("zklink.requests.tombstones");
        double unknown = capture.Value("zklink.operation.unknown");
        double count = capture
            .Values.Where(static pair =>
                pair.Key.StartsWith(
                    "zklink.operation.count|operation=SetData|",
                    StringComparison.Ordinal
                )
            )
            .Sum(static pair => pair.Value);
        var clock = new ManualTimeProvider();
        var gate = new object();
        var state = new TelemetryState();
        using var options = ClientOptionsSnapshot.Capture(SessionFixture.Options());
        var scheduler = new RequestScheduler(gate, options, clock, new AsyncPulse(), state);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        bool sent = completion is not (CompletionCase.CancelQueued or CompletionCase.TimeoutQueued);
        bool timedOut = completion is CompletionCase.TimeoutQueued or CompletionCase.TimeoutSent;
        scheduler.Attach(Generation, new ZooKeeperEndpoint("private-endpoint-marker", 2181));
        var pending = scheduler.Submit(
            new SetDataRequest(SecretPath, Encoding.UTF8.GetBytes(SecretData), 0),
            new ZooKeeperRequestInfo(ZooKeeperOperation.SetData, path: SecretPath),
            stop.Token
        );
        PendingRequest? wire = null;
        capture.OnActivity = () => Assert.False(Monitor.IsEntered(gate));
        capture.Collect();
        Assert.Equal(requests + 1, capture.Value("zklink.requests.outstanding"));
        Assert.True(capture.Value("zklink.requests.bytes") > bytes);
        if (sent)
        {
            wire = Assert.IsType<PendingRequest>(scheduler.Take(Generation).Request);
            scheduler.WriteFinished(wire);
        }
        clock.Advance(timedOut ? options.Settings.OperationTimeout : Elapsed);
        if (completion is CompletionCase.Success or CompletionCase.Rejected)
        {
            scheduler.Complete(
                Generation,
                wire!,
                new(
                    new(
                        wire!.Info.RequestId!.Value,
                        0,
                        completion == CompletionCase.Rejected ? (int)ZooKeeperErrorCode.NoAuth : 0
                    ),
                    new EmptyResult()
                )
            );
            if (completion == CompletionCase.Success)
                await pending.ConfigureAwait(true);
            else
                await Assert.ThrowsAsync<ZooKeeperException>(() => pending).ConfigureAwait(true);
        }
        else if (timedOut)
            await Assert.ThrowsAsync<ZooKeeperTimeoutException>(() => pending).ConfigureAwait(true);
        else
        {
            await stop.CancelAsync().ConfigureAwait(true);
            await Assert
                .ThrowsAsync<ZooKeeperOperationCanceledException>(() => pending)
                .ConfigureAwait(true);
        }
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        capture.Collect();
        bool retained =
            sent && completion is not (CompletionCase.Success or CompletionCase.Rejected);
        Assert.Equal(tombstones + (retained ? 1 : 0), capture.Value("zklink.requests.tombstones"));
        Assert.Equal(unknown + (retained ? 1 : 0), capture.Value("zklink.operation.unknown"));
        if (retained)
            scheduler.Complete(
                Generation,
                wire!,
                new(new(wire!.Info.RequestId!.Value, 0, 0), new EmptyResult())
            );
        scheduler.Detach();
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        capture.Collect();
        Assert.Equal(requests, capture.Value("zklink.requests.outstanding"));
        Assert.Equal(bytes, capture.Value("zklink.requests.bytes"));
        Assert.Equal(tombstones, capture.Value("zklink.requests.tombstones"));
        Assert.Equal(
            count + 1,
            capture
                .Values.Where(static pair =>
                    pair.Key.StartsWith(
                        "zklink.operation.count|operation=SetData|",
                        StringComparison.Ordinal
                    )
                )
                .Sum(static pair => pair.Value)
        );
        var operation = Assert.Single(
            capture.Activities,
            activity =>
                activity.Name == "zklink.operation"
                && activity.Duration == (timedOut ? options.Settings.OperationTimeout : Elapsed)
        );
        Assert.Contains(
            operation.Tags,
            tag =>
                tag.Key == "outcome"
                && Equals(
                    tag.Value,
                    retained ? "Unknown"
                        : completion == CompletionCase.Rejected ? "Rejected"
                        : completion == CompletionCase.Success ? "Success"
                        : "NotSent"
                )
        );
        capture.AssertRedacted(SecretPath, SecretData, "private-endpoint-marker");
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T34")]
    public async Task LifecycleHeartbeatRecoveryAndCloseUseOriginalDurationsAndSafeParentContext()
    {
        using var capture = new TelemetryCapture();
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        capture.Reset();
        var fixture = new SessionFixture();
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        using var first = new SessionPeer
        {
            NegotiatedTimeout = NegotiatedMilliseconds,
            ReplyPings = false,
        };
        using var second = new SessionPeer
        {
            NegotiatedTimeout = NegotiatedMilliseconds,
            ReplyHandshake = false,
        };
        using var credentials = ZooKeeperAuthentication.Digest(SecretUser, SecretPassword);
        using var parent = new Activity("caller");
        parent.SetIdFormat(ActivityIdFormat.W3C);
        parent.Start();
        parent.AddBaggage("secret", SecretData);
        parent.TraceStateString = "tenant=" + SecretPassword;
        var client = await fixture
            .ConnectAsync(
                first,
                SessionFixture.Options(
                    connection: "one:2181,two:2181",
                    authentication: [credentials]
                ),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        fixture.Connector.Add(second);
        await SessionTestSupport.NextAsync(first).ConfigureAwait(true);
        await SessionTestSupport.NextAsync(first).ConfigureAwait(true);
        var watch = await client
            .AddWatchAsync(
                SecretPath,
                PersistentWatchMode.Persistent,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(first).ConfigureAwait(true);
        capture.Collect();
        Assert.True(capture.Value("zklink.watches.active") >= 1);
        var pingDue = TimeSpan.FromSeconds(1);
        await fixture
            .Time.WaitForTimerAsync(pingDue, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var ping = await SessionTestSupport.NextAsync(first).ConfigureAwait(true);
        const int PingOpcode = 11;
        Assert.Equal(PingOpcode, ping.Opcode);
        while (first.DrainReadWaits()) { }
        fixture.Time.Advance(Elapsed);
        first.Reply(ping);
        await SessionTestSupport.ReaderWaitingAsync(first).ConfigureAwait(true);
        var observer = new StateObserver(client);
        await using var observerLifetime = observer.ConfigureAwait(true);
        first.Drop();
        await observer.WaitAsync(ZooKeeperSessionState.Disconnected).ConfigureAwait(true);
        await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
        capture.Collect();
        Assert.True(capture.Value("zklink.sessions.disconnected") >= 1);
        fixture.Time.Advance(Elapsed);
        second.HandshakeReply();
        await observer.WaitAsync(ZooKeeperSessionState.Connected).ConfigureAwait(true);
        await client
            .SyncAsync(SecretPath, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await client.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await client.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await watch.DisposeAsync().ConfigureAwait(true);
        await client.DisposeAsync().ConfigureAwait(true);
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        capture.Collect();
        var connect = Assert.Single(
            capture.Activities,
            static activity => activity.Name == "zklink.connect"
        );
        Assert.InRange(connect.Duration, TimeSpan.Zero, TimeSpan.FromTicks(1));
        Assert.Equal(parent.TraceId, connect.TraceId);
        Assert.Equal(parent.SpanId, connect.ParentSpanId);
        Assert.Equal(
            typeof(ZooKeeperClient).Assembly.GetName().Version?.ToString(),
            connect.Version
        );
        Assert.Single(capture.Activities, static activity => activity.Name == "zklink.close");
        Assert.Equal(
            2,
            capture.Activities.Count(static activity => activity.Name == "zklink.authenticate")
        );
        Assert.Equal(
            2,
            capture.Activities.Count(static activity => activity.Name == "zklink.restore")
        );
        Assert.Contains(
            capture.Histograms,
            value =>
                value.Name == "zklink.heartbeat.roundtrip" && value.Value == Elapsed.TotalSeconds
        );
        Assert.Contains(
            capture.Histograms,
            value =>
                value.Name == "zklink.session.disconnected.duration"
                && value.Value == Elapsed.TotalSeconds
        );
        Assert.Contains(
            capture.Histograms,
            static value => value.Name == "zklink.session.negotiated_timeout" && value.Value == 3
        );
        Assert.Equal(0, capture.Value("zklink.sessions.disconnected"));
        Assert.Equal(0, capture.Value("zklink.watches.active"));
        Assert.Equal(0, capture.Value("zklink.events.backlog"));
        capture.AssertRedacted(SecretPath, SecretData, SecretUser, SecretPassword);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T34")]
    public async Task EventOverflowAndDisposalBalanceTheSharedBacklog()
    {
        using var capture = new TelemetryCapture();
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        capture.Reset();
        var fixture = new SessionFixture();
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        using var peer = new SessionPeer();
        var client = await fixture
            .ConnectAsync(
                peer,
                SessionFixture.Options(events: 1),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        var subscription = client.SubscribeStateChanges();
        capture.Collect();
        double overflows = capture.Value("zklink.event.overflows");
        Assert.True(capture.Value("zklink.events.backlog") >= 1);
        peer.Drop();
        await SessionTestSupport.Bounded(client.Manager.Completion).ConfigureAwait(true);
        capture.Collect();
        Assert.Equal(overflows + 1, capture.Value("zklink.event.overflows"));
        Assert.Equal(ZooKeeperSessionState.Faulted, client.Session.State);
        await client.DisposeAsync().ConfigureAwait(true);
        capture.Collect();
        Assert.Equal(0, capture.Value("zklink.events.backlog"));
        await subscription.DisposeAsync().ConfigureAwait(true);
        capture.Collect();
        Assert.Equal(0, capture.Value("zklink.events.backlog"));
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T34")]
    public async Task ImmediateAdmissionPressureAndCancellationEmitNotSentWithoutOwningBuffers()
    {
        using var capture = new TelemetryCapture();
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        capture.Reset();
        var clock = new ManualTimeProvider();
        using var options = ClientOptionsSnapshot.Capture(SessionFixture.Options(queued: 1));
        var state = new TelemetryState();
        var scheduler = new RequestScheduler(new object(), options, clock, new AsyncPulse(), state);
        var request = new PathRequest(ZooKeeperOperation.Sync, SecretPath);
        var info = new ZooKeeperRequestInfo(ZooKeeperOperation.Sync, path: SecretPath);
        await Assert
            .ThrowsAsync<ZooKeeperException>(() =>
                scheduler.Submit(request, info, TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);
        scheduler.Attach(Generation, new ZooKeeperEndpoint("peer", 2181));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var queued = scheduler.Submit(request, info, stop.Token);
        await Assert
            .ThrowsAsync<ZooKeeperBackpressureException>(() =>
                scheduler.Submit(request, info, TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);
        await stop.CancelAsync().ConfigureAwait(true);
        await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => queued)
            .ConfigureAwait(true);
        await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() =>
                scheduler.Submit(request, info, stop.Token)
            )
            .ConfigureAwait(true);
        var close = scheduler.EnqueueClose();
        capture.Collect();
        Assert.Equal(1, capture.Value("zklink.requests.outstanding"));
        var closing = Assert.IsType<PendingRequest>(scheduler.Take(Generation).Request);
        scheduler.WriteFinished(closing);
        scheduler.Complete(
            Generation,
            closing,
            new(new(closing.Info.RequestId!.Value, 0, 0), new EmptyResult())
        );
        await close.Task.ConfigureAwait(true);
        scheduler.Detach();
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        capture.Collect();
        Assert.Equal(4, capture.Activities.Count);
        Assert.All(
            capture.Activities,
            static activity =>
                Assert.Contains(
                    activity.Tags,
                    static tag => tag.Key == "outcome" && Equals(tag.Value, "NotSent")
                )
        );
        Assert.Contains(
            capture.Activities,
            static activity =>
                activity.Tags.Any(static tag =>
                    tag.Key == "error" && Equals(tag.Value, "Backpressure")
                )
        );
        Assert.Equal(0, capture.Value("zklink.requests.bytes"));
        Assert.Equal(0, capture.Value("zklink.requests.outstanding"));
        capture.AssertRedacted(SecretPath);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T34")]
    public async Task AuthenticationFailureEmitsFailedLifecycleWithoutCredentialOrExceptionText()
    {
        using var capture = new TelemetryCapture();
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        capture.Reset();
        var fixture = new SessionFixture();
        await using var lifetime = fixture.ConfigureAwait(true);
        using var peer = new SessionPeer { AuthError = (int)ZooKeeperErrorCode.AuthFailed };
        using var authentication = ZooKeeperAuthentication.Digest(SecretUser, SecretPassword);
        await Assert
            .ThrowsAsync<ZooKeeperConnectionException>(() =>
                fixture.ConnectAsync(
                    peer,
                    SessionFixture.Options(authentication: [authentication]),
                    TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Contains(
            capture.Activities,
            static activity =>
                activity.Name == "zklink.connect" && activity.Status == ActivityStatusCode.Error
        );
        var auth = Assert.Single(
            capture.Activities,
            static activity => activity.Name == "zklink.authenticate"
        );
        Assert.Contains(
            auth.Tags,
            static tag => tag.Key == "outcome" && Equals(tag.Value, "Failed")
        );
        capture.AssertRedacted(SecretUser, SecretPassword);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T34")]
    public async Task UnacknowledgedClosePreservesItsUnknownRequestOutcome()
    {
        using var capture = new TelemetryCapture();
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        capture.Reset();
        var fixture = new SessionFixture();
        await using var lifetime = fixture.ConfigureAwait(true);
        using var peer = new SessionPeer { ReplyClose = false };
        var options = SessionFixture.Options();
        var client = await fixture
            .ConnectAsync(peer, options, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        var closing = client.CloseAsync(TestContext.Current.CancellationToken);
        var request = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        Assert.Equal((int)ZooKeeperOperation.CloseSession, request.Opcode);
        await fixture
            .Time.WaitForTimerAsync(options.CloseTimeout, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        fixture.Time.Advance(options.CloseTimeout);
        var error = await Assert
            .ThrowsAsync<ZooKeeperCloseException>(() => closing)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, error.Outcome);
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var close = Assert.Single(
            capture.Activities,
            static activity => activity.Name == "zklink.close"
        );
        Assert.Equal(ActivityStatusCode.Error, close.Status);
        Assert.Contains(
            close.Tags,
            static tag => tag.Key == "outcome" && Equals(tag.Value, "Unknown")
        );
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T34")]
    public async Task BlockedThrowingListenersAndFullDiagnosticQueueCannotBlockDriverProgress()
    {
        using var capture = new TelemetryCapture();
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        capture.Reset();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.OnActivity = () =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(SessionTestSupport.Guard));
        };
        try
        {
            var fixture = new SessionFixture();
            await using var lifetime = fixture.ConfigureAwait(true);
            using var peer = new SessionPeer();
            var client = await fixture
                .ConnectAsync(peer, cancellationToken: TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await entered
                .Task.WaitAsync(SessionTestSupport.Guard, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            capture.Collect();
            double dropped = capture.Value("zklink.telemetry.dropped");
            double successes = capture.Value(
                "zklink.operation.count|operation=Sync|outcome=Success"
            );
            double unknown = capture.Value("zklink.operation.unknown");
            const int BeyondCapacity = 4;
            for (int i = 0; i < ZooKeeperTelemetry.Capacity + BeyondCapacity; i++)
                ZooKeeperTelemetry
                    .Begin(fixture.Time, TelemetryKind.Operation, ZooKeeperOperation.Sync)
                    .Complete();
            ZooKeeperTelemetry
                .Begin(fixture.Time, TelemetryKind.Operation, ZooKeeperOperation.Sync)
                .Complete(
                    new ZooKeeperException(
                        new ZooKeeperRequestInfo(ZooKeeperOperation.Sync, path: SecretPath),
                        RequestOutcome.Unknown
                    )
                );
            await client
                .SyncAsync(SecretPath, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            await client.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            capture.Collect();
            Assert.True(capture.Value("zklink.telemetry.dropped") > dropped);
            Assert.Equal(
                successes + ZooKeeperTelemetry.Capacity + BeyondCapacity + 1,
                capture.Value("zklink.operation.count|operation=Sync|outcome=Success")
            );
            Assert.Equal(unknown + 1, capture.Value("zklink.operation.unknown"));
            Assert.Equal(ZooKeeperCloseStatus.Acknowledged, client.Session.CloseStatus);
        }
        finally
        {
            release.Set();
        }
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        capture.OnActivity = static () => throw new InvalidOperationException(SecretPassword);
        capture.Collect();
        double errors = capture.Value("zklink.telemetry.listener_errors");
        ZooKeeperTelemetry.Begin(TimeProvider.System, TelemetryKind.Close).Complete();
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        capture.Collect();
        Assert.Equal(errors + 1, capture.Value("zklink.telemetry.listener_errors"));
        capture.OnActivity = null;
        ZooKeeperTelemetry.Begin(TimeProvider.System, TelemetryKind.Close).Complete();
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        capture.Collect();
        errors = capture.Value("zklink.telemetry.listener_errors");
        capture.OnMeasurement = static () => throw new InvalidOperationException(SecretPassword);
        ZooKeeperTelemetry.Begin(TimeProvider.System, TelemetryKind.Close).Complete();
        await ZooKeeperTelemetry
            .FlushAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        capture.OnMeasurement = null;
        capture.Collect();
        Assert.Equal(errors + 1, capture.Value("zklink.telemetry.listener_errors"));
        capture.AssertRedacted(SecretPath, SecretPassword);
    }
}
