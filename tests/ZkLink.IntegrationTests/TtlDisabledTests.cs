namespace ZkLink.IntegrationTests;

[Trait("Fixture", "TTL")]
public sealed class TtlDisabledTests : IAsyncLifetime
{
    private readonly LiveZooKeeperFixture _fixture = new();

    public ValueTask InitializeAsync()
    {
        TtlTestSupport.RequireSetting("disabled");
        return _fixture.InitializeAsync();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Theory(Timeout = TtlTestSupport.TestTimeoutMilliseconds)]
    [InlineData(CreateMode.PersistentWithTtl, false)]
    [InlineData(CreateMode.PersistentWithTtl, true)]
    [InlineData(CreateMode.PersistentSequentialWithTtl, false)]
    [InlineData(CreateMode.PersistentSequentialWithTtl, true)]
    [Trait("Acceptance", "T17")]
    public async Task ExplicitClientOptInPreservesDisabledServerRejectionAndAtomicRollback(
        CreateMode mode,
        bool multi
    )
    {
        var enabled = await _fixture
            .ConnectAsync(chroot: _fixture.Root, enableTtlNodes: true)
            .ConfigureAwait(true);
        await using var lifetime = enabled.ConfigureAwait(true);
        var options = TtlTestSupport.Options(mode);
        if (multi)
        {
            var error = await Assert
                .ThrowsAsync<ZooKeeperMultiException>(() =>
                    enabled.MultiAsync(
                        [
                            ZooKeeperWriteOperation.Create(
                                "/ordinary",
                                null,
                                LiveZooKeeperFixture.OwnerCreate
                            ),
                            ZooKeeperWriteOperation.Create("/ttl", null, options),
                        ],
                        TestContext.Current.CancellationToken
                    )
                )
                .ConfigureAwait(true);
            Assert.Equal(ZooKeeperErrorCode.Unimplemented, error.KnownErrorCode);
            Assert.Equal(RequestOutcome.Rejected, error.Outcome);
            Assert.Equal(1, error.FailedMemberIndex);
            Assert.Equal(2, error.Diagnostics.Count);
            Assert.Null(
                await enabled
                    .ExistsAsync("/ordinary", TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            );
        }
        else
        {
            var error = await Assert
                .ThrowsAsync<ZooKeeperException>(() =>
                    enabled.CreateAsync(
                        "/ttl",
                        null,
                        options,
                        TestContext.Current.CancellationToken
                    )
                )
                .ConfigureAwait(true);
            Assert.Equal(ZooKeeperErrorCode.Unimplemented, error.KnownErrorCode);
            Assert.Equal(RequestOutcome.Rejected, error.Outcome);
            Assert.Equal(ZooKeeperOperation.CreateTtl, error.Request.Operation);
            Assert.Equal("/ttl", error.Request.Path);
            Assert.NotNull(error.Request.RequestId);
        }
        Assert.Null(
            await enabled
                .ExistsAsync("/ttl", TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Empty(
            (
                await enabled
                    .GetChildrenAsync("/", TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            ).Children
        );
        Assert.Equal(ZooKeeperSessionState.Connected, enabled.Session.State);
    }
}
