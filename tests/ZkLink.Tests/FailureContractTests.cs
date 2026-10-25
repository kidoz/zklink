namespace ZkLink.Tests;

public sealed class FailureContractTests
{
    [Theory]
    [InlineData(-103, ZooKeeperErrorCode.BadVersion)]
    [InlineData(-127, ZooKeeperErrorCode.ThrottledOperation)]
    [InlineData(-999, null)]
    [InlineData(-100, null)]
    [Trait("Acceptance", "T14")]
    public void RawServerCodesAndExplicitOutcomesArePreserved(int code, ZooKeeperErrorCode? known)
    {
        var request = new ZooKeeperRequestInfo(
            ZooKeeperOperation.SetData,
            7,
            new ZooKeeperEndpoint("zk.example", 2281),
            "/settings"
        );
        var exception = new ZooKeeperException(request, RequestOutcome.Rejected, code);
        Assert.Same(request, exception.Request);
        Assert.Equal(code, exception.ErrorCode);
        Assert.Equal(known, exception.KnownErrorCode);
        Assert.Equal(RequestOutcome.Rejected, exception.Outcome);
        Assert.Equal(5, exception.Request.Opcode);
        Assert.Equal(7, exception.Request.RequestId);
        Assert.Equal("/settings", exception.Request.Path);
        Assert.Equal("zk.example", exception.Request.Endpoint!.Host);
    }

    [Fact]
    [Trait("Acceptance", "T12")]
    public void TransportCodeDoesNotDetermineTransmissionOutcome()
    {
        var request = new ZooKeeperRequestInfo(ZooKeeperOperation.SetData);
        var exception = new ZooKeeperException(request, RequestOutcome.Unknown, -4);
        Assert.Equal(RequestOutcome.Unknown, exception.Outcome);
        Assert.Equal(ZooKeeperErrorCode.ConnectionLoss, exception.KnownErrorCode);
        Assert.Throws<ArgumentException>(() =>
            new ZooKeeperException(request, RequestOutcome.NotSent, -4)
        );
        Assert.Throws<ArgumentException>(() =>
            new ZooKeeperException(request, RequestOutcome.Rejected)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ZooKeeperException(request, RequestOutcome.Unknown, 0)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ZooKeeperException(request, (RequestOutcome)99)
        );
    }

    [Theory]
    [InlineData(RequestOutcome.NotSent)]
    [InlineData(RequestOutcome.Unknown)]
    [Trait("Acceptance", "T11")]
    [Trait("Acceptance", "T12")]
    public void CancellationPreservesTokenAndOrdinaryCancellationHandling(RequestOutcome outcome)
    {
        using var source = new CancellationTokenSource();
        var request = new ZooKeeperRequestInfo(ZooKeeperOperation.CreateWithStat);
        var exception = new ZooKeeperOperationCanceledException(request, outcome, source.Token);
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        var failure = Assert.IsAssignableFrom<IZooKeeperRequestFailure>(exception);
        Assert.Same(request, failure.Request);
        Assert.Equal(outcome, failure.Outcome);
        Assert.Equal(source.Token, exception.CancellationToken);
        Assert.Null(failure.ErrorCode);
        Assert.Null(failure.KnownErrorCode);
    }

    [Fact]
    [Trait("Acceptance", "T11")]
    public void BackpressureAndDeadlinesHaveNoInventedServerCode()
    {
        var request = new ZooKeeperRequestInfo(ZooKeeperOperation.GetData);
        var capacity = new ZooKeeperBackpressureException(request);
        Assert.Equal(RequestOutcome.NotSent, capacity.Outcome);
        Assert.Null(capacity.ErrorCode);
        Assert.Null(capacity.Request.RequestId);
        var timeout = new ZooKeeperTimeoutException(request, RequestOutcome.Unknown);
        Assert.Equal(RequestOutcome.Unknown, timeout.Outcome);
        Assert.Null(timeout.ErrorCode);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ZooKeeperOperationCanceledException(request, RequestOutcome.Rejected)
        );
        Assert.ThrowsAny<ArgumentException>(() =>
            new ZooKeeperTimeoutException(request, RequestOutcome.Rejected)
        );
    }

    [Fact]
    [Trait("Acceptance", "T14")]
    public void CloseFailuresNeverClaimAcknowledgment()
    {
        var request = new ZooKeeperRequestInfo(ZooKeeperOperation.CloseSession, 9);
        var exception = new ZooKeeperCloseException(request, RequestOutcome.Unknown);
        Assert.False(exception.CloseAcknowledged);
        Assert.Equal(RequestOutcome.Unknown, exception.Outcome);
        var cancelled = new ZooKeeperOperationCanceledException(request, RequestOutcome.NotSent);
        Assert.False(cancelled.CloseAcknowledged);
        Assert.Throws<ArgumentException>(() =>
            new ZooKeeperCloseException(
                new ZooKeeperRequestInfo(ZooKeeperOperation.GetData),
                RequestOutcome.NotSent
            )
        );
    }

    [Fact]
    [Trait("Acceptance", "T18")]
    public void MultiFailurePreservesFullOrderedDiagnosticsWithoutImplyingCommit()
    {
        ZooKeeperMultiDiagnostic[] diagnostics =
        [
            new(0, 15, 0, "/created", default(ZNodeStat)),
            new(1, 5, -103),
            new(2, 2, -2),
        ];
        var exception = new ZooKeeperMultiException(
            new ZooKeeperRequestInfo(ZooKeeperOperation.Multi, 3),
            diagnostics,
            1
        );
        diagnostics[1] = new ZooKeeperMultiDiagnostic(1, 5, -999);
        Assert.Equal(3, exception.Diagnostics.Count);
        Assert.Equal(1, exception.FailedMemberIndex);
        Assert.Equal(0, exception.Diagnostics[0].ErrorCode);
        Assert.Equal(-103, exception.Diagnostics[1].ErrorCode);
        Assert.Equal(-103, exception.ErrorCode);
        Assert.Equal(RequestOutcome.Rejected, exception.Outcome);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ZooKeeperMultiDiagnostic>)exception.Diagnostics).Clear()
        );
    }

    [Fact]
    public void MultiFailureRejectsMissingOutOfOrderOrSuccessfulFailingMember()
    {
        var request = new ZooKeeperRequestInfo(ZooKeeperOperation.Multi);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ZooKeeperMultiException(request, [], 0)
        );
        Assert.Throws<ArgumentException>(() =>
            new ZooKeeperMultiException(request, [new(1, 5, -103)], 0)
        );
        Assert.Throws<ArgumentException>(() =>
            new ZooKeeperMultiException(request, [new(0, 5, 0)], 0)
        );
        Assert.Throws<ArgumentException>(() =>
            new ZooKeeperMultiException(
                new ZooKeeperRequestInfo(ZooKeeperOperation.MultiRead),
                [new(0, 5, -103)],
                0
            )
        );
    }

    [Fact]
    [Trait("Acceptance", "T34")]
    public void ExceptionTextOmitsPathAndEndpoint()
    {
        const string marker = "sensitive-marker";
        var request = new ZooKeeperRequestInfo(
            ZooKeeperOperation.SetData,
            1,
            new ZooKeeperEndpoint(marker + ".example"),
            "/" + marker
        );
        Exception[] failures =
        [
            new ZooKeeperException(request, RequestOutcome.Unknown, -999),
            new ZooKeeperTimeoutException(request, RequestOutcome.Unknown),
            new ZooKeeperBackpressureException(request),
            new ZooKeeperOperationCanceledException(request, RequestOutcome.Unknown),
        ];
        foreach (Exception failure in failures)
            Assert.DoesNotContain(marker, failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RequestIdsMustBePositiveWhenAssigned(int xid) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ZooKeeperRequestInfo(ZooKeeperOperation.Exists, xid)
        );

    [Fact]
    [Trait("Acceptance", "T29")]
    public void WatchRemovalDistinguishesSharedRetentionFromServerConfirmation()
    {
        var shared = new ZooKeeperWatchRemovalResult(
            42,
            WatchRemovalStatus.SharedRegistrationRetained
        );
        Assert.Equal(42, shared.RegistrationId);
        Assert.NotEqual(WatchRemovalStatus.ServerConfirmed, shared.Status);
        Assert.Equal(
            WatchRemovalStatus.NoWatcher,
            new ZooKeeperWatchRemovalResult(42, WatchRemovalStatus.NoWatcher).Status
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ZooKeeperWatchRemovalResult(0, WatchRemovalStatus.LocalOnly)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ZooKeeperWatchRemovalResult(1, (WatchRemovalStatus)99)
        );
    }
}
