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

    /// <summary>
    /// A destination of the unspecified address is refused before a socket is made, and the
    /// refusal names what was asked for rather than the loopback address it would have reached.
    /// </summary>
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::ffff:0.0.0.0")]
    public void A_wildcard_destination_is_refused_before_a_socket_is_made(string wildcard)
    {
        var destination = new IPEndPoint(IPAddress.Parse(wildcard), 10);
        Pool pool = new PoolBuilder()
            .InsertSocketAddress(destination, AmbientAuthority.Acquire())
            .InsertIpNet(IPNetwork.Parse("127.0.0.0/8"), PortRange.Every, AmbientAuthority.Acquire())
            .InsertIpNet(IPNetwork.Parse("::1/128"), PortRange.Every, AmbientAuthority.Acquire())
            .Build();

        EndpointNotGrantedException refusal = Assert.Throws<EndpointNotGrantedException>(() =>
            CapTcpStream.Connect(pool, destination));

        Assert.Contains(destination.ToString(), refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A wildcard grant still lets a listener publish on every interface.</summary>
    [Fact]
    public void A_wildcard_grant_still_lets_a_listener_bind_every_interface()
    {
        Pool wildcard = new PoolBuilder()
            .InsertIpNet(IPNetwork.Parse("0.0.0.0/32"), PortRange.Every, AmbientAuthority.Acquire())
            .Build();

        using CapTcpListener listener = CapTcpListener.Bind(wildcard, new IPEndPoint(IPAddress.Any, 0));

        Assert.Equal(IPAddress.Any, listener.LocalEndPoint.Address);
    }

    /// <summary>
    /// Listening on every interface needs the unspecified address granted; a grant of
    /// loopback is not enough.
    /// </summary>
    [Fact]
    public void A_wildcard_listener_needs_the_wildcard_granted()
    {
        Assert.Throws<EndpointNotGrantedException>(() =>
            CapTcpListener.Bind(Loopback, new IPEndPoint(IPAddress.Any, 0)));
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

    /// <summary>A grant for the IPv6 loopback reaches a listener on the IPv6 loopback.</summary>
    [Fact]
    public async Task An_ipv6_loopback_grant_reaches_the_ipv6_loopback()
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

    /// <summary>A listener on the IPv6 wildcard does not accept IPv4 peers.</summary>
    /// <remarks>
    /// A dual-stack socket bound to <c>[::]</c> would also listen on <c>0.0.0.0</c>, which is
    /// a different set of interfaces from the one the grant names. The socket is single-stack,
    /// so the IPv4 port stays unclaimed and a connection to it is refused.
    /// </remarks>
    [Fact]
    public async Task A_listener_on_the_ipv6_wildcard_does_not_accept_ipv4_peers()
    {
        Pool pool = new PoolBuilder()
            .InsertIpNet(IPNetwork.Parse("::/128"), PortRange.Every, AmbientAuthority.Acquire())
            .Build();

        using CapTcpListener listener = CapTcpListener.Bind(pool, new IPEndPoint(IPAddress.IPv6Any, 0));

        using var outsider = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        SocketException refused = await Assert.ThrowsAsync<SocketException>(async () =>
            await outsider.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, listener.LocalEndPoint.Port),
                TestContext.Current.CancellationToken));

        Assert.Equal(SocketError.ConnectionRefused, refused.SocketErrorCode);
    }

    /// <summary>
    /// A mapped address passes the pool as the IPv4 address it maps to, and then cannot be
    /// reached over the single-stack IPv6 socket it would be connected on.
    /// </summary>
    /// <remarks>
    /// What the documentation tells a caller to expect: the grant is about the host, the
    /// socket is about the family, and the address has to be written in the family that
    /// reaches it.
    /// </remarks>
    [Fact]
    public async Task A_mapped_address_is_granted_but_not_reachable_over_ipv6()
    {
        Pool pool = Loopback;
        using CapTcpListener listener = CapTcpListener.Bind(pool, new IPEndPoint(IPAddress.Loopback, 0));
        var mapped = new IPEndPoint(IPAddress.Loopback.MapToIPv6(), listener.LocalEndPoint.Port);

        Assert.True(pool.Allows(mapped));
        await Assert.ThrowsAsync<SocketException>(async () =>
            await CapTcpStream.ConnectAsync(pool, mapped, TestContext.Current.CancellationToken));
    }
}
