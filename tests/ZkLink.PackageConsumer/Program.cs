using System.Security.Cryptography;
using ZkLink;
using ZkLink.Samples.Basic;

const int DeadlineSeconds = 30;
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(DeadlineSeconds));
var expectedHash = Environment.GetEnvironmentVariable("ZKLINK_PACKAGE_ASSEMBLY_SHA256");
using (var assembly = File.OpenRead(typeof(ZooKeeperClient).Assembly.Location))
{
    if (
        string.IsNullOrEmpty(expectedHash)
        || Convert.ToHexString(
            await SHA256.HashDataAsync(assembly, deadline.Token).ConfigureAwait(false)
        ) != expectedHash
    )
    {
        await Console
            .Error.WriteLineAsync("The loaded driver does not match the candidate package.")
            .ConfigureAwait(false);
        return 1;
    }
}

await Console.Out.WriteLineAsync("Loaded candidate assembly hash: verified.").ConfigureAwait(false);
var credentials = SampleCredentials.Read();
var parsed = SampleArgumentParser.Parse(args, credentials);
if (parsed.Arguments is not { } arguments)
{
    await Console.Error.WriteLineAsync(parsed.Error).ConfigureAwait(false);
    return 2;
}

using var authentication = credentials.CreateAuthentication();
var options = SampleClientOptionsFactory.Create(arguments, authentication);
return await new SampleSession(Console.Out, Console.Error)
    .RunAsync(
        options,
        [new NodeSample(), new TransactionSample(), new AuxiliarySample(), new WatchSample()],
        deadline.Token
    )
    .ConfigureAwait(false);
