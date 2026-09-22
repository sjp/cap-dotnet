using System.Net;
using System.Net.Sockets;
using Cap.Primitives;

namespace Cap.Net.Tests;

/// <summary>
/// Connections and listeners, against the pool that permitted them.
/// </summary>
/// <remarks>
/// <para>
/// Everything here runs against the loopback interface, because the property under test is
/// which endpoints the library agrees to reach and that is settled before a packet is sent.
/// A test that needed a peer elsewhere would be testing the network.
/// </para>
/// <para>
/// The refusals are the point, and each is written so that a pool which wrongly said yes
/// would be caught by the attempt succeeding rather than by the wrong exception type: the
/// endpoints they name are ones nothing is listening at, so a refusal that failed to happen
/// would surface as a connection failure and not as a pass.
/// </para>
/// </remarks>
public sealed class TcpGrantTests
{
    /// <summary>The loopback address, granted at every port.</summary>
    private static Pool Loopback => new PoolBuilder()
        .InsertIpNet(IPNetwork.Parse("127.0.0.0/8"), PortRange.Every, AmbientAuthority.Acquire())
        .Build();

    /// <summary>A connection to a granted endpoint is made, and carries bytes.</summary>
    [Fact]
    public async Task A_granted_endpoint_can_be_reached()
    {
        Pool pool = Loopback;
        using CapTcpListener listener = CapTcpListener.Bind(pool, new IPEndPoint(IPAddress.Loopback, 0));

        ValueTask<CapTcpStream> accepting = listener.AcceptAsync(TestContext.Current.CancellationToken);

        using CapTcpStream client = await CapTcpStream.ConnectAsync(
            pool, listener.LocalEndPoint, TestContext.Current.CancellationToken);
        using CapTcpStream server = await accepting;

        await client.WriteAsync("hello"u8.ToArray(), TestContext.Current.CancellationToken);

        byte[] received = new byte[5];
        await server.ReadExactlyAsync(received, TestContext.Current.CancellationToken);

        Assert.Equal("hello"u8.ToArray(), received);
    }

    /// <summary>An endpoint the pool does not grant is refused before anything is attempted.</summary>
    [Fact]
    public void An_ungranted_endpoint_is_refused()
    {
        Pool pool = new PoolBuilder()
            .InsertSocketAddress(new IPEndPoint(IPAddress.Loopback, 9), AmbientAuthority.Acquire())
            .Build();

        Assert.Throws<EndpointNotGrantedException>(() =>
            CapTcpStream.Connect(pool, new IPEndPoint(IPAddress.Loopback, 10)));
    }

    /// <summary>The refusal is about authority and is not a network failure.</summary>
    /// <remarks>
    /// Worth asserting separately, because an application that wants to alert on grants being
    /// exceeded has to be able to tell the two apart without reading message text. A
    /// connection that was refused by the far end and a connection that was never permitted
    /// are different events.
    /// </remarks>
    [Fact]
    public void The_refusal_is_not_a_socket_failure()
    {
        Assert.IsNotType<SocketException>(
            Assert.ThrowsAny<Exception>(() =>
                CapTcpStream.Connect(Pool.Empty, new IPEndPoint(IPAddress.Loopback, 10))));
    }

    /// <summary>A listener cannot claim an endpoint the pool does not grant.</summary>
    [Fact]
    public void An_ungranted_endpoint_cannot_be_listened_at()
    {
        Assert.Throws<EndpointNotGrantedException>(() =>
            CapTcpListener.Bind(Pool.Empty, new IPEndPoint(IPAddress.Loopback, 0)));
    }

    /// <summary>
    /// A listener that asked the system to choose a port is checked against the port it was
    /// given.
    /// </summary>
    /// <remarks>
    /// The case the acceptance of "check the endpoint actually reached" is really about. The
    /// pool here grants one port, the request names none, and the port the system picks will
    /// not be that one — so a check written against what was asked for rather than against
    /// what happened would let this through and publish a name nobody granted.
    /// </remarks>
    [Fact]
    public void A_chosen_port_the_pool_does_not_grant_is_given_up()
    {
        Pool pool = new PoolBuilder()
            .InsertSocketAddress(new IPEndPoint(IPAddress.Loopback, 9), AmbientAuthority.Acquire())
            .Build();

        Assert.Throws<EndpointNotGrantedException>(() =>
            CapTcpListener.Bind(pool, new IPEndPoint(IPAddress.Loopback, 0)));
    }

    /// <summary>A chosen port the pool does grant is kept.</summary>
    [Fact]
    public void A_chosen_port_the_pool_grants_is_kept()
    {
        using CapTcpListener listener = CapTcpListener.Bind(Loopback, new IPEndPoint(IPAddress.Loopback, 0));

        Assert.NotEqual(0, listener.LocalEndPoint.Port);
        Assert.True(listener.Pool.Allows(listener.LocalEndPoint));
    }

    /// <summary>
    /// An accepted connection carries the listener's pool rather than an unstated authority.
    /// </summary>
    /// <remarks>
    /// So a component handed one has the reach the listening component had, and no more. The
    /// alternative — an accepted connection that could reach anywhere — would make accepting
    /// a way to acquire authority nobody granted.
    /// </remarks>
    [Fact]
    public async Task An_accepted_connection_carries_the_listeners_pool()
    {
        Pool pool = Loopback;
        using CapTcpListener listener = CapTcpListener.Bind(pool, new IPEndPoint(IPAddress.Loopback, 0));

        ValueTask<CapTcpStream> accepting = listener.AcceptAsync(TestContext.Current.CancellationToken);

        using CapTcpStream client = await CapTcpStream.ConnectAsync(
            pool, listener.LocalEndPoint, TestContext.Current.CancellationToken);
        using CapTcpStream server = await accepting;

        Assert.Same(pool, server.Pool);
        Assert.Same(pool, client.Pool);
        Assert.Equal(listener.LocalEndPoint, client.RemoteEndPoint);
    }

    /// <summary>
    /// A peer the pool does not grant is still accepted, and is reported so the caller can
    /// decide.
    /// </summary>
    /// <remarks>
    /// The pool governs the name a component publishes, not who may reach it. A listener that
    /// refused unheard-of peers could not serve anything public, and the address a connection
    /// arrives from is not evidence of anything anyway — it is what the network says, not
    /// what the peer proved.
    /// </remarks>
    [Fact]
    public async Task A_peer_outside_the_pool_is_accepted_and_reported()
    {
        Pool listening = new PoolBuilder()
            .InsertIpNet(IPNetwork.Parse("127.0.0.0/8"), PortRange.Every, AmbientAuthority.Acquire())
            .Build();

        using CapTcpListener listener = CapTcpListener.Bind(listening, new IPEndPoint(IPAddress.Loopback, 0));
        ValueTask<CapTcpStream> accepting = listener.AcceptAsync(TestContext.Current.CancellationToken);

        // Connected by an ordinary socket, so that the peer's own endpoint owes nothing to
        // this library and is simply whatever the system assigned it.
        using var outsider = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await outsider.ConnectAsync(listener.LocalEndPoint, TestContext.Current.CancellationToken);

        using CapTcpStream server = await accepting;

        Assert.Equal(outsider.LocalEndPoint, server.RemoteEndPoint);
    }

    /// <summary>A connection made to a mapped address is granted by the address it maps to.</summary>
    /// <remarks>
    /// A dual-stack listener reports its peers in the mapped form, so a check that did not
    /// reduce the two spellings to one would refuse a peer the pool plainly grants — and,
    /// written the other way round, would permit one it plainly does not.
    /// </remarks>
    [Fact]
    public async Task A_mapped_loopback_address_is_granted_by_the_plain_one()
    {
        Pool pool = new PoolBuilder()
            .InsertIpNet(IPNetwork.Parse("::1/128"), PortRange.Every, AmbientAuthority.Acquire())
            .Build();

        using CapTcpListener listener = CapTcpListener.Bind(
            pool, new IPEndPoint(IPAddress.IPv6Loopback, 0));

        ValueTask<CapTcpStream> accepting = listener.AcceptAsync(TestContext.Current.CancellationToken);

        using CapTcpStream client = await CapTcpStream.ConnectAsync(
            pool, listener.LocalEndPoint, TestContext.Current.CancellationToken);
        using CapTcpStream server = await accepting;

        Assert.Equal(listener.LocalEndPoint.Port, client.RemoteEndPoint.Port);
    }
}
