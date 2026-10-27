namespace ZkLink.Samples.Basic;

/// <summary>Validated command-line selection. Credentials stay on <see cref="SampleCredentials"/>.</summary>
internal sealed record SampleArguments(
    string ConnectionString,
    bool Plaintext,
    bool Nodes,
    bool Transactions,
    bool Auxiliary,
    bool Watches
)
{
    internal bool RequiresAuthentication => Nodes || Transactions || Auxiliary || Watches;
}
