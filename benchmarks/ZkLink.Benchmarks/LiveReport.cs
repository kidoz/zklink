using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ZkLink.Benchmarks;

internal static class LiveReport
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task WriteAsync(
        string output,
        object report,
        IEnumerable<LatencyBucket>? histogram
    )
    {
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, JsonOptions))
            .ConfigureAwait(false);
        if (histogram is null)
        {
            return;
        }
        var csv = new StringBuilder("lower_microseconds,upper_microseconds,count\n");
        foreach (var bucket in histogram)
        {
            csv.Append(
                CultureInfo.InvariantCulture,
                $"{bucket.LowerMicroseconds},{bucket.UpperMicroseconds},{bucket.Count}\n"
            );
        }
        await File.WriteAllTextAsync(Path.ChangeExtension(output, ".csv"), csv.ToString())
            .ConfigureAwait(false);
    }
}
