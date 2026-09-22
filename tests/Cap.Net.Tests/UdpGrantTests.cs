using System.Net;
using System.Net.Sockets;
using Cap.Primitives;

namespace Cap.Net.Tests;

/// <summary>
/// Datagrams, which have no connection to check once and so are checked every time.
/// </summary>
/// <remarks>
/// The difference from a connection is the whole reason this is a separate type rather than
/// another way of opening the same one. A connected socket asks the question once because the
/// answer cannot change; a datagram socket can be pointed anywhere on each send, so each send
/// asks it again.
/// </remarks>
public sealed class UdpGrantTests
{
    /// <summary>The loopback address, granted at every port.</summary>
    private static Pool Loopback => new PoolBuilder()
        .InsertIpNet(IPNetwork.Parse("127.0.0.0/8"), PortRange.Every, AmbientAuthority.Acquire())
        .Build();

    /// <summary>A datagram to a granted destination arrives.</summary>
    [Fact]
    public async Task A_granted_destination_can_be_sent_to()
    {
        Pool pool = Loopback;
        using CapUdpSocket receiver = CapUdpSocket.Bind(pool, new IPEndPoint(IPAddress.Loopback, 0));
        using CapUdpSocket sender = CapUdpSocket.Open(pool, AddressFamily.InterNetwork);

        IPEndPoint destination = receiver.LocalEndPoint!;
        await sender.SendToAsync("ping"u8.ToArray(), destination, TestContext.Current.CancellationToken);

        byte[] buffer = new byte[16];
        SocketReceiveFromResult result = await receiver.ReceiveFromAsync(
            buffer, TestContext.Current.CancellationToken);

        Assert.Equal("ping"u8.ToArray(), buffer[..result.ReceivedBytes]);
    }

    /// <summary>A destination the pool does not grant is refused, on every send.</summary>
    [Fact]
    public void An_ungranted_destination_is_refused()
    {
        using CapUdpSocket sender = CapUdpSocket.Open(Pool.Empty, AddressFamily.InterNetwork);

        Assert.Throws<EndpointNotGrantedException>(() =>
            sender.SendTo("ping"u8, new IPEndPoint(IPAddress.Loopback, 9)));
    }

    /// <summary>Pointing a socket at an ungranted peer is refused too.</summary>
    /// <remarks>
    /// The check has to happen here rather than on the sends that follow, because after this
    /// the system will not send anywhere else — so this is the last moment at which there is
    /// a destination to check.
    /// </remarks>
    [Fact]
    public void An_ungranted_peer_cannot_be_connected_to()
    {
        using CapUdpSocket sender = CapUdpSocket.Open(Pool.Empty, AddressFamily.InterNetwork);

        Assert.Throws<EndpointNotGrantedException>(() =>
            sender.Connect(new IPEndPoint(IPAddress.Loopback, 9)));
    }

    /// <summary>A socket cannot claim an endpoint the pool does not grant.</summary>
    [Fact]
    public void An_ungranted_endpoint_cannot_be_received_at()
    {
        Assert.Throws<EndpointNotGrantedException>(() =>
            CapUdpSocket.Bind(Pool.Empty, new IPEndPoint(IPAddress.Loopback, 0)));
    }

    /// <summary>
    /// A socket that claims no endpoint of its own does not need one granted, and still
    /// checks where it sends.
    /// </summary>
    /// <remarks>
    /// The distinction is between publishing a name a peer could be told in advance and
    /// having the system assign one nobody chose. Requiring a grant for the second would mean
    /// a component that only sends had to be granted a range of local ports it never picked,
    /// which says nothing about what it can reach.
    /// </remarks>
    [Fact]
    public async Task A_socket_that_claims_nothing_still_checks_where_it_sends()
    {
        Pool pool = Loopback;
        using CapUdpSocket receiver = CapUdpSocket.Bind(pool, new IPEndPoint(IPAddress.Loopback, 0));

        // Granted for the receiver only: the sender's own endpoint is never consulted.
        Pool sending = new PoolBuilder()
            .InsertSocketAddress(receiver.LocalEndPoint!, AmbientAuthority.Acquire())
            .Build();

        using CapUdpSocket sender = CapUdpSocket.Open(sending, AddressFamily.InterNetwork);

        await sender.SendToAsync(
            "ping"u8.ToArray(), receiver.LocalEndPoint!, TestContext.Current.CancellationToken);

        Assert.Throws<EndpointNotGrantedException>(() =>
            sender.SendTo("ping"u8, new IPEndPoint(IPAddress.Loopback, 9)));
    }

    /// <summary>What arrives is not checked against the pool.</summary>
    /// <remarks>
    /// Nothing acknowledged the datagram, so the address it claims to come from is a field
    /// somebody wrote rather than a fact. Filtering on it would give an allowlist the
    /// appearance of a security control without the substance of one.
    /// </remarks>
    [Fact]
    public async Task A_datagram_from_outside_the_pool_still_arrives()
    {
        Pool pool = Loopback;
        using CapUdpSocket receiver = CapUdpSocket.Bind(pool, new IPEndPoint(IPAddress.Loopback, 0));

        using var outsider = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        // Bound to the interface it will send over, so that the endpoint it reports and the
        // endpoint the receiver is told about are the same fact rather than two.
        outsider.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        await outsider.SendToAsync(
            "ping"u8.ToArray(), receiver.LocalEndPoint!, TestContext.Current.CancellationToken);

        byte[] buffer = new byte[16];
        SocketReceiveFromResult result = await receiver.ReceiveFromAsync(
            buffer, TestContext.Current.CancellationToken);

        Assert.Equal("ping"u8.ToArray(), buffer[..result.ReceivedBytes]);
        Assert.Equal(outsider.LocalEndPoint, result.RemoteEndPoint);
    }
}
