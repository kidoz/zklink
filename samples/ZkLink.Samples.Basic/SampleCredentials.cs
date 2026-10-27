using ZkLink;

namespace ZkLink.Samples.Basic;

/// <summary>Optional digest credentials from the process environment.</summary>
internal sealed class SampleCredentials
{
    private readonly string? _user;
    private readonly string? _password;

    private SampleCredentials(string? user, string? password)
    {
        _user = user;
        _password = password;
    }

    internal static SampleCredentials Read() =>
        new(
            Environment.GetEnvironmentVariable("ZKLINK_USER"),
            Environment.GetEnvironmentVariable("ZKLINK_PASSWORD")
        );

    internal bool IsMismatched => (_user is null) != (_password is null);

    internal bool HasUser => _user is not null;

    internal ZooKeeperAuthentication? CreateAuthentication() =>
        _user is null || _password is null
            ? null
            : ZooKeeperAuthentication.Digest(_user, _password);
}
