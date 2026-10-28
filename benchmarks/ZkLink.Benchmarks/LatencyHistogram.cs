using System.Numerics;

namespace ZkLink.Benchmarks;

// Single writer per worker; merge only after all workers have stopped.
internal sealed class LatencyHistogram
{
    private const int SubBucketBits = 5;
    private const int SubBuckets = 1 << SubBucketBits;
    private const int BucketCount = 1024;
    private readonly long[] _counts = new long[BucketCount];
    internal long Count { get; private set; }
    internal long TotalTicks { get; private set; }
    internal long MaxTicks { get; private set; }

    internal void Record(long elapsedTicks)
    {
        long micros = Math.Max(
            1,
            (long)Math.Ceiling(elapsedTicks * 1_000_000d / System.Diagnostics.Stopwatch.Frequency)
        );
        int index;
        if (micros < SubBuckets)
        {
            index = (int)micros;
        }
        else
        {
            int shift = BitOperations.Log2((ulong)micros) - SubBucketBits;
            index = (shift + 1) * SubBuckets + (int)(micros >> shift) - SubBuckets;
        }
        if (index >= BucketCount)
        {
            throw new InvalidOperationException("Latency exceeded histogram capacity.");
        }
        _counts[index]++;
        Count++;
        TotalTicks += elapsedTicks;
        MaxTicks = Math.Max(MaxTicks, elapsedTicks);
    }

    internal void Merge(LatencyHistogram other)
    {
        for (int index = 0; index < BucketCount; index++)
        {
            _counts[index] += other._counts[index];
        }
        Count += other.Count;
        TotalTicks += other.TotalTicks;
        MaxTicks = Math.Max(MaxTicks, other.MaxTicks);
    }

    internal LatencyRange Percentile(int percent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        if (Count == 0)
        {
            throw new InvalidOperationException("No completed latency observations.");
        }
        long rank = (Count * percent + 99) / 100;
        long cumulative = 0;
        for (int index = 0; index < BucketCount; index++)
        {
            cumulative += _counts[index];
            if (cumulative >= rank)
            {
                return Bounds(index);
            }
        }
        throw new InvalidOperationException("Histogram count mismatch.");
    }

    internal IEnumerable<LatencyBucket> Buckets()
    {
        for (int index = 0; index < BucketCount; index++)
        {
            if (_counts[index] != 0)
            {
                var bounds = Bounds(index);
                yield return new(
                    bounds.LowerMicroseconds,
                    bounds.UpperMicroseconds,
                    _counts[index]
                );
            }
        }
    }

    private static LatencyRange Bounds(int index)
    {
        if (index < SubBuckets)
        {
            return new(index, index);
        }
        int shift = index / SubBuckets - 1;
        long lower = (long)(SubBuckets + index % SubBuckets) << shift;
        return new(lower, lower + (1L << shift) - 1);
    }
}

internal sealed record LatencyRange(long LowerMicroseconds, long UpperMicroseconds);

internal sealed record LatencyBucket(long LowerMicroseconds, long UpperMicroseconds, long Count);
