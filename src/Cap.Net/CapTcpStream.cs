using System.Net;
using System.Net.Sockets;

namespace Cap.Net;

/// <summary>
/// A connection to an endpoint a <see cref="Pool"/> granted.
/// </summary>
/// <remarks>
/// <para>
/// There is no way to make one without a pool, and no member that takes a host name: a
/// connection is made to an address, and the address it was made to is the address that was
/// checked. That is not a convenience — resolving a name and then connecting are two
/// lookups, and an attacker who controls the answers can make the first return an address
/// the pool allows and the second return one it does not. Checking the address that is
/// connected to, and connecting to no other, is what closes that.
/// </para>
/// <para>
/// A stream produced by accepting carries the pool of the listener that accepted it, so a
/// component handed one has the same stated reach as the component that was listening, and
/// no more. It does not carry ambient authority: an accepted connection is not a way to
/// acquire a pool that grants everything.
/// </para>
/// </remarks>
public sealed class CapTcpStream : CapSocketStream
{
    internal CapTcpStream(Socket socket, Pool pool)
        : base(socket)
    {
        Pool = pool;
        RemoteEndPoint = SocketEndpoints.Remote(socket);
        LocalEndPoint = SocketEndpoints.Local(socket);
    }

    /// <summary>The authority this connection was made under, for what it does next.</summary>
    public Pool Pool { get; }

    /// <summary>The endpoint at the other end, as it stood when the connection was made.</summary>
    public IPEndPoint RemoteEndPoint { get; }

    /// <summary>The endpoint at this end, as it stood when the connection was made.</summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>Connects to <paramref name="endpoint"/>, if <paramref name="pool"/> grants it.</summary>
    /// <param name="pool">The authority the connection is made under.</param>
    /// <param name="endpoint">The address and port to connect to.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="EndpointNotGrantedException">
    /// <paramref name="pool"/> grants no authority over <paramref name="endpoint"/>.
    /// </exception>
    /// <exception cref="SocketException">The connection could not be made.</exception>
    public static CapTcpStream Connect(Pool pool, IPEndPoint endpoint)
    {
        Socket socket = Prepare(pool, endpoint);
        try
        {
            // Checked against the pool in Prepare, and the peer actually reached in Complete.
#pragma warning disable CAP0002
            socket.Connect(endpoint);
#pragma warning restore CAP0002
            return Complete(socket, pool);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <inheritdoc cref="Connect"/>
    /// <param name="pool">The authority the connection is made under.</param>
    /// <param name="endpoint">The address and port to connect to.</param>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    public static async ValueTask<CapTcpStream> ConnectAsync(
        Pool pool, IPEndPoint endpoint, CancellationToken cancellationToken = default)
    {
        Socket socket = Prepare(pool, endpoint);
        try
        {
            // Checked against the pool in Prepare, and the peer actually reached in Complete.
#pragma warning disable CAP0002
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
#pragma warning restore CAP0002
            return Complete(socket, pool);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Checks the grant and produces the socket the connection will be made on.</summary>
    private static Socket Prepare(Pool pool, IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(endpoint);

        pool.Demand(endpoint, "connect to");
        return new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
    }

    /// <summary>
    /// Checks the endpoint the connection actually reached, and wraps it.
    /// </summary>
    /// <remarks>
    /// The check before the attempt is on what was asked for; this one is on what happened.
    /// They cannot differ here — the connection was made to an address rather than to a name,
    /// so there is nothing in between for anything to change — and asking anyway is what
    /// keeps that from quietly becoming untrue if this ever grows a second way to connect.
    /// </remarks>
    private static CapTcpStream Complete(Socket socket, Pool pool)
    {
        IPEndPoint reached = SocketEndpoints.Remote(socket);
        pool.Demand(reached, "connect to");

        return new CapTcpStream(socket, pool);
    }
}
