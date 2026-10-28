using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ZkLink.Benchmarks;

internal static class WatchRecoveryBenchmark
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Executable harness boundary preserves primary failure, observes/disposes every owned task/client/proxy, reports safe metadata and fails nonzero."
    )]
    internal static async Task<int> RunAsync(RecoverySettings settings)
    {
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(RecoverySettings.CaseGuardSeconds)
        );
        using var counters = new WatchCounters();
        var token = deadline.Token;
        long setupAt = Stopwatch.GetTimestamp();
        string user = "zklink-recovery-" + Guid.NewGuid().ToString("N");
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        string root = "/zklink-recovery-" + Guid.NewGuid().ToString("N");
        string lease = root + "/lease";
        string padding = new('p', RecoverySettings.PathPadding);
        string[] paths = Enumerable
            .Range(0, settings.Watches)
            .Select(index =>
                root + "/n-" + index.ToString("D5", CultureInfo.InvariantCulture) + "-" + padding
            )
            .ToArray();
        byte[] initial = new byte[CodecWorkload.SmallPayloadBytes];
        byte[] changed = new byte[CodecWorkload.SmallPayloadBytes];
        initial.AsSpan().Fill(42);
        changed.AsSpan().Fill(43);
        var consumers = new List<WatchConsumer>(settings.Watches);
        RecoveryProxy? proxy = null;
        ZooKeeperClient? owner = null;
        ZooKeeperClient? control = null;
        var run = new WatchRecoveryRun();
        RecoveryWireObservation? wire = null;
        Exception? failure = null;
        string activity = "setup";
        bool ownerRetained = false;
        bool controlRetained = false;
        bool ephemeralRetained = false;
        bool acknowledgedClose = false;
        bool countersZero = false;
        bool namespaceRemoved = false;
        double setupMilliseconds = 0;
        int admissionProbeTransmissions = 0;
        int eventBudget = checked(settings.Watches * 2);
        try
        {
            control = await LiveBenchmark
                .ConnectAsync(settings.Common, user, password, token)
                .ConfigureAwait(false);
            long controlSession = control.Session.SessionId;
            long controlGeneration = control.Session.ConnectionGeneration;
            await control
                .CreateAsync(root, null, LiveBenchmark.OwnerCreate, token)
                .ConfigureAwait(false);
            for (int offset = 0; offset < paths.Length; offset += RecoverySettings.BatchSize)
            {
                await Task.WhenAll(
                        paths
                            .Skip(offset)
                            .Take(RecoverySettings.BatchSize)
                            .Select(path =>
                                control.CreateAsync(path, initial, LiveBenchmark.OwnerCreate, token)
                            )
                    )
                    .ConfigureAwait(false);
            }
            proxy = new RecoveryProxy(settings.Common.Endpoint, settings.Watches, token);
            owner = await LiveBenchmark
                .ConnectAsync(
                    settings.Common with
                    {
                        Endpoint = proxy.Endpoint,
                    },
                    user,
                    password,
                    token,
                    settings.Watches,
                    eventBudget
                )
                .ConfigureAwait(false);
            long session = owner.Session.SessionId;
            long generation = owner.Session.ConnectionGeneration;
            await owner
                .CreateAsync(
                    lease,
                    null,
                    new CreateOptions(LiveBenchmark.OwnerCreate.Acl, CreateMode.Ephemeral),
                    token
                )
                .ConfigureAwait(false);
            for (int offset = 0; offset < paths.Length; offset += RecoverySettings.BatchSize)
            {
                var watches = await Task.WhenAll(
                        paths
                            .Skip(offset)
                            .Take(RecoverySettings.BatchSize)
                            .Select(path =>
                                owner.AddWatchAsync(path, PersistentWatchMode.Persistent, token)
                            )
                    )
                    .ConfigureAwait(false);
                foreach (var watch in watches)
                {
                    consumers.Add(new WatchConsumer(watch, token));
                }
            }
            if (
                consumers
                    .Select(static consumer => consumer.Watch.Path)
                    .Distinct(StringComparer.Ordinal)
                    .Count() != settings.Watches
                || consumers
                    .Select(static consumer => consumer.Watch.RegistrationId)
                    .Distinct()
                    .Count() != settings.Watches
            )
            {
                throw new InvalidDataException(
                    "Recovery requires distinct paths and logical identities."
                );
            }
            await counters.RequireAsync(settings.Watches, token).ConfigureAwait(false);
            setupMilliseconds = Stopwatch.GetElapsedTime(setupAt).TotalMilliseconds;
            activity = "recovery";
            await run.RecoverAsync(owner, proxy, consumers, root, generation + 1, counters, token)
                .ConfigureAwait(false);
            wire = proxy.Observation();
            admissionProbeTransmissions = wire.SyncRequests;
            ownerRetained =
                owner.Session.SessionId == session
                && owner.Session.ConnectionGeneration == generation + 1
                && owner.Session.State == ZooKeeperSessionState.Connected;
            if (
                !ownerRetained
                || wire.Connections != 2
                || wire.Handshakes != 2
                || wire.AuthenticationRequests != 2
                || wire.SyncRequests != 0
                || wire.AddWatchRequests != settings.Watches
                || wire.RestorePaths != settings.Watches
                || wire.DistinctRestorePaths != settings.Watches
                || wire.RestoreFrames == 0
                || wire.RestoreReplies != wire.RestoreFrames
                || wire.ErrorType is not null
                || (settings.Watches == RecoverySettings.FullWatches && wire.RestoreFrames < 2)
            )
            {
                throw new InvalidDataException(
                    "Incomplete native watch restoration or session continuity."
                );
            }
            var retained = await control.ExistsAsync(lease, token).ConfigureAwait(false);
            ephemeralRetained = retained is { } stat && stat.EphemeralOwner == session;
            if (!ephemeralRetained)
            {
                throw new InvalidDataException("Session ephemeral was not retained.");
            }
            activity = "post-recovery-validation";
            await run.VerifyAsync(
                    owner,
                    control,
                    consumers,
                    root,
                    changed,
                    generation + 1,
                    counters,
                    token
                )
                .ConfigureAwait(false);
            controlRetained =
                control.Session.SessionId == controlSession
                && control.Session.ConnectionGeneration == controlGeneration
                && control.Session.State == ZooKeeperSessionState.Connected;
            ownerRetained =
                ownerRetained
                && owner.Session.SessionId == session
                && owner.Session.ConnectionGeneration == generation + 1
                && owner.Session.State == ZooKeeperSessionState.Connected;
            if (
                !ownerRetained
                || !controlRetained
                || run.Changes != settings.Watches
                || run.ProtectedReads != settings.Watches
            )
            {
                throw new InvalidDataException("Post-recovery verification incomplete.");
            }
            activity = "close";
            acknowledgedClose = await run.CloseAsync(owner, consumers, token).ConfigureAwait(false);
            await control.SyncAsync(root, token).ConfigureAwait(false);
            if (
                !acknowledgedClose
                || await control.ExistsAsync(lease, token).ConfigureAwait(false) is not null
            )
            {
                throw new InvalidDataException("Acknowledged close/ephemeral deletion failed.");
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
            proxy?.Resume();
            proxy?.ReleaseRestore();
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
                Task? pending in new Task?[]
                {
                    run.GapReads,
                    run.PendingReads,
                    run.ClosingReads,
                    run.Readiness,
                }
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
            if (proxy is not null)
            {
                wire = proxy.Observation();
                try
                {
                    await proxy.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    failure ??= error;
                }
            }
            if (control is not null)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(RecoverySettings.CleanupGuard);
                    await DeleteLeavesAsync(control, root, cleanup.Token).ConfigureAwait(false);
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
                            "Recovery namespace remains after cleanup."
                        );
                    }
                }
                catch (Exception error)
                {
                    failure ??= error;
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
            && controlRetained
            && ephemeralRetained
            && acknowledgedClose
            && countersZero
            && namespaceRemoved;
        var report = new
        {
            Status = passed ? "passed" : "failed",
            Activity = passed ? "complete" : activity,
            ErrorType = failure?.GetType().Name,
            ServerErrorCode = (failure as ZooKeeperException)?.ErrorCode,
            settings.Common.Version,
            settings.Watches,
            EventBudget = eventBudget,
            AdmissionProbeSyncTransmissions = admissionProbeTransmissions,
            DistinctPaths = consumers
                .Select(static consumer => consumer.Watch.Path)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            OwnerSessionRetained = ownerRetained,
            ControlSessionRetained = controlRetained,
            EphemeralRetained = ephemeralRetained,
            AcknowledgedClose = acknowledgedClose,
            FinalCountersZero = countersZero,
            NamespaceRemoved = namespaceRemoved,
            SetupMilliseconds = setupMilliseconds,
            TimingGate = false,
            StopwatchFrequency = Stopwatch.Frequency,
            Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Wire = wire,
            Measurement = run.Result(wire),
        };
        await LiveReport
            .WriteAsync(settings.Common.Output, report, run.Histogram)
            .ConfigureAwait(false);
        Console.WriteLine(
            FormattableString.Invariant(
                $"watch-recovery {settings.Common.Version} watches={settings.Watches} status={(passed ? "passed" : "failed")} restored={wire?.RestorePaths ?? 0} frames={wire?.RestoreFrames ?? 0} changes={run.Changes} error={failure?.GetType().Name ?? "none"}"
            )
        );
        return passed ? 0 : 1;
    }

    private static async Task DeleteLeavesAsync(
        ZooKeeperClient control,
        string root,
        CancellationToken token
    )
    {
        try
        {
            var children = (
                await control.GetChildrenAsync(root, token).ConfigureAwait(false)
            ).Children;
            for (int offset = 0; offset < children.Count; offset += RecoverySettings.BatchSize)
            {
                await Task.WhenAll(
                        children
                            .Skip(offset)
                            .Take(RecoverySettings.BatchSize)
                            .Select(child =>
                                control.DeleteAsync(root + "/" + child, ZNodeVersion.Any, token)
                            )
                    )
                    .ConfigureAwait(false);
            }
            await control.DeleteAsync(root, ZNodeVersion.Any, token).ConfigureAwait(false);
        }
        catch (ZooKeeperException error) when (error.KnownErrorCode == ZooKeeperErrorCode.NoNode)
        { }
    }
}
