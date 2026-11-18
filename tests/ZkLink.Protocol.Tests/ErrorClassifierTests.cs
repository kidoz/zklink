namespace ZkLink.Protocol.Tests;

public sealed class ErrorClassifierTests
{
    [Fact]
    public void EveryServerCodeRetainsItsPhaseSpecificImpactAndTransmissionOutcome()
    {
        const ZooKeeperConnectionFailure disconnect = ZooKeeperConnectionFailure.Unreachable;
        const ZooKeeperConnectionFailure expire = ZooKeeperConnectionFailure.SessionRejected;
        const ZooKeeperConnectionFailure auth = ZooKeeperConnectionFailure.Authentication;
        const ZooKeeperConnectionFailure protocol = ZooKeeperConnectionFailure.Protocol;
        const RequestOutcome unknown = RequestOutcome.Unknown;
        const RequestOutcome rejected = RequestOutcome.Rejected;
        var checkedCodes = new HashSet<int>();

        Check([0], null, null, null, null, unknown);
        Check([-12, -118], disconnect, disconnect, disconnect, disconnect, unknown);
        Check([-4, -7, -122], null, disconnect, disconnect, disconnect, unknown);
        Check([-112], expire, expire, expire, expire, rejected);
        Check([-115, -124], auth, auth, auth, auth, rejected);
        Check(
            [
                -6,
                -8,
                -101,
                -102,
                -103,
                -108,
                -110,
                -111,
                -113,
                -114,
                -119,
                -120,
                -121,
                -123,
                -125,
                -127,
            ],
            null,
            protocol,
            auth,
            protocol,
            rejected
        );
        Check([-1, -2, -3, -5, -13, -14, -999], null, protocol, auth, protocol, unknown);

        Assert.Equal(
            Enum.GetValues<ZooKeeperErrorCode>()
                .Select(code => (int)code)
                .Append(0)
                .Append(-999)
                .Order(),
            checkedCodes.Order()
        );

        void Check(
            int[] codes,
            ZooKeeperConnectionFailure? application,
            ZooKeeperConnectionFailure? ping,
            ZooKeeperConnectionFailure? authentication,
            ZooKeeperConnectionFailure? restore,
            RequestOutcome outcome
        )
        {
            foreach (var code in codes)
            {
                Assert.True(checkedCodes.Add(code));
                Assert.Equal(
                    application,
                    ErrorClassifier.ConnectionImpact(code, ReplyPhase.Application)
                );
                Assert.Equal(ping, ErrorClassifier.ConnectionImpact(code, ReplyPhase.Ping));
                Assert.Equal(
                    authentication,
                    ErrorClassifier.ConnectionImpact(code, ReplyPhase.Authentication)
                );
                Assert.Equal(
                    restore,
                    ErrorClassifier.ConnectionImpact(code, ReplyPhase.WatchRestore)
                );
                if (code != 0)
                {
                    Assert.Equal(outcome, ErrorClassifier.OutcomeFor(code));
                }
            }
        }
    }

    [Theory]
    [InlineData(3, null)]
    [InlineData(0, ZooKeeperConnectionFailure.Unreachable)]
    [InlineData(-112, ZooKeeperConnectionFailure.SessionRejected)]
    [InlineData(4, ZooKeeperConnectionFailure.Authentication)]
    [InlineData(-115, ZooKeeperConnectionFailure.Protocol)]
    public void SessionNotificationsUseKeeperStateValues(
        int state,
        ZooKeeperConnectionFailure? failure
    ) => Assert.Equal(failure, ErrorClassifier.NotificationImpact(state));
}
