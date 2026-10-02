using System.Net;
using System.Net.Sockets;

namespace Cap.Net;

/// <summary>
/// Reduces an address to the one form a grant is compared in.
/// </summary>
/// <remarks>
/// <para>
/// An allowlist that compares addresses as they were written is not an allowlist. The same
/// host can be spelled several ways, and the spellings are not string variants of each other
/// — they are different address families holding the same thirty-two bits. A check against
/// <c>127.0.0.1</c> that is handed <c>::ffff:127.0.0.1</c> sees an address of another family
/// and answers that it is not in the list, and the connection that follows reaches the
/// loopback interface exactly as the refused one would have.
/// </para>
/// <para>
/// So everything a grant holds and everything a grant is tested against passes through here
/// first, and the comparison happens in one family. Two spellings reduce to one form:
/// </para>
/// <list type="bullet">
/// <item>
/// The mapped form, <c>::ffff:a.b.c.d</c>, which is what a socket that accepts both families
/// reports for a peer that arrived over the older one.
/// </item>
/// <item>
/// The compatible form, <c>::a.b.c.d</c>, deprecated for two decades and still accepted by
/// address parsers, which is reason enough for it to appear in a request somebody hoped
/// would not be checked.
/// </item>
/// </list>
/// <para>
/// <strong>What is not reduced is anything that merely embeds an address without being it.</strong>
/// A tunnelling address reaches its host through a relay and is a different endpoint at the
/// layer this library checks at, so it is left alone; folding it in would make a grant cover
/// a path nobody granted.
/// </para>
/// <para>
/// <strong>Scope identifiers are dropped.</strong> They name an interface rather than a host,
/// they are absent from most written forms of the addresses that can carry one, and a grant
/// that matched only when the caller had guessed the same interface index would be a grant
/// that usually failed. The addresses that can carry one are link-local, and those are
/// already outside what a grant over a range reaches.
/// </para>
/// </remarks>
internal static class EndpointNormalization
{
    /// <summary>How many bytes an address of the older family occupies.</summary>
    private const int ShortAddressLength = 4;

    /// <summary>How many bytes an address of the newer family occupies.</summary>
    private const int LongAddressLength = 16;

    /// <summary>Where the embedded older address begins in the two embedding forms.</summary>
    private const int EmbeddedOffset = LongAddressLength - ShortAddressLength;

    /// <summary>The prefix length at which a network of the newer family embeds one of the older.</summary>
    private const int EmbeddingPrefixLength = EmbeddedOffset * 8;

    /// <summary>
    /// The prefix length of the older family's addresses an interface configures for itself.
    /// </summary>
    private const int ShortLinkLocalPrefixLength = 16;

    /// <summary>
    /// The prefix length of the newer family's addresses an interface configures for itself.
    /// </summary>
    private const int LongLinkLocalPrefixLength = 10;

    /// <summary>
    /// The addresses outside the link-local ranges that hosted machines answer configuration
    /// questions on, credentials included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each is here for the reason the link-local ranges are: it is reachable from the
    /// instance without anybody having routed to it, so a range that happens to contain it
    /// would hand it over as a side effect.
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <c>fd00:ec2::254</c>, the newer-family address of the AWS instance metadata service.
    /// </item>
    /// <item>
    /// <c>64:ff9b::169.254.0.0/112</c>, the older family's link-local range behind the
    /// well-known NAT64 prefix. A tunnelling address is not folded into the address it embeds
    /// (see <see cref="EndpointNormalization"/>), so without this entry a gateway would carry
    /// a range grant to the very endpoint the link-local rule keeps out.
    /// </item>
    /// <item>
    /// <c>100.100.100.200</c>, the Alibaba Cloud metadata service. Only that address: the
    /// shared address space around it is routed in many private deployments.
    /// </item>
    /// </list>
    /// </remarks>
    private static readonly IPNetwork[] ConfigurationEndpoints =
    [
        IPNetwork.Parse("fd00:ec2::254/128"),
        IPNetwork.Parse("64:ff9b::169.254.0.0/112"),
        IPNetwork.Parse("100.100.100.200/32"),
    ];

    /// <summary>The single form <paramref name="address"/> is compared in.</summary>
    public static IPAddress Normalize(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        Span<byte> bytes = stackalloc byte[LongAddressLength];
        if (!address.TryWriteBytes(bytes, out int written) || written != LongAddressLength)
        {
            return address;
        }

        if (IsCompatibleForm(bytes))
        {
            return new IPAddress(bytes[EmbeddedOffset..]);
        }

        // Rebuilt without the scope rather than returned as it arrived, so that two values
        // for one host compare equal however each of them was obtained.
        return address.ScopeId == 0 ? address : new IPAddress(bytes);
    }

    /// <summary>The single form <paramref name="network"/> is compared in.</summary>
    /// <remarks>
    /// A network of the newer family that lies wholly inside the embedding prefix describes a
    /// set of older-family addresses, and is rewritten as that set so it covers them however
    /// they are spelled. A shorter prefix is not: it covers far more than the embedded range,
    /// and turning it into a grant over the older family would widen it into a family nobody
    /// named.
    /// </remarks>
    public static IPNetwork Normalize(IPNetwork network)
    {
        if (network.BaseAddress.AddressFamily != AddressFamily.InterNetworkV6 ||
            network.PrefixLength < EmbeddingPrefixLength)
        {
            return network;
        }

        if (network.BaseAddress.IsIPv4MappedToIPv6)
        {
            return new IPNetwork(
                network.BaseAddress.MapToIPv4(),
                network.PrefixLength - EmbeddingPrefixLength);
        }

        Span<byte> bytes = stackalloc byte[LongAddressLength];
        if (!network.BaseAddress.TryWriteBytes(bytes, out int written) ||
            written != LongAddressLength ||
            !IsCompatibleForm(bytes))
        {
            return network;
        }

        return new IPNetwork(
            new IPAddress(bytes[EmbeddedOffset..]),
            network.PrefixLength - EmbeddingPrefixLength);
    }

    /// <summary>
    /// Whether <paramref name="network"/> lies inside the compatible embedding's prefix and
    /// also covers the two addresses there that are not embeddings.
    /// </summary>
    /// <remarks>
    /// Such a range is neither a set of older-family addresses nor a plain range of the newer
    /// family: read one way it would quietly drop the newer family's loopback, read the other
    /// it would cover nothing but that loopback and the unspecified address. Only a range
    /// based at the unspecified address can cover either of them, since every other range in
    /// the prefix starts above both. The single address <c>::/128</c> is not such a range; it
    /// is just itself.
    /// </remarks>
    public static bool StraddlesCompatibleForm(IPNetwork network) =>
        network.BaseAddress.AddressFamily == AddressFamily.InterNetworkV6 &&
        network.PrefixLength >= EmbeddingPrefixLength &&
        network.PrefixLength < LongAddressLength * 8 &&
        SameAddress(network.BaseAddress, IPAddress.IPv6Any);

    /// <summary>Whether two normalized addresses name the same host.</summary>
    public static bool SameAddress(IPAddress left, IPAddress right)
    {
        if (left.AddressFamily != right.AddressFamily)
        {
            return false;
        }

        Span<byte> first = stackalloc byte[LongAddressLength];
        Span<byte> second = stackalloc byte[LongAddressLength];

        return left.TryWriteBytes(first, out int firstLength) &&
            right.TryWriteBytes(second, out int secondLength) &&
            firstLength == secondLength &&
            first[..firstLength].SequenceEqual(second[..secondLength]);
    }

    /// <summary>
    /// Whether the normalized <paramref name="address"/> is the unspecified address of either
    /// family, which names no host.
    /// </summary>
    /// <remarks>
    /// The mapped spelling of the older family's unspecified address has already been folded
    /// into it by <see cref="Normalize(IPAddress)"/>, so the two plain forms are all there is
    /// to test for.
    /// </remarks>
    public static bool IsUnspecified(IPAddress address) =>
        SameAddress(address, IPAddress.Any) || SameAddress(address, IPAddress.IPv6Any);

    /// <summary>
    /// Whether the normalized <paramref name="address"/> is one a grant over a range does not
    /// reach: a link-local address, or one of the instance-configuration endpoints that sit
    /// outside the link-local ranges.
    /// </summary>
    public static bool IsBeyondRangeGrants(IPAddress address)
    {
        if (IsLinkLocal(address))
        {
            return true;
        }

        foreach (IPNetwork endpoint in ConfigurationEndpoints)
        {
            if (endpoint.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether every address in the normalized <paramref name="network"/> is one a grant over
    /// a range does not reach.
    /// </summary>
    public static bool IsWhollyBeyondRangeGrants(IPNetwork network)
    {
        if (IsWhollyLinkLocal(network))
        {
            return true;
        }

        foreach (IPNetwork endpoint in ConfigurationEndpoints)
        {
            if (network.PrefixLength >= endpoint.PrefixLength && endpoint.Contains(network.BaseAddress))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="address"/> is one an interface configures for itself rather
    /// than one anybody routes to.
    /// </summary>
    /// <remarks>
    /// The two ranges together are what a host reaches without leaving the wire it is
    /// plugged into, and the well-known address that hosted services answer configuration
    /// questions on — credentials included — is one of them.
    /// </remarks>
    private static bool IsLinkLocal(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal;
        }

        Span<byte> bytes = stackalloc byte[ShortAddressLength];
        return address.TryWriteBytes(bytes, out int written) &&
            written == ShortAddressLength &&
            bytes[0] == 169 &&
            bytes[1] == 254;
    }

    /// <summary>Whether every address in <paramref name="network"/> is link-local.</summary>
    private static bool IsWhollyLinkLocal(IPNetwork network) =>
        IsLinkLocal(network.BaseAddress) &&
        network.PrefixLength >= (network.BaseAddress.AddressFamily == AddressFamily.InterNetworkV6
            ? LongLinkLocalPrefixLength
            : ShortLinkLocalPrefixLength);

    /// <summary>
    /// Whether the sixteen bytes hold an older-family address in the deprecated embedding.
    /// </summary>
    /// <remarks>
    /// The two lowest values in that range are excluded because they are not embeddings at
    /// all: they are the unspecified address and the loopback address of the newer family,
    /// which happen to sit inside the prefix and mean something else entirely.
    /// </remarks>
    private static bool IsCompatibleForm(ReadOnlySpan<byte> bytes)
    {
        if (bytes[..EmbeddedOffset].ContainsAnyExcept((byte)0))
        {
            return false;
        }

        ReadOnlySpan<byte> embedded = bytes[EmbeddedOffset..];
        return embedded.ContainsAnyExcept((byte)0) && !(embedded[0] == 0 && embedded[1] == 0 &&
            embedded[2] == 0 && embedded[3] == 1);
    }
}
