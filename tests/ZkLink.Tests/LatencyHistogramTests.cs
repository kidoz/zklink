using System.Diagnostics;
using ZkLink.Benchmarks;

namespace ZkLink.Tests;

public sealed class LatencyHistogramTests
{
    [Fact]
    public void MergingDistributionsPreservesWeightedPercentilesAndAllCounts()
    {
        var first = new LatencyHistogram();
        var second = new LatencyHistogram();
        const int lowerCount = 50;
        const int middleCount = 45;
        const int upperCount = 5;
        const long microsecondsPerSecond = 1_000_000;
        for (int index = 0; index < lowerCount; index++)
        {
            first.Record(Stopwatch.Frequency);
        }
        for (int index = 0; index < middleCount; index++)
        {
            second.Record(2 * Stopwatch.Frequency);
        }
        for (int index = 0; index < upperCount; index++)
        {
            second.Record(4 * Stopwatch.Frequency);
        }
        first.Merge(second);
        Assert.Equal(lowerCount + middleCount + upperCount, first.Count);
        Assert.Equal(first.Count, first.Buckets().Sum(static bucket => bucket.Count));
        Assert.Equal(4 * Stopwatch.Frequency, first.MaxTicks);
        Assert.Equal(
            (lowerCount + 2 * middleCount + 4 * upperCount) * Stopwatch.Frequency,
            first.TotalTicks
        );
        var median = first.Percentile(50);
        var p95 = first.Percentile(95);
        var p99 = first.Percentile(99);
        Assert.InRange(microsecondsPerSecond, median.LowerMicroseconds, median.UpperMicroseconds);
        Assert.InRange(2 * microsecondsPerSecond, p95.LowerMicroseconds, p95.UpperMicroseconds);
        Assert.InRange(4 * microsecondsPerSecond, p99.LowerMicroseconds, p99.UpperMicroseconds);
        Assert.True(median.UpperMicroseconds < p95.LowerMicroseconds);
        Assert.True(p95.UpperMicroseconds < p99.LowerMicroseconds);
    }

    [Fact]
    public void EmptyAndInvalidPercentileQueriesAreRejected()
    {
        var histogram = new LatencyHistogram();
        Assert.Throws<InvalidOperationException>(() => histogram.Percentile(50));
        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.Percentile(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.Percentile(101));
        Assert.Empty(histogram.Buckets());
    }
}
