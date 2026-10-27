namespace ZkLink.Samples.Basic;

internal static class SampleArgumentParser
{
    internal const string Usage =
        "Usage: ZkLink.Samples.Basic host:port[,host:port] [--plaintext] [--nodes] [--transactions] [--auxiliary] [--watches]";

    internal const string AuthenticationError =
        "Set both ZKLINK_USER and ZKLINK_PASSWORD; --nodes, --transactions, --auxiliary and --watches require an authenticated creator ACL.";

    private static readonly string[] OptionalFlags =
    [
        "--plaintext",
        "--nodes",
        "--transactions",
        "--auxiliary",
        "--watches",
    ];

    internal static SampleParseResult Parse(string[] args, SampleCredentials credentials)
    {
        if (
            args.Length is < 1
            || args.Length > OptionalFlags.Length + 1
            || args.Skip(1)
                .Any(static value => !OptionalFlags.Contains(value, StringComparer.Ordinal))
        )
        {
            return SampleParseResult.Failure(Usage);
        }

        var arguments = new SampleArguments(
            args[0],
            args.Contains("--plaintext", StringComparer.Ordinal),
            args.Contains("--nodes", StringComparer.Ordinal),
            args.Contains("--transactions", StringComparer.Ordinal),
            args.Contains("--auxiliary", StringComparer.Ordinal),
            args.Contains("--watches", StringComparer.Ordinal)
        );
        if (credentials.IsMismatched || (arguments.RequiresAuthentication && !credentials.HasUser))
        {
            return SampleParseResult.Failure(AuthenticationError);
        }

        return SampleParseResult.Success(arguments);
    }
}

internal readonly record struct SampleParseResult(SampleArguments? Arguments, string? Error)
{
    internal static SampleParseResult Success(SampleArguments arguments) => new(arguments, null);

    internal static SampleParseResult Failure(string error) => new(null, error);
}
