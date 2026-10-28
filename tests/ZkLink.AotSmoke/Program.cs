using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ZkLink.Samples.Basic;

namespace ZkLink.AotSmoke;

internal static class Program
{
    private const int CaseDeadlineSeconds = 90;

    [SuppressMessage(
        "Design",
        "CA1031",
        Justification = "The executable boundary returns failure without logging credentials, paths or platform exception text."
    )]
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            using var deadline = new CancellationTokenSource(
                TimeSpan.FromSeconds(CaseDeadlineSeconds)
            );
            if (args is not ([] or ["--security-negatives"] or ["--native-check"]))
            {
                throw new InvalidOperationException("Invalid native smoke arguments.");
            }

            NativeSettings.Require(
                !RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled
            );
            NativeSettings.Require(
                RuntimeInformation.RuntimeIdentifier
                    == NativeSettings.Required("ZKLINK_AOT_EXPECTED_RID")
            );
            using (
                var executable = File.OpenRead(
                    Environment.ProcessPath
                        ?? throw new InvalidOperationException("The process image is unavailable.")
                )
            )
            {
                var hash = await SHA256
                    .HashDataAsync(executable, deadline.Token)
                    .ConfigureAwait(false);
                NativeSettings.Require(
                    Convert.ToHexString(hash)
                        == NativeSettings.Required("ZKLINK_AOT_EXECUTABLE_SHA256")
                );
            }
            await Console
                .Out.WriteLineAsync("Native execution and expected RID: verified.")
                .ConfigureAwait(false);
            if (args is ["--native-check"])
            {
                await Console
                    .Out.WriteLineAsync("Native identity probe: PASS (no live operations).")
                    .ConfigureAwait(false);
                return 0;
            }
            using var settings = await NativeSettings
                .ReadAsync(deadline.Token)
                .ConfigureAwait(false);
            var client = await settings
                .ConnectAsync(settings.Endpoint, deadline.Token)
                .ConfigureAwait(false);
            await using var lifetime = client.ConfigureAwait(false);
            foreach (
                ISampleStep step in new ISampleStep[]
                {
                    new NodeSample(),
                    new TransactionSample(),
                    new AuxiliarySample(),
                    new WatchSample(),
                }
            )
            {
                var message = await step.RunAsync(client, deadline.Token).ConfigureAwait(false);
                await Console.Out.WriteLineAsync(message).ConfigureAwait(false);
            }

            await NativeSecurity
                .VerifyAsync(settings, client, deadline.Token)
                .ConfigureAwait(false);
            await NativeRecovery
                .VerifyAsync(settings, client, deadline.Token)
                .ConfigureAwait(false);
            if (args.Length != 0)
            {
                await NativeSecurity
                    .RejectionsAsync(settings, deadline.Token)
                    .ConfigureAwait(false);
            }

            await NativeSettings.CloseAsync(client, deadline.Token).ConfigureAwait(false);
            await Console.Out.WriteLineAsync("Native smoke: PASS").ConfigureAwait(false);
            return 0;
        }
        catch (Exception error)
        {
            await Console
                .Error.WriteLineAsync("Native smoke failed: " + error.GetType().Name)
                .ConfigureAwait(false);
            return 1;
        }
    }
}
