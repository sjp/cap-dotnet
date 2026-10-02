using System.Net;
using Cap.Primitives;

namespace Cap.Net.Tests;

/// <summary>
/// What a pool says yes to, and what it refuses to be talked into.
/// </summary>
/// <remarks>
/// <para>
/// These are the tests that matter most in this assembly, because everything else here
/// trusts the answer. A socket type that consults the pool correctly is worth nothing if the
/// pool can be made to agree that an address it was never given is one of the addresses it
/// was given.
/// </para>
/// <para>
/// The interesting cases are all the same shape: an address written in a way the person who
/// wrote the grant did not have in mind. One host has several spellings, a range written to
/// describe a network turns out to include something that was never part of the network in
/// anybody's mind, and the value nobody constructed has to mean nothing rather than mean
/// whatever falls out of zero.
/// </para>
/// </remarks>
public sealed class PoolGrantTests
{
    /// <summary>A pool nobody granted anything grants nothing.</summary>
    [Fact]
    public void An_empty_pool_allows_nothing()
    {
        Assert.False(Pool.Empty.Allows(new IPEndPoint(IPAddress.Loopback, 443)));
        Assert.False(Pool.Empty.GrantsEveryEndpoint);
    }

    /// <summary>An endpoint granted outright is allowed, and its neighbours are not.</summary>
    [Fact]
    public void An_endpoint_grant_covers_that_endpoint_alone()
    {
        Pool pool = Granting(IPAddress.Parse("10.1.2.3"), 443);

        Assert.True(pool.Allows(IPAddress.Parse("10.1.2.3"), 443));
        Assert.False(pool.Allows(IPAddress.Parse("10.1.2.3"), 444));
        Assert.False(pool.Allows(IPAddress.Parse("10.1.2.4"), 443));
    }

    /// <summary>A range grant covers the addresses in it, at the ports it names.</summary>
    [Fact]
    public void A_range_grant_covers_its_addresses_at_its_ports()
    {
        Pool pool = new PoolBuilder()
            .InsertIpNet(IPNetwork.Parse("10.0.0.0/8"), PortRange.Between(8000, 8099), AmbientAuthority.Acquire())
            .Build();

        Assert.True(pool.Allows(IPAddress.Parse("10.255.255.255"), 8000));
        Assert.True(pool.Allows(IPAddress.Parse("10.0.0.1"), 8099));
        Assert.False(pool.Allows(IPAddress.Parse("10.0.0.1"), 8100));
        Assert.False(pool.Allows(IPAddress.Parse("11.0.0.1"), 8000));
    }

    /// <summary>
    /// The two spellings of one address answer the same, whichever one the grant was written
    /// in.
    /// </summary>
    /// <remarks>
    /// The mapped form is not an obscure corner: it is what a socket opened for both families
    /// reports for a peer that arrived over the older one, so a grant written the ordinary way
    /// and checked against a value read off such a socket — one a caller made, since this
    /// library's own are single-stack — meets exactly this case.
    /// </remarks>
    [Fact]
    public void A_mapped_address_is_the_address_it_maps_to()
    {
        Pool written = Granting(IPAddress.Parse("127.0.0.1"), 443);
        Assert.True(written.Allows(IPAddress.Parse("::ffff:127.0.0.1"), 443));

        Pool mapped = Granting(IPAddress.Parse("::ffff:127.0.0.1"), 443);
        Assert.True(mapped.Allows(IPAddress.Parse("127.0.0.1"), 443));
    }

    /// <summary>
    /// Neither family's loopback is granted by the other's, in either spelling.
    /// </summary>
    /// <remarks>
    /// The two loopbacks are different interfaces with different services listening on them.
    /// Folding the mapped form into the older family must not make a grant for one reach the
    /// other.
    /// </remarks>
    [Fact]
    public void A_loopback_grant_does_not_cover_the_other_familys_loopback()
    {
        Pool v6 = Granting(IPAddress.IPv6Loopback, 443);
        Assert.False(v6.Allows(IPAddress.Parse("::ffff:127.0.0.1"), 443));
        Assert.False(v6.Allows(IPAddress.Loopback, 443));

        Pool v4 = Granting(IPAddress.Loopback, 443);
        Assert.False(v4.Allows(IPAddress.IPv6Loopback, 443));
    }

    /// <summary>The deprecated embedding is read the same way as the current one.</summary>
    /// <remarks>
    /// Still parsed by everything that parses addresses, and therefore still available to
    /// somebody hoping a check was written against one spelling only.
    /// </remarks>
    [Fact]
    public void A_compatible_address_is_the_address_it_embeds()
    {
        Pool pool = Granting(IPAddress.Parse("127.0.0.1"), 443);

        Assert.True(pool.Allows(IPAddress.Parse("::127.0.0.1"), 443));
    }

    /// <summary>The two addresses inside the embedding prefix that are not embeddings stay themselves.</summary>
    /// <remarks>
    /// Both sit inside the range that embeds the older family and neither is an embedding.
    /// Folding them in would make a grant for the older family's loopback cover the newer
    /// family's, which is a different interface's worth of services.
    /// </remarks>
    [Fact]
    public void The_unspecified_and_loopback_addresses_are_not_embeddings()
    {
        Pool loopback = Granting(IPAddress.Parse("0.0.0.1"), 443);
        Assert.False(loopback.Allows(IPAddress.IPv6Loopback, 443));

        Pool unspecified = Granting(IPAddress.Parse("0.0.0.0"), 443);
        Assert.False(unspecified.Allows(IPAddress.IPv6Any, 443));
    }

    /// <summary>A range given in the newer family covers the older family's spellings too.</summary>
    [Fact]
    public void A_range_inside_the_embedding_prefix_covers_the_family_it_embeds()
    {
        Pool pool = new PoolBuilder()
            .InsertIpNet(IPNetwork.Parse("::ffff:10.0.0.0/104"), PortRange.Only(443), AmbientAuthority.Acquire())
            .Build();

        Assert.True(pool.Allows(IPAddress.Parse("10.0.0.7"), 443));
        Assert.True(pool.Allows(IPAddress.Parse("::ffff:10.0.0.7"), 443));
    }

    /// <summary>A range given in the compatible form covers the older family's spellings too.</summary>
    /// <remarks>
    /// Addresses in the compatible form are already read as the address they embed, so a
    /// range in that form left in the newer family would be compared against nothing but
    /// older-family addresses and cover none of them.
    /// </remarks>
    [Fact]
    public void A_compatible_form_range_covers_the_family_it_embeds()
    {
        Pool pool = new PoolBuilder()
            .InsertIpNet(IPNetwork.Parse("::10.0.0.0/104"), PortRange.Only(443), AmbientAuthority.Acquire())
            .Build();

        Assert.True(pool.Allows(IPAddress.Parse("10.0.0.7"), 443));
        Assert.True(pool.Allows(IPAddress.Parse("::10.0.0.7"), 443));
        Assert.True(pool.Allows(IPAddress.Parse("::ffff:10.0.0.7"), 443));
        Assert.False(pool.Allows(IPAddress.Parse("11.0.0.1"), 443));
        Assert.False(pool.Allows(IPAddress.Parse("10.0.0.7"), 80));
    }

    /// <summary>
    /// A range in the compatible prefix that also covers the two addresses there which are
    /// not embeddings is refused.
    /// </summary>
    /// <remarks>
    /// Read as the older family it would drop the newer family's loopback; read as the newer
    /// family it would cover only that loopback and the unspecified address. Either reading
    /// surprises whoever wrote it, so neither is chosen for them. The two addresses on their
    /// own are not ranges and stay grants over themselves.
    /// </remarks>
    [Theory]
    [InlineData("::/96")]
    [InlineData("::/104")]
    [InlineData("::/127")]
    public void A_compatible_form_range_covering_the_non_embeddings_is_refused(string network)
    {
        var builder = new PoolBuilder();

        ArgumentException refused = Assert.Throws<ArgumentException>(() => builder.InsertIpNet(
            IPNetwork.Parse(network), PortRange.Every, AmbientAuthority.Acquire()));
        Assert.Equal("network", refused.ParamName);
    }

    /// <summary>The newer family's loopback granted as a one-address range is that address alone.</summary>
    [Fact]
    public void The_loopback_as_a_range_is_not_an_embedding()
    {
        Pool pool = new PoolBuilder()
            .InsertIpNet(IPNetwork.Parse("::1/128"), PortRange.Only(443), AmbientAuthority.Acquire())
            .Build();

        Assert.True(pool.Allows(IPAddress.IPv6Loopback, 443));
        Assert.False(pool.Allows(IPAddress.Parse("0.0.0.1"), 443));
    }

    /// <summary>A scope identifier does not make an address a different address.</summary>
    [Fact]
    public void A_scope_identifier_is_not_part_of_the_comparison()
    {
        Pool pool = Granting(IPAddress.Parse("fe80::1"), 443);

        Assert.True(pool.Allows(IPAddress.Parse("fe80::1%3"), 443));
    }

    /// <summary>
    /// A range grant does not reach the addresses an interface configures for itself,
    /// however wide the range.
    /// </summary>
    /// <remarks>
    /// The address in this test is the one hosted machines answer configuration questions on,
    /// credentials included. It is reachable from every process on such a machine without any
    /// routing, so a grant written to describe a network would otherwise hand it over as a
    /// side effect — and nothing in the way that grant was written would show it.
    /// </remarks>
    [Fact]
    public void A_range_grant_does_not_reach_a_link_local_address()
    {
        Pool pool = new PoolBuilder()
            .InsertIpNet(IPNetwork.Parse("0.0.0.0/1"), PortRange.Every, AmbientAuthority.Acquire())
            .InsertIpNet(IPNetwork.Parse("128.0.0.0/1"), PortRange.Every, AmbientAuthority.Acquire())
            .Build();

        Assert.True(pool.Allows(IPAddress.Parse("93.184.216.34"), 80));
        Assert.False(pool.Allows(IPAddress.Parse("169.254.169.254"), 80));
    }

    /// <summary>The same rule for the newer family's link-local addresses.</summary>
    [Fact]
    public void A_range_grant_does_not_reach_a_link_local_address_of_either_family()
    {
        Pool pool = new PoolBuilder()
            .InsertIpNet(IPNetwork.Parse("::/0"), PortRange.Every, AmbientAuthority.Acquire())
            .Build();

        Assert.True(pool.Allows(IPAddress.Parse("2001:db8::1"), 80));
        Assert.False(pool.Allows(IPAddress.Parse("fe80::1"), 80));
    }

    /// <summary>Naming such an address outright still reaches it.</summary>
    /// <remarks>
    /// The rule is about what falls out of a range, not about forbidding the address. A
    /// process whose job is to read instance configuration says so in one line, and that line
    /// is what a review reads.
    /// </remarks>
    [Fact]
    public void An_endpoint_grant_reaches_a_link_local_address()
    {
        Pool pool = Granting(IPAddress.Parse("169.254.169.254"), 80);

        Assert.True(pool.Allows(IPAddress.Parse("169.254.169.254"), 80));
    }

    /// <summary>A range that could only ever cover such addresses is refused outright.</summary>
    /// <remarks>
    /// Rather than accepted and then never matched. A grant that silently covers nothing
    /// reads, to whoever wrote it, exactly like a grant that works.
    /// </remarks>
    [Fact]
    public void A_range_wholly_inside_the_link_local_addresses_is_refused()
    {
        var builder = new PoolBuilder();

        Assert.Throws<ArgumentException>(() => builder.InsertIpNet(
            IPNetwork.Parse("169.254.0.0/16"), PortRange.Every, AmbientAuthority.Acquire()));

        Assert.Throws<ArgumentException>(() => builder.InsertIpNet(
            IPNetwork.Parse("fe80::/10"), PortRange.Every, AmbientAuthority.Acquire()));
    }

    /// <summary>The differently-named grant reaches everything, link-local included.</summary>
    /// <remarks>
    /// It is the one grant that says "no stated reach" rather than describing a reach, and a
    /// caller who writes it has been as explicit as the API can make them.
    /// </remarks>
    [Fact]
    public void The_wildcard_grant_reaches_everything()
    {
        Pool pool = new PoolBuilder().InsertEveryEndpoint(AmbientAuthority.Acquire()).Build();

        Assert.True(pool.GrantsEveryEndpoint);
        Assert.True(pool.Allows(IPAddress.Parse("169.254.169.254"), 80));
        Assert.True(pool.Allows(IPAddress.Parse("fe80::1"), 1));
    }

    /// <summary>The network nobody set is refused rather than read as all of IPv4.</summary>
    /// <remarks>
    /// The default <see cref="IPNetwork"/> is <c>0.0.0.0/0</c>, so a configuration record
    /// with its network left unset would otherwise grant every address of the older family.
    /// Written out it is the same value, so it is refused too, and the whole family is
    /// granted as its two halves instead.
    /// </remarks>
    [Fact]
    public void The_default_network_is_refused()
    {
        var builder = new PoolBuilder();

        ArgumentException unset = Assert.Throws<ArgumentException>(() => builder.InsertIpNet(
            default, PortRange.Every, AmbientAuthority.Acquire()));
        Assert.Equal("network", unset.ParamName);

        ArgumentException written = Assert.Throws<ArgumentException>(() => builder.InsertIpNet(
            IPNetwork.Parse("0.0.0.0/0"), PortRange.Every, AmbientAuthority.Acquire()));
        Assert.Equal("network", written.ParamName);

        Pool halves = builder
            .InsertIpNet(IPNetwork.Parse("0.0.0.0/1"), PortRange.Every, AmbientAuthority.Acquire())
            .InsertIpNet(IPNetwork.Parse("128.0.0.0/1"), PortRange.Every, AmbientAuthority.Acquire())
            .Build();
        Assert.True(halves.Allows(IPAddress.Parse("8.8.8.8"), 53));
        Assert.True(halves.Allows(IPAddress.Parse("203.0.113.1"), 53));
    }

    /// <summary>The whole of the newer family is nobody's default, so it is accepted.</summary>
    [Fact]
    public void The_whole_of_the_newer_family_is_accepted()
    {
        Pool pool = new PoolBuilder()
            .InsertIpNet(IPNetwork.Parse("::/0"), PortRange.Every, AmbientAuthority.Acquire())
            .Build();

        Assert.True(pool.Allows(IPAddress.Parse("2001:db8::1"), 443));
    }

    /// <summary>Port zero cannot be granted, because it is a request for a port.</summary>
    [Fact]
    public void Port_zero_cannot_be_granted()
    {
        var builder = new PoolBuilder();

        Assert.Throws<ArgumentException>(() => builder.InsertSocketAddress(
            new IPEndPoint(IPAddress.Loopback, 0), AmbientAuthority.Acquire()));
    }

    /// <summary>A grant with no ports in it is refused rather than silently useless.</summary>
    [Fact]
    public void A_range_grant_with_no_ports_is_refused()
    {
        var builder = new PoolBuilder();

        Assert.Throws<ArgumentException>(() => builder.InsertIpNet(
            IPNetwork.Parse("10.0.0.0/8"), PortRange.None, AmbientAuthority.Acquire()));
    }

    /// <summary>
    /// Every grant demands a token that was actually taken, so the decisions stay findable.
    /// </summary>
    [Fact]
    public void A_grant_refuses_a_token_that_was_never_acquired()
    {
        var builder = new PoolBuilder();

        Assert.Throws<ArgumentException>(() =>
            builder.InsertSocketAddress(new IPEndPoint(IPAddress.Loopback, 443), default));
        Assert.Throws<ArgumentException>(() =>
            builder.InsertIpNet(IPNetwork.Parse("10.0.0.0/8"), PortRange.Every, default));
        Assert.Throws<ArgumentException>(() => builder.InsertEveryEndpoint(default));
    }

    /// <summary>
    /// A pool that has been handed out does not change when the builder that made it is used
    /// again.
    /// </summary>
    /// <remarks>
    /// This is the property that makes handing a pool to a component a transfer of a stated
    /// reach rather than a loan of a mutable list. Without it, whoever kept the builder could
    /// widen what somebody else is holding, and the holder would have no way to notice.
    /// </remarks>
    [Fact]
    public void A_pool_does_not_change_after_it_is_built()
    {
        var builder = new PoolBuilder()
            .InsertSocketAddress(new IPEndPoint(IPAddress.Loopback, 443), AmbientAuthority.Acquire());

        Pool handed = builder.Build();

        builder.InsertEveryEndpoint(AmbientAuthority.Acquire());

        Assert.False(handed.GrantsEveryEndpoint);
        Assert.False(handed.Allows(IPAddress.Parse("10.0.0.1"), 443));
        Assert.True(builder.Build().GrantsEveryEndpoint);
    }

    private static Pool Granting(IPAddress address, int port) => new PoolBuilder()
        .InsertSocketAddress(new IPEndPoint(address, port), AmbientAuthority.Acquire())
        .Build();
}
