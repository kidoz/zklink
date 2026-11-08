using ZkLink.Samples.Basic;

namespace ZkLink.Tests;

public sealed class SampleSecurityTests
{
    [Theory]
    [InlineData("127.0.0.1:2181,127.0.0.2:2182/app")]
    [InlineData("[::1]:2181")]
    [InlineData("[::ffff:127.0.0.1]:2181")]
    [Trait("Acceptance", "T21")]
    public void PlaintextDigestIsLimitedToLoopbackFixtures(string connectionString)
    {
        using var authentication = ZooKeeperAuthentication.Digest("user", "disposable");

        var options = SampleClientOptionsFactory.Create(
            Arguments(connectionString, true),
            authentication
        );

        Assert.False(options.Tls.Enabled);
        Assert.True(options.AllowUnsafePlaintextDigest);
    }

    [Theory]
    [InlineData("localhost:2181")]
    [InlineData("remote.example:2181")]
    [InlineData("127.0.0.1:2181,remote.example:2181")]
    [InlineData("[2001:db8::1]:2181")]
    [InlineData("localhost.attacker.example:2181")]
    [InlineData("127.0.0.1@remote.example:2181")]
    [InlineData("localhost:2181,")]
    [InlineData("0.0.0.0:2181")]
    [InlineData("[::]:2181")]
    [InlineData("loopback:2181")]
    [InlineData("[::ffff:192.0.2.1]:2181")]
    [Trait("Acceptance", "T21")]
    public void PlaintextSampleRejectsRemoteOrAmbiguousEndpoints(string connectionString)
    {
        using var authentication = ZooKeeperAuthentication.Digest("user", "disposable");

        var error = Assert.Throws<ArgumentException>(() =>
            SampleClientOptionsFactory.Create(Arguments(connectionString, true), authentication)
        );

        Assert.DoesNotContain("disposable", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Acceptance", "T21")]
    public void RemoteSampleUsesTlsWithoutTheUnsafeDigestOverride()
    {
        using var authentication = ZooKeeperAuthentication.Digest("user", "disposable");

        var options = SampleClientOptionsFactory.Create(
            Arguments("remote.example:2281", false),
            authentication
        );

        Assert.True(options.Tls.Enabled);
        Assert.False(options.AllowUnsafePlaintextDigest);
    }

    private static SampleArguments Arguments(string connectionString, bool plaintext) =>
        new(connectionString, plaintext, false, false, false, false);
}
