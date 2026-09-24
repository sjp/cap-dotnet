using System.Net;
using System.Net.Sockets;

namespace Cap.Net;

/// <summary>
/// A datagram socket whose every destination is one a <see cref="Pool"/> granted.
/// </summary>
/// <remarks>
/// <para>
/// A datagram socket has no connection to check once and be done with, so the check is made
/// per datagram: every send names where it is going, and every one of those is tested against
/// the pool. A socket that has been pointed at a peer with <see cref="Connect"/> has that peer
/// checked once, at that point, and a send that names no destination goes to it. A send that
/// does name one is checked like any other, whether or not the socket is pointed anywhere:
/// some systems, Linux among them, let a pointed socket send to an explicit address that
/// differs from its peer, so the check cannot be left to the system.
/// </para>
/// <para>
/// <strong>What arrives is not checked.</strong> Anything able to route to this socket can
/// send to it, as with a listener, and the address a datagram claims to come from is
/// especially not worth trusting: nothing acknowledged it, so it is a field in a packet that
/// anybody could have written. The sender is reported so a caller can make that decision, and
/// a caller who needs it to mean something needs authentication rather than an allowlist.
/// </para>
/// <para>
/// <strong>Thread safety.</strong> Instances are safe for concurrent use.
/// </para>
/// </remarks>
public sealed class CapUdpSocket : IDisposable
{
    private readonly Socket _socket;

    private CapUdpSocket(Socket socket, Pool pool)
    {
        _socket = socket;
        Pool = pool;
    }

    /// <summary>The authority every destination is checked against.</summary>
    /// <remarks>Fixed when the socket is made; safe to read from any thread.</remarks>
    public Pool Pool { get; }

    /// <summary>
    /// The endpoint this socket sends from, or null before the system has assigned one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null only for a socket from <see cref="Open"/> that has not yet sent anything: it
    /// claims no name until it needs one, and the system assigns it at the first send.
    /// </para>
    /// <para>
    /// Safe to read from any thread. It is asked of the socket each time rather than
    /// remembered, so a send on another thread can change the answer from null to an
    /// endpoint between one read and the next.
    /// </para>
    /// </remarks>
    public IPEndPoint? LocalEndPoint => _socket.LocalEndPoint as IPEndPoint;

    /// <summary>
    /// Opens a socket that claims no endpoint of its own.
    /// </summary>
    /// <param name="pool">The authority every destination is checked against.</param>
    /// <param name="family">Which kind of address this socket will send to.</param>
    /// <remarks>
    /// <para>
    /// For sending, and for the replies that come back to whatever the system assigns. The
    /// pool is not consulted about the local endpoint here, and that is the difference from
    /// <see cref="Bind"/>: nothing has been published, there is no name a peer could have
    /// been told in advance, and the system's choice is not a decision anybody made. Every
    /// datagram this socket sends is still checked.
    /// </para>
    /// <para>
    /// A service that peers are told how to reach wants <see cref="Bind"/>, so that the
    /// endpoint it claims is one the pool granted.
    /// </para>
    /// <para>Safe to call from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is null.</exception>
    public static CapUdpSocket Open(Pool pool, AddressFamily family)
    {
        ArgumentNullException.ThrowIfNull(pool);

        return new CapUdpSocket(new Socket(family, SocketType.Dgram, ProtocolType.Udp), pool);
    }

    /// <summary>Claims <paramref name="endpoint"/>, if <paramref name="pool"/> grants it.</summary>
    /// <param name="pool">The authority the endpoint is claimed under, and every destination checked against.</param>
    /// <param name="endpoint">
    /// The address and port to claim. A port of zero asks the system to choose one, which is
    /// then checked against the pool like any other.
    /// </param>
    /// <remarks>Safe to call from any thread.</remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="EndpointNotGrantedException">
    /// <paramref name="pool"/> grants no authority over the endpoint that would be claimed.
    /// </exception>
    /// <exception cref="SocketException">The endpoint could not be bound.</exception>
    public static CapUdpSocket Bind(Pool pool, IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(endpoint);

        if (endpoint.Port != 0)
        {
            pool.Demand(endpoint, "receive at");
        }

        var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // Checked against the pool above where the port was named, and below where it was not.
#pragma warning disable CAP0002
            socket.Bind(endpoint);
#pragma warning restore CAP0002
            pool.Demand(SocketEndpoints.Local(socket), "receive at");

            return new CapUdpSocket(socket, pool);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Points this socket at one peer, if the pool grants it.</summary>
    /// <remarks>
    /// <para>
    /// After this, a send that names no destination goes to this peer, which is why the
    /// check for those sends happens here and not on each of them. A send that names a
    /// destination is still checked on its own, since not every system refuses one that
    /// differs from the peer. The system also discards what arrives from anywhere else.
    /// </para>
    /// <para>
    /// Safe to call from any thread. A send racing it on another thread may reach the peer
    /// this socket was pointed at before or the one it is pointed at after, but either way
    /// one the pool granted, since nothing is pointed at an endpoint before it is checked.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is null.</exception>
    /// <exception cref="EndpointNotGrantedException">
    /// The pool grants no authority over <paramref name="endpoint"/>.
    /// </exception>
    public void Connect(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        Pool.Demand(endpoint, "send to");
#pragma warning disable CAP0002 // Checked against the pool on the line above.
        _socket.Connect(endpoint);
#pragma warning restore CAP0002
        Pool.Demand(SocketEndpoints.Remote(_socket), "send to");
    }

    /// <summary>Sends one datagram to <paramref name="destination"/>, if the pool grants it.</summary>
    /// <returns>How many bytes were sent.</returns>
    /// <remarks>
    /// Safe to call from several threads at once; each datagram is sent whole, and each is
    /// checked against the pool on its own.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.</exception>
    /// <exception cref="EndpointNotGrantedException">
    /// The pool grants no authority over <paramref name="destination"/>.
    /// </exception>
    public int SendTo(ReadOnlySpan<byte> buffer, IPEndPoint destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        Pool.Demand(destination, "send to");
#pragma warning disable CAP0002 // Checked against the pool on the line above.
        return _socket.SendTo(buffer, SocketFlags.None, destination);
#pragma warning restore CAP0002
    }

    /// <inheritdoc cref="SendTo"/>
    /// <param name="buffer">What to send.</param>
    /// <param name="destination">Where to send it.</param>
    /// <param name="cancellationToken">Abandons the send.</param>
    public ValueTask<int> SendToAsync(
        ReadOnlyMemory<byte> buffer,
        IPEndPoint destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        Pool.Demand(destination, "send to");
#pragma warning disable CAP0002 // Checked against the pool on the line above.
        return _socket.SendToAsync(buffer, SocketFlags.None, destination, cancellationToken);
#pragma warning restore CAP0002
    }

    /// <summary>Sends one datagram to the peer this socket was pointed at.</summary>
    /// <returns>How many bytes were sent.</returns>
    /// <remarks>
    /// <para>
    /// The destination was checked by <see cref="Connect"/> and cannot have changed since:
    /// a send that names no destination goes to the socket's peer, and
    /// <see cref="Connect"/>, which checks the pool before it points the socket anywhere,
    /// is the only way to change that peer.
    /// </para>
    /// <para>Safe to call from several threads at once; each datagram is sent whole.</para>
    /// </remarks>
    public int Send(ReadOnlySpan<byte> buffer) => _socket.Send(buffer, SocketFlags.None);

    /// <inheritdoc cref="Send"/>
    /// <param name="buffer">What to send.</param>
    /// <param name="cancellationToken">Abandons the send.</param>
    public ValueTask<int> SendAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _socket.SendAsync(buffer, SocketFlags.None, cancellationToken);

    /// <summary>Waits for a datagram, and reports who it claims to be from.</summary>
    /// <remarks>
    /// <para>
    /// The sender is not checked against the pool and is not authenticated. It is what the
    /// datagram says.
    /// </para>
    /// <para>
    /// Safe to call from several threads at once; each datagram arrives whole at exactly one
    /// of them.
    /// </para>
    /// </remarks>
    public SocketReceiveFromResult ReceiveFrom(Span<byte> buffer)
    {
        EndPoint sender = AnySender();
        int received = _socket.ReceiveFrom(buffer, SocketFlags.None, ref sender);

        return new SocketReceiveFromResult { ReceivedBytes = received, RemoteEndPoint = sender };
    }

    /// <inheritdoc cref="ReceiveFrom"/>
    /// <param name="buffer">Where to put what arrives.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    public ValueTask<SocketReceiveFromResult> ReceiveFromAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _socket.ReceiveFromAsync(buffer, SocketFlags.None, AnySender(), cancellationToken);

    /// <summary>Waits for a datagram from the peer this socket was pointed at.</summary>
    /// <returns>How many bytes arrived.</returns>
    /// <remarks>
    /// <para>
    /// On a socket that has not been pointed at a peer with <see cref="Connect"/>, this takes
    /// a datagram from anybody and does not say who sent it; <see cref="ReceiveFrom"/> is the
    /// form that reports the sender.
    /// </para>
    /// <para>
    /// Safe to call from several threads at once; each datagram arrives whole at exactly one
    /// of them.
    /// </para>
    /// </remarks>
    public int Receive(Span<byte> buffer) => _socket.Receive(buffer, SocketFlags.None);

    /// <inheritdoc cref="Receive"/>
    /// <param name="buffer">Where to put what arrives.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    public ValueTask<int> ReceiveAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);

    /// <summary>Closes the socket.</summary>
    /// <remarks>
    /// Safe to call from any thread; a send or receive waiting on another thread is abandoned
    /// and fails rather than completing.
    /// </remarks>
    public void Dispose() => _socket.Dispose();

    /// <summary>
    /// The placeholder a receive is handed for the system to write the sender into.
    /// </summary>
    private IPEndPoint AnySender() => new(
        _socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
        0);
}
