using System.Net;
using System.Net.Sockets;

namespace Cap.Net;

/// <summary>
/// A listening socket at an endpoint a <see cref="Pool"/> granted.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The pool governs the name this listener claims, not who is allowed to reach
/// it.</strong> Binding publishes an address and a port that anything able to route to them
/// can connect to, and which of them a component may publish is exactly the kind of decision
/// a pool exists to record. Who then connects is not: a listener that refused peers it had
/// not been told about in advance would be a listener no public service could be built on,
/// and the address a connection arrives from is not a fact worth trusting anyway — it is
/// what the network says, not what the peer proved. <see cref="CapTcpStream.RemoteEndPoint"/>
/// is there for a caller who wants to make that decision, and authentication is there for a
/// caller who needs it to mean something.
/// </para>
/// <para>
/// The endpoint the grant is checked against is the one the socket ended up bound to, not the
/// one that was asked for. A request for port zero asks the system to choose, so the choice
/// has to be made before there is anything to check; the bind is made, the result is checked,
/// and a result the pool does not grant is closed again before it is ever listened on.
/// </para>
/// <para>
/// <strong>Thread safety.</strong> Instances are safe for concurrent use; several accepts may
/// be outstanding at once.
/// </para>
/// </remarks>
public sealed class CapTcpListener : IDisposable
{
    /// <summary>How many pending connections are held when the caller does not say.</summary>
    private const int DefaultBacklog = 128;

    private readonly Socket _socket;

    private CapTcpListener(Socket socket, Pool pool)
    {
        _socket = socket;
        Pool = pool;
        LocalEndPoint = SocketEndpoints.Local(socket);
    }

    /// <summary>The authority this listener was bound under.</summary>
    public Pool Pool { get; }

    /// <summary>The endpoint this listener is bound to, with the port the system settled on.</summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>Binds and listens at <paramref name="endpoint"/>, if <paramref name="pool"/> grants it.</summary>
    /// <param name="pool">The authority the listener is bound under.</param>
    /// <param name="endpoint">
    /// The address and port to claim. A port of zero asks the system to choose one, which is
    /// then checked against the pool like any other.
    /// </param>
    /// <param name="backlog">How many pending connections the system holds before refusing.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="EndpointNotGrantedException">
    /// <paramref name="pool"/> grants no authority over the endpoint that would be claimed.
    /// </exception>
    /// <exception cref="SocketException">The endpoint could not be bound or listened on.</exception>
    public static CapTcpListener Bind(Pool pool, IPEndPoint endpoint, int backlog = DefaultBacklog)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(endpoint);

        // Refused before a socket exists where the request names a port outright, so that the
        // ordinary case fails without having claimed anything. A request for port zero has
        // nothing to check yet and is checked below.
        if (endpoint.Port != 0)
        {
            pool.Demand(endpoint, "listen at");
        }

        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(endpoint);

            // Between the bind and the listen on purpose. The socket has a name by now but
            // nothing can connect to it yet, so a name the pool does not grant is given up
            // without ever having accepted anything.
            IPEndPoint claimed = SocketEndpoints.Local(socket);
            pool.Demand(claimed, "listen at");

            socket.Listen(backlog);
            return new CapTcpListener(socket, pool);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Waits for a connection and takes it.</summary>
    /// <remarks>The connection carries this listener's pool for whatever it does next.</remarks>
    /// <exception cref="SocketException">The accept failed.</exception>
    public CapTcpStream Accept() => Adopt(_socket.Accept());

    /// <inheritdoc cref="Accept"/>
    /// <param name="cancellationToken">Abandons the wait.</param>
    public async ValueTask<CapTcpStream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        Socket accepted = await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
        return Adopt(accepted);
    }

    /// <summary>Stops listening and gives up the endpoint.</summary>
    /// <remarks>
    /// Connections already accepted are unaffected and stay usable: each owns a socket of its
    /// own rather than a reference into this one.
    /// </remarks>
    public void Dispose() => _socket.Dispose();

    private CapTcpStream Adopt(Socket accepted)
    {
        try
        {
            return new CapTcpStream(accepted, Pool);
        }
        catch
        {
            accepted.Dispose();
            throw;
        }
    }
}
