using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ZkLink.Benchmarks;

internal static class WatchFanoutBenchmark
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Harness boundary preserves the first failure, observes/disposes every owned consumer and client, writes only safe metadata, and returns nonzero on all failures."
    )]
    internal static async Task<int> RunAsync(LiveSettings settings)
    {
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(settings.Seconds + LiveSettings.CaseOverheadSeconds)
        );
        using var counters = new WatchCounters();
        var token = deadline.Token;
        string user = "zklink-watch-benchmark-" + Guid.NewGuid().ToString("N");
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        string root = "/zklink-watch-benchmark-" + Guid.NewGuid().ToString("N");
        string path = root + "/watched";
        var initial = new byte[CodecWorkload.SmallPayloadBytes];
        var alternate = new byte[CodecWorkload.SmallPayloadBytes];
        initial.AsSpan().Fill(42);
        alternate.AsSpan().Fill(43);
        ZooKeeperClient? owner = null;
        ZooKeeperClient? control = null;
        var consumers = new List<WatchConsumer>(settings.Concurrency);
        WatchFanoutInterval? interval = null;
        WatchFanoutInterval? warmup = null;
        Task<WatchReceipt?[]>? closingReads = null;
        Exception? failure = null;
        string activity = "setup";
        bool ownerRetained = false;
        bool mutatorRetained = false;
        bool acknowledgedClose = false;
        bool streamsCompleted = false;
        bool countersZero = false;
        bool namespaceRemoved = false;
        var state = new WatchMutationState();
        try
        {
            control = await LiveBenchmark
                .ConnectAsync(settings, user, password, token)
                .ConfigureAwait(false);
            await control
                .CreateAsync(root, null, LiveBenchmark.OwnerCreate, token)
                .ConfigureAwait(false);
            await control
                .CreateAsync(path, initial, LiveBenchmark.OwnerCreate, token)
                .ConfigureAwait(false);
            owner = await LiveBenchmark
                .ConnectAsync(settings, user, password, token, settings.Concurrency)
                .ConfigureAwait(false);
            long ownerSession = owner.Session.SessionId;
            long ownerGeneration = owner.Session.ConnectionGeneration;
            long controlSession = control.Session.SessionId;
            long controlGeneration = control.Session.ConnectionGeneration;
            for (int index = 0; index < settings.Concurrency; index++)
            {
                var watch = await owner
                    .AddWatchAsync(path, PersistentWatchMode.Persistent, token)
                    .ConfigureAwait(false);
                consumers.Add(new WatchConsumer(watch, token));
            }
            if (
                consumers
                    .Select(static consumer => consumer.Watch.RegistrationId)
                    .Distinct()
                    .Count() != settings.Concurrency
            )
            {
                throw new InvalidDataException(
                    "Shared watches must have distinct logical identities."
                );
            }
            await counters.RequireAsync(settings.Concurrency, token).ConfigureAwait(false);
            activity = "warmup";
            warmup = new WatchFanoutInterval(record: false);
            await warmup
                .RunAsync(
                    owner,
                    control,
                    path,
                    consumers,
                    state,
                    counters,
                    initial,
                    alternate,
                    LiveSettings.WarmupSeconds,
                    token
                )
                .ConfigureAwait(false);
            activity = "measurement";
            interval = new WatchFanoutInterval(record: true);
            await interval
                .RunAsync(
                    owner,
                    control,
                    path,
                    consumers,
                    state,
                    counters,
                    initial,
                    alternate,
                    settings.Seconds,
                    token
                )
                .ConfigureAwait(false);
            ownerRetained =
                owner.Session.SessionId == ownerSession
                && owner.Session.ConnectionGeneration == ownerGeneration
                && owner.Session.State == ZooKeeperSessionState.Connected;
            mutatorRetained =
                control.Session.SessionId == controlSession
                && control.Session.ConnectionGeneration == controlGeneration
                && control.Session.State == ZooKeeperSessionState.Connected;
            if (!ownerRetained || !mutatorRetained)
            {
                throw new InvalidDataException("Session changed during watch benchmark.");
            }
            activity = "close";
            var endings = consumers.Select(static consumer => consumer.ReadAsync()).ToArray();
            if (endings.Any(static read => read.IsCompleted))
            {
                throw new InvalidDataException(
                    "Unexpected buffered watch delivery after measurement."
                );
            }
            await owner.CloseAsync(token).ConfigureAwait(false);
            acknowledgedClose = owner.Session.CloseStatus == ZooKeeperCloseStatus.Acknowledged;
            closingReads = Task.WhenAll(endings);
            var remaining = await closingReads
                .WaitAsync(LiveSettings.OperationGuard, token)
                .ConfigureAwait(false);
            streamsCompleted = remaining.All(static value => value is null);
            if (!acknowledgedClose || !streamsCompleted)
            {
                throw new InvalidDataException("Watch close/stream completion mismatch.");
            }
            await counters.RequireAsync(0, token).ConfigureAwait(false);
            countersZero = true;
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            foreach (var consumer in consumers)
            {
                try
                {
                    await consumer.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    failure ??= error;
                }
            }
            foreach (
                var pending in new[] { warmup?.PendingReads, interval?.PendingReads, closingReads }
            )
            {
                try
                {
                    await WatchConsumer.ObserveAsync(pending).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    failure ??= error;
                }
            }
            try
            {
                if (owner is not null)
                {
                    await owner.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                failure ??= error;
            }
            if (control is not null)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(LiveSettings.CleanupGuard);
                    await LiveBenchmark
                        .DeleteAsync(control, root, cleanup.Token)
                        .ConfigureAwait(false);
                    await control.SyncAsync("/", cleanup.Token).ConfigureAwait(false);
                    namespaceRemoved =
                        await control.ExistsAsync(root, cleanup.Token).ConfigureAwait(false)
                            is null;
                    await counters.RequireAsync(0, cleanup.Token).ConfigureAwait(false);
                    countersZero = true;
                    if (!namespaceRemoved && failure is null)
                    {
                        activity = "cleanup";
                        failure = new InvalidDataException(
                            "Watch benchmark namespace remains after cleanup."
                        );
                    }
                }
                catch (Exception error)
                {
                    if (failure is null)
                    {
                        activity = "cleanup";
                        failure = error;
                    }
                }
                finally
                {
                    try
                    {
                        await control.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        failure ??= error;
                    }
                }
            }
        }
        bool passed =
            failure is null
            && ownerRetained
            && mutatorRetained
            && acknowledgedClose
            && streamsCompleted
            && countersZero
            && namespaceRemoved;
        var measurement = interval?.Result(consumers);
        var report = new
        {
            Status = passed ? "passed" : "failed",
            Activity = passed ? "complete" : activity,
            ErrorType = failure?.GetType().Name,
            ServerErrorCode = (failure as ZooKeeperException)?.ErrorCode,
            settings.Version,
            settings.Endpoint,
            Consumers = settings.Concurrency,
            PayloadBytes = CodecWorkload.SmallPayloadBytes,
            RequestedSeconds = settings.Seconds,
            LiveSettings.WarmupSeconds,
            EventBudget = settings.Concurrency,
            MaxWatches = settings.Concurrency,
            SamplingIntervalMilliseconds = LiveSettings.SampleInterval.TotalMilliseconds,
            OwnerSessionRetained = ownerRetained,
            MutatorSessionRetained = mutatorRetained,
            AcknowledgedClose = acknowledgedClose,
            StreamsCompleted = streamsCompleted,
            FinalCountersZero = countersZero,
            NamespaceRemoved = namespaceRemoved,
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
                $"watch-fanout {settings.Version} consumers={settings.Concurrency} status={(passed ? "passed" : "failed")} mutations={measurement?.MutationsCompleted ?? 0} deliveries={measurement?.DeliveriesCompleted ?? 0} error={failure?.GetType().Name ?? "none"}"
            )
        );
        return passed ? 0 : 1;
    }
}
