using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ZkLink.Benchmarks;

internal static class LiveBenchmark
{
    internal static readonly CreateOptions OwnerCreate = new([
        new(ZooKeeperPermissions.All, "auth", ""),
    ]);
    private static readonly TimeSpan ConnectGuard = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AttemptGuard = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CloseGuard = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(30);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "This harness boundary preserves primary failure, always disposes independent owners, writes only safe error metadata and returns nonzero for every failure."
    )]
    internal static async Task<int> RunAsync(LiveSettings settings)
    {
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(settings.Seconds + LiveSettings.CaseOverheadSeconds)
        );
        var token = deadline.Token;
        string user = "zklink-benchmark-" + Guid.NewGuid().ToString("N");
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        string root = "/zklink-benchmark-" + Guid.NewGuid().ToString("N");
        var workers = Enumerable
            .Range(0, settings.Concurrency)
            .Select(index => new LiveWorker(
                root + "/n-" + index.ToString(CultureInfo.InvariantCulture)
            ))
            .ToArray();
        var initial = new byte[settings.PayloadBytes];
        var alternate = new byte[settings.PayloadBytes];
        initial.AsSpan().Fill(42);
        alternate.AsSpan().Fill(43);
        ZooKeeperClient? control = null;
        ZooKeeperClient? client = null;
        LiveInterval? interval = null;
        Exception? failure = null;
        string activity = "setup";
        bool sessionRetained = false;
        bool acknowledgedClose = false;
        bool cleaned = false;
        try
        {
            control = await ConnectAsync(settings, user, password, token).ConfigureAwait(false);
            await control.CreateAsync(root, null, OwnerCreate, token).ConfigureAwait(false);
            for (int offset = 0; offset < workers.Length; offset += LiveSettings.MediumConcurrency)
            {
                await Task.WhenAll(
                        workers
                            .Skip(offset)
                            .Take(LiveSettings.MediumConcurrency)
                            .Select(worker =>
                                control.CreateAsync(worker.Path, initial, OwnerCreate, token)
                            )
                    )
                    .ConfigureAwait(false);
            }
            client = await ConnectAsync(settings, user, password, token).ConfigureAwait(false);
            long session = client.Session.SessionId;
            long generation = client.Session.ConnectionGeneration;
            activity = "warmup";
            await new LiveInterval(workers, record: false)
                .RunAsync(client, settings, initial, alternate, LiveSettings.WarmupSeconds, token)
                .ConfigureAwait(false);
            foreach (var worker in workers)
            {
                worker.Started = 0;
                worker.Completed = 0;
            }
            activity = "measurement";
            interval = new LiveInterval(workers, record: true);
            await interval
                .RunAsync(client, settings, initial, alternate, settings.Seconds, token)
                .ConfigureAwait(false);
            sessionRetained =
                client.Session.SessionId == session
                && client.Session.ConnectionGeneration == generation
                && client.Session.State == ZooKeeperSessionState.Connected;
            if (!sessionRetained)
            {
                throw new InvalidDataException("Session changed during benchmark.");
            }
            activity = "validation";
            await control.SyncAsync(root, token).ConfigureAwait(false);
            foreach (var worker in workers)
            {
                var result = await control.GetDataAsync(worker.Path, token).ConfigureAwait(false);
                byte[] expected = worker.Alternate ? alternate : initial;
                if (
                    result.Data is not { } data
                    || !data.Span.SequenceEqual(expected)
                    || result.Stat.Version != worker.Version
                )
                {
                    throw new InvalidDataException("Final benchmark data validation failed.");
                }
            }
            activity = "close";
            await client.CloseAsync(token).ConfigureAwait(false);
            acknowledgedClose = client.Session.CloseStatus == ZooKeeperCloseStatus.Acknowledged;
            if (!acknowledgedClose)
            {
                throw new InvalidDataException("Benchmark close was not acknowledged.");
            }
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            try
            {
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                failure ??= error;
            }
            try
            {
                if (control is not null)
                {
                    try
                    {
                        using var cleanup = new CancellationTokenSource(LiveSettings.CleanupGuard);
                        await DeleteAsync(control, root, cleanup.Token).ConfigureAwait(false);
                        await control.SyncAsync("/", cleanup.Token).ConfigureAwait(false);
                        cleaned =
                            await control.ExistsAsync(root, cleanup.Token).ConfigureAwait(false)
                                is null;
                        if (!cleaned && failure is null)
                        {
                            failure = new InvalidDataException(
                                "Benchmark namespace remains after cleanup."
                            );
                            activity = "cleanup";
                        }
                    }
                    finally
                    {
                        await control.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception error)
            {
                if (failure is null)
                {
                    activity = "cleanup";
                    failure = error;
                }
                cleaned = false;
            }
        }
        var measurement = interval?.Result();
        bool passed = failure is null && cleaned && sessionRetained && acknowledgedClose;
        var report = new
        {
            Status = passed ? "passed" : "failed",
            Activity = passed ? "complete" : activity,
            ErrorType = failure?.GetType().Name,
            ServerErrorCode = (failure as ZooKeeperException)?.ErrorCode,
            RequestOutcome = (failure as ZooKeeperException)?.Outcome.ToString(),
            settings.Version,
            settings.Endpoint,
            Operation = settings.Write ? "set" : "get",
            settings.PayloadBytes,
            settings.Concurrency,
            RequestedSeconds = settings.Seconds,
            LiveSettings.WarmupSeconds,
            OperationTimeoutMilliseconds = LiveSettings.OperationGuard.TotalMilliseconds,
            SamplingIntervalMilliseconds = LiveSettings.SampleInterval.TotalMilliseconds,
            SessionRetained = sessionRetained,
            AcknowledgedClose = acknowledgedClose,
            NamespaceRemoved = cleaned,
            TimingGate = false,
            Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Measurement = measurement,
        };
        await LiveReport
            .WriteAsync(settings.Output, report, measurement?.Histogram)
            .ConfigureAwait(false);
        Console.WriteLine(
            FormattableString.Invariant(
                $"live {settings.Version} {(settings.Write ? "set" : "get")} bytes={settings.PayloadBytes} concurrency={settings.Concurrency} status={(passed ? "passed" : "failed")} completed={measurement?.Completed ?? 0} error={failure?.GetType().Name ?? "none"}"
            )
        );
        return passed ? 0 : 1;
    }

    internal static async Task<ZooKeeperClient> ConnectAsync(
        LiveSettings settings,
        string user,
        string password,
        CancellationToken token,
        int? watchLimit = null,
        int? eventBudget = null
    )
    {
        var defaults = new ZooKeeperClientOptions { ConnectionString = settings.Endpoint };
        using var authentication = ZooKeeperAuthentication.Digest(user, password);
        return await ZooKeeperClient
            .ConnectAsync(
                new ZooKeeperClientOptions
                {
                    ConnectionString = settings.Endpoint,
                    CompatibilityProfile =
                        settings.Version == "3.9.6"
                            ? ZooKeeperCompatibilityProfile.ZooKeeper39
                            : ZooKeeperCompatibilityProfile.ZooKeeper38,
                    Tls = ZooKeeperTlsOptions.Disabled(),
                    AllowUnsafePlaintextDigest = true,
                    Authentication = [authentication],
                    InitialConnectTimeout = ConnectGuard,
                    ConnectAttemptTimeout = AttemptGuard,
                    OperationTimeout = LiveSettings.OperationGuard,
                    CloseTimeout = CloseGuard,
                    SessionTimeout = SessionTimeout,
                    MaxWatches = watchLimit ?? defaults.MaxWatches,
                    MaxBufferedEvents = eventBudget ?? watchLimit ?? defaults.MaxBufferedEvents,
                },
                token
            )
            .ConfigureAwait(false);
    }

    internal static async Task DeleteAsync(
        ZooKeeperClient client,
        string root,
        CancellationToken token
    )
    {
        try
        {
            var children = await client.GetChildrenAsync(root, token).ConfigureAwait(false);
            foreach (string child in children.Children)
            {
                await client
                    .DeleteAsync(root + "/" + child, ZNodeVersion.Any, token)
                    .ConfigureAwait(false);
            }
            await client.DeleteAsync(root, ZNodeVersion.Any, token).ConfigureAwait(false);
        }
        catch (ZooKeeperException error) when (error.KnownErrorCode == ZooKeeperErrorCode.NoNode)
        { }
    }
}
