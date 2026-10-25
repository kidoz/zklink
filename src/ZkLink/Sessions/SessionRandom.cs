namespace ZkLink.Sessions;

// Reproducible endpoint spreading and retry timing, never credentials or security tokens.
internal sealed class SessionRandom(Random random)
{
#pragma warning disable CA5394 // Load spreading/full jitter is not security-sensitive randomness.
    internal void Shuffle<T>(T[] values) => random.Shuffle(values);

    internal TimeSpan Backoff(int failures, TimeSpan remaining)
    {
        var cap = Math.Min(2000, 100 * Math.Pow(2, Math.Min(failures, 5)));
        return TimeSpan.FromMilliseconds(
            Math.Min(
                Math.Max(1, Math.Ceiling(cap * random.NextDouble())),
                remaining.TotalMilliseconds
            )
        );
    }
#pragma warning restore CA5394
}
