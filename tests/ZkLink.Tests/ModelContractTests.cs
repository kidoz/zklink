using System.Collections;

namespace ZkLink.Tests;

public sealed class ModelContractTests
{
    [Fact]
    [Trait("Acceptance", "T15")]
    public void DataCopiesCallerMemoryAndDistinguishesNullFromEmpty()
    {
        byte[] bytes = [1, 2, 3];
        var snapshot = new ZNodeData(bytes, default);
        bytes[0] = 255;
        Assert.Equal(new byte[] { 1, 2, 3 }, snapshot.Data!.Value.ToArray());
        Assert.Null(new ZNodeData(null, default).Data);
        var empty = new ZNodeData(ReadOnlyMemory<byte>.Empty, default).Data;
        Assert.True(empty.HasValue);
        Assert.True(empty.Value.IsEmpty);
    }

    [Fact]
    [Trait("Acceptance", "T15")]
    public void ChildrenAndAclsSnapshotMutableCollections()
    {
        var children = new List<string> { "first", "second" };
        var result = new ZNodeChildren(children, default);
        children[0] = "changed";
        children.Clear();
        Assert.Equal(2, result.Children.Count);
        Assert.Equal("first", result.Children[0]);
        Assert.Equal("second", result.Children[1]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.Children)[0] = "changed");

        var acl = new List<ZooKeeperAcl> { ZooKeeperAcl.OpenUnsafe };
        var options = new CreateOptions(acl);
        var aclResult = new ZNodeAcl(acl, default);
        acl.Clear();
        Assert.Single(options.Acl);
        Assert.Single(aclResult.Acl);
        Assert.Throws<NotSupportedException>(() => ((IList<ZooKeeperAcl>)options.Acl).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ZooKeeperAcl>)aclResult.Acl).Clear());
    }

    [Fact]
    public void OversizeCollectionsFailBeforeReadingEntries()
    {
        var input = new OversizedList<string>();
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZNodeChildren(input, default));
        Assert.False(input.EntryRead);
    }

    [Fact]
    [Trait("Acceptance", "T19")]
    public void CreateRequiresExplicitAclsAndRejectsUnknownPermissions()
    {
        Assert.Throws<ArgumentNullException>(() => new CreateOptions(null!));
        Assert.Throws<ArgumentException>(() => new CreateOptions([]));
        Assert.Throws<ArgumentException>(() => new CreateOptions([null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ZooKeeperAcl((ZooKeeperPermissions)32, "world", "anyone")
        );
        Assert.Equal(ZooKeeperPermissions.All, ZooKeeperAcl.OpenUnsafe.Permissions);
        Assert.Equal("world", ZooKeeperAcl.OpenUnsafe.Scheme);
        Assert.Equal("anyone", ZooKeeperAcl.OpenUnsafe.Id);
        var saslAcl = new ZooKeeperAcl(ZooKeeperPermissions.Read, "sasl", "principal");
        Assert.Equal("sasl", saslAcl.Scheme);
    }

    [Theory]
    [InlineData(CreateMode.PersistentWithTtl)]
    [InlineData(CreateMode.PersistentSequentialWithTtl)]
    [Trait("Acceptance", "T17")]
    public void TtlModesRequirePositiveWholeMillisecondsWithinExactBound(CreateMode mode)
    {
        ZooKeeperAcl[] acl = [ZooKeeperAcl.OpenUnsafe];
        Assert.Throws<ArgumentException>(() => new CreateOptions(acl, mode));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CreateOptions(acl, mode, TimeSpan.Zero)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CreateOptions(acl, mode, TimeSpan.FromTicks(-1))
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CreateOptions(acl, mode, TimeSpan.FromTicks(1))
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CreateOptions(acl, mode, TimeSpan.FromTicks(10_001))
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CreateOptions(
                acl,
                mode,
                TimeSpan.FromMilliseconds(CreateOptions.MaxTtlMilliseconds + 1)
            )
        );
        var maximum = TimeSpan.FromTicks(
            CreateOptions.MaxTtlMilliseconds * TimeSpan.TicksPerMillisecond
        );
        Assert.Equal(maximum, new CreateOptions(acl, mode, maximum).Ttl);
        Assert.Equal(
            TimeSpan.FromMilliseconds(1),
            new CreateOptions(acl, mode, TimeSpan.FromMilliseconds(1)).Ttl
        );
    }

    [Theory]
    [InlineData(CreateMode.Persistent)]
    [InlineData(CreateMode.Ephemeral)]
    [InlineData(CreateMode.PersistentSequential)]
    [InlineData(CreateMode.EphemeralSequential)]
    [InlineData(CreateMode.Container)]
    [Trait("Acceptance", "T17")]
    public void OrdinaryModesRejectTtl(CreateMode mode)
    {
        Assert.Throws<ArgumentException>(() =>
            new CreateOptions([ZooKeeperAcl.OpenUnsafe], mode, TimeSpan.FromSeconds(1))
        );
        Assert.Null(new CreateOptions([ZooKeeperAcl.OpenUnsafe], mode).Ttl);
    }

    [Fact]
    public void UnknownCreateModesFailLocally() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CreateOptions([ZooKeeperAcl.OpenUnsafe], (CreateMode)7)
        );

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    [Trait("Acceptance", "T15")]
    public void VersionsPreserveAllSignedValues(int value) =>
        Assert.Equal(value, ZNodeVersion.Exact(value).Value);

    [Fact]
    public void DefaultVersionAndWildcardAreExplicit()
    {
        Assert.Equal(0, default(ZNodeVersion).Value);
        Assert.Equal(-1, ZNodeVersion.Any.Value);
        Assert.Equal(ZNodeVersion.Any, ZNodeVersion.Exact(-1));
    }

    [Fact]
    [Trait("Acceptance", "T34")]
    public void DefaultModelFormattingDoesNotRevealSensitiveValues()
    {
        const string marker = "sensitive-marker";
        object[] models =
        [
            new ZNodeData(System.Text.Encoding.UTF8.GetBytes(marker), default),
            new ZNodeChildren([marker], default),
            new CreateResult("/" + marker, default),
            new ZooKeeperAcl(ZooKeeperPermissions.Read, "digest", marker),
            new ZooKeeperClientIdentity("x509", marker),
            new ZooKeeperEndpoint(marker + ".example"),
            new ZooKeeperRequestInfo(ZooKeeperOperation.GetData, path: "/" + marker),
            new ZooKeeperMultiDiagnostic(0, 15, 0, "/" + marker),
        ];
        foreach (var model in models)
        {
            Assert.DoesNotContain(marker, model.ToString(), StringComparison.Ordinal);
        }
    }

    private sealed class OversizedList<T> : IReadOnlyList<T>
    {
        public int Count => 1_000_001;
        public bool EntryRead { get; private set; }
        public T this[int index]
        {
            get
            {
                EntryRead = true;
                throw new InvalidOperationException("Must reject before access.");
            }
        }

        public IEnumerator<T> GetEnumerator() =>
            throw new InvalidOperationException("Must reject before enumeration.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
