using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ZkLink.FaultTests;

// Register actual sockets while live. Port numbers alone are not ownership:
// another loopback address or a later socket can use the same port.
internal sealed class SoakSocketScope
{
    internal const int MaximumListeners = 8;
    internal const int MaximumConnections = 256;
    private readonly object _gate = new();
    private readonly List<SoakListenerSocket> _listeners = [];
    private readonly List<SoakConnectionSocket> _connections = [];
    private bool _sealed;

    internal void TrackListener(Socket socket)
    {
        lock (_gate)
        {
            if (_sealed || _listeners.Count >= MaximumListeners)
            {
                throw new InvalidOperationException("Soak listener inventory is closed or full.");
            }
            _listeners.Add(new((IPEndPoint)socket.LocalEndPoint!, socket.SafeHandle));
        }
    }

    internal void TrackConnection(Socket socket)
    {
        lock (_gate)
        {
            if (_sealed || _connections.Count >= MaximumConnections)
            {
                throw new InvalidOperationException("Soak connection inventory is closed or full.");
            }
            _connections.Add(
                new(
                    (IPEndPoint)socket.LocalEndPoint!,
                    (IPEndPoint)socket.RemoteEndPoint!,
                    socket.SafeHandle
                )
            );
        }
    }

    internal SoakSocketInventory Seal()
    {
        lock (_gate)
        {
            _sealed = true;
            return new(_listeners.ToArray(), _connections.ToArray());
        }
    }
}

internal sealed record SoakListenerSocket(IPEndPoint Endpoint, SafeHandle Handle);

internal sealed record SoakConnectionSocket(IPEndPoint Local, IPEndPoint Remote, SafeHandle Handle);

internal sealed record SoakSocketInventory(
    IReadOnlyList<SoakListenerSocket> Listeners,
    IReadOnlyList<SoakConnectionSocket> Connections
)
{
    internal int OpenHandles =>
        Listeners.Count(s => !s.Handle.IsClosed) + Connections.Count(s => !s.Handle.IsClosed);

    internal static bool SameEndpoint(IPEndPoint left, IPEndPoint right)
    {
        static IPAddress Normalize(IPAddress address) =>
            address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return left.Port == right.Port && Normalize(left.Address).Equals(Normalize(right.Address));
    }

    internal bool Matches(TcpConnectionInformation connection)
    {
        if (
            connection.State
            is not (
                TcpState.Established
                or TcpState.CloseWait
                or TcpState.SynSent
                or TcpState.SynReceived
            )
        )
        {
            return false;
        }
        if (
            Connections.Any(s =>
                (
                    SameEndpoint(s.Local, connection.LocalEndPoint)
                    && SameEndpoint(s.Remote, connection.RemoteEndPoint)
                )
                || (
                    SameEndpoint(s.Local, connection.RemoteEndPoint)
                    && SameEndpoint(s.Remote, connection.LocalEndPoint)
                )
            )
        )
        {
            return true;
        }
        // Preserve detection of an attempt not yet accepted when its listener closed.
        return connection.State is TcpState.SynSent or TcpState.SynReceived
            && Listeners.Any(s =>
                SameEndpoint(s.Endpoint, connection.LocalEndPoint)
                || SameEndpoint(s.Endpoint, connection.RemoteEndPoint)
            );
    }
}
