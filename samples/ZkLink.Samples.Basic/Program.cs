using ZkLink.Samples.Basic;

var credentials = SampleCredentials.Read();
var parsed = SampleArgumentParser.Parse(args, credentials);
if (parsed.Arguments is not { } arguments)
{
    await Console.Error.WriteLineAsync(parsed.Error).ConfigureAwait(false);
    return 2;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var authentication = credentials.CreateAuthentication();
var options = SampleClientOptionsFactory.Create(arguments, authentication);
return await new SampleSession(Console.Out, Console.Error)
    .RunAsync(options, SelectSteps(arguments), deadline.Token)
    .ConfigureAwait(false);

static List<ISampleStep> SelectSteps(SampleArguments arguments)
{
    var steps = new List<ISampleStep>(4);
    if (arguments.Nodes)
    {
        steps.Add(new NodeSample());
    }

    if (arguments.Transactions)
    {
        steps.Add(new TransactionSample());
    }

    if (arguments.Auxiliary)
    {
        steps.Add(new AuxiliarySample());
    }

    if (arguments.Watches)
    {
        steps.Add(new WatchSample());
    }

    return steps;
}
