namespace ZkLink.Tests;

public sealed class EndpointContractTests
{
    [Theory]
    [InlineData("zk.example")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    [Trait("Acceptance", "T05")]
    public void TypedEndpointsPreserveHostAndExplicitPort(string host)
    {
        var endpoint = new ZooKeeperEndpoint(host, 2281);
        Assert.Equal(host, endpoint.Host);
        Assert.Equal(2281, endpoint.Port);
        Assert.Equal(2181, new ZooKeeperEndpoint(host).Port);
    }

    [Theory]
    [InlineData("user:secret@zk.example")]
    [InlineData("zk.example/app")]
    [InlineData("zk.example:2181")]
    [InlineData("[::1]")]
    [InlineData("zk.example?password=value")]
    [InlineData("zk.example#fragment")]
    [InlineData(" zk.example")]
    [InlineData("")]
    [Trait("Acceptance", "T05")]
    public void TypedEndpointsRejectEmbeddedUriAndConnectionStringParts(string host) =>
        Assert.ThrowsAny<ArgumentException>(() => new ZooKeeperEndpoint(host));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void InvalidPortsFail(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZooKeeperEndpoint("zk.example", port));
}
