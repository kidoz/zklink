namespace ZkLink.Sessions;

internal sealed class EndpointProvider(
    IReadOnlyList<ZooKeeperEndpoint> endpoints,
    SessionRandom random
)
{
    private readonly object _gate = new();
    private ZooKeeperEndpoint[] _current = endpoints.ToArray();
    private int _cursor;

    internal (ZooKeeperEndpoint Endpoint, bool LastInCycle) Next()
    {
        lock (_gate)
        {
            if (_cursor == 0)
            {
                random.Shuffle(_current);
            }
            var endpoint = _current[_cursor++];
            var last = _cursor == _current.Length;
            if (last)
            {
                _cursor = 0;
            }
            return (endpoint, last);
        }
    }

    internal void Update(IReadOnlyList<ZooKeeperEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Count is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(endpoints));
        }
        var copy = Snapshot.List(endpoints).ToArray();
        lock (_gate)
        {
            _current = copy;
            _cursor = 0;
        }
    }
}
