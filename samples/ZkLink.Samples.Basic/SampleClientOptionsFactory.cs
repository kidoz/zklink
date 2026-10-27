using ZkLink;

namespace ZkLink.Samples.Basic;

internal static class SampleClientOptionsFactory
{
    internal static ZooKeeperClientOptions Create(
        SampleArguments arguments,
        ZooKeeperAuthentication? authentication
    )
    {
        var plaintext = arguments.Plaintext;
        return new ZooKeeperClientOptions
        {
            ConnectionString = arguments.ConnectionString,
            Tls = plaintext ? ZooKeeperTlsOptions.Disabled() : ZooKeeperTlsOptions.Required(),
            Authentication = authentication is null ? [] : [authentication],
            // This switch is for the explicitly selected local plaintext fixture only.
            AllowUnsafePlaintextDigest = plaintext,
        };
    }
}
