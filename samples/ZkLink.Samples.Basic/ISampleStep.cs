using ZkLink;

namespace ZkLink.Samples.Basic;

/// <summary>One optional demonstration selected by a command-line flag.</summary>
internal interface ISampleStep
{
    /// <summary>Runs this demonstration and returns its completion line.</summary>
    Task<string> RunAsync(ZooKeeperClient client, CancellationToken cancellationToken);
}
