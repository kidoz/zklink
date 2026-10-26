namespace ZkLink.IntegrationTests;

public sealed class AuxiliaryOperationsTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 60_000;
    private readonly LiveZooKeeperFixture _fixture = new();
    private ZooKeeperClient Client => _fixture.Client;
    private string Root => _fixture.Root;
    private static CreateOptions OwnerCreate => LiveZooKeeperFixture.OwnerCreate;
    private static readonly string[] ScopedEphemerals = ["/tree/deep/leaf", "/tree2"];
    private static readonly CreateOptions Ephemeral = new(OwnerCreate.Acl, CreateMode.Ephemeral);

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T22")]
    public async Task EphemeralsBelongToTheSessionAndUseTextualPrefixesWithoutRequiringAParent()
    {
        await Client
            .CreateAsync(Root + "/node", null, Ephemeral, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await Client
            .CreateAsync(Root + "/node2", null, Ephemeral, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await Client
            .CreateAsync(
                Root + "/persistent",
                null,
                OwnerCreate,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        var other = await _fixture.ConnectAsync().ConfigureAwait(true);
        await using var otherLifetime = other.ConfigureAwait(true);
        await other
            .CreateAsync(Root + "/other", null, Ephemeral, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        IReadOnlyList<string> own = await Client
            .GetEphemeralsAsync(Root, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(new[] { Root + "/node", Root + "/node2" }, own.Order(StringComparer.Ordinal));
        Assert.Equal(
            new[] { Root + "/other" },
            await other
                .GetEphemeralsAsync(Root, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            new[] { Root + "/node", Root + "/node2" },
            (
                await Client
                    .GetEphemeralsAsync(Root + "/nod", TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            ).Order(StringComparer.Ordinal)
        );
        Assert.Empty(
            await Client
                .GetEphemeralsAsync(Root + "/missing", TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Throws<NotSupportedException>(() => ((IList<string>)own).Clear());
        await other.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(2, own.Count);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T22")]
    public async Task ChrootIsAppliedOnceToEphemeralPrefixesAndRecursiveCounts()
    {
        var scoped = await _fixture.ConnectAsync(chroot: Root).ConfigureAwait(true);
        await using var scopedLifetime = scoped.ConfigureAwait(true);
        await scoped
            .CreateAsync("/tree", null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await scoped
            .CreateAsync("/tree/deep", null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await scoped
            .CreateAsync("/tree/deep/leaf", null, Ephemeral, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await scoped
            .CreateAsync("/tree2", null, Ephemeral, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        IReadOnlyList<string> ephemerals = await scoped
            .GetEphemeralsAsync("/tree", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(ScopedEphemerals, ephemerals.Order(StringComparer.Ordinal));
        Assert.Equal(
            ephemerals.Order(StringComparer.Ordinal),
            (
                await scoped
                    .GetEphemeralsAsync("/", TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            ).Order(StringComparer.Ordinal)
        );
        Assert.Equal(
            4,
            await scoped
                .GetAllChildrenNumberAsync("/", TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            2,
            await scoped
                .GetAllChildrenNumberAsync("/tree", TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(
            0,
            await scoped
                .GetAllChildrenNumberAsync("/tree/deep/leaf", TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T22")]
    public async Task DescendantCountChecksReadOnTheQueriedNodeAndPreservesMissingAndDeniedErrors()
    {
        string tree = Root + "/tree";
        var readable = new CreateOptions([
            new(ZooKeeperPermissions.All, "auth", ""),
            new(ZooKeeperPermissions.Read, "world", "anyone"),
        ]);
        await Client
            .CreateAsync(tree, null, readable, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await Client
            .CreateAsync(
                tree + "/private",
                null,
                OwnerCreate,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await Client
            .CreateAsync(
                tree + "/private/leaf",
                null,
                OwnerCreate,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await Client
            .CreateAsync(Root + "/tree2", null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var anonymous = await _fixture.ConnectAsync(authenticated: false).ConfigureAwait(true);
        await using var anonymousLifetime = anonymous.ConfigureAwait(true);
        Assert.Equal(
            2,
            await anonymous
                .GetAllChildrenNumberAsync(tree, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        ZooKeeperException denied = await Assert
            .ThrowsAsync<ZooKeeperException>(() =>
                anonymous.GetAllChildrenNumberAsync(
                    tree + "/private",
                    TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperErrorCode.NoAuth, denied.KnownErrorCode);
        Assert.Equal(RequestOutcome.Rejected, denied.Outcome);
        ZooKeeperException missing = await Assert
            .ThrowsAsync<ZooKeeperException>(() =>
                Client.GetAllChildrenNumberAsync(
                    Root + "/missing",
                    TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperErrorCode.NoNode, missing.KnownErrorCode);
        Assert.Equal(Root + "/missing", missing.Request.Path);
        Assert.Equal(ZooKeeperOperation.GetAllChildrenNumber, missing.Request.Operation);
        Assert.Equal(ZooKeeperSessionState.Connected, anonymous.Session.State);
        Assert.Equal(
            2,
            await anonymous
                .GetAllChildrenNumberAsync(tree, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T22")]
    public async Task WhoAmIReportsServerIdentitiesForAuthenticatedAndAnonymousConnections()
    {
        IReadOnlyList<ZooKeeperClientIdentity> identities = await Client
            .WhoAmIAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        ZooKeeperClientIdentity digest = Assert.Single(
            identities,
            value => value.Scheme == "digest"
        );
        Assert.Equal(_fixture.User, digest.User);
        Assert.DoesNotContain(_fixture.User, digest.ToString(), StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ZooKeeperClientIdentity>)identities).Clear()
        );
        var anonymous = await _fixture.ConnectAsync(authenticated: false).ConfigureAwait(true);
        await using var anonymousLifetime = anonymous.ConfigureAwait(true);
        IReadOnlyList<ZooKeeperClientIdentity> anonymousIdentities = await anonymous
            .WhoAmIAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.DoesNotContain(anonymousIdentities, value => value.Scheme == "digest");
        Assert.Contains(
            anonymousIdentities,
            value => value.Scheme == "ip" && value.User.Length > 0
        );
        var scoped = await _fixture.ConnectAsync(chroot: Root).ConfigureAwait(true);
        await using var scopedLifetime = scoped.ConfigureAwait(true);
        ZooKeeperClientIdentity scopedDigest = Assert.Single(
            await scoped.WhoAmIAsync(TestContext.Current.CancellationToken).ConfigureAwait(true),
            value => value.Scheme == "digest"
        );
        Assert.Equal(digest.User, scopedDigest.User);
    }
}
