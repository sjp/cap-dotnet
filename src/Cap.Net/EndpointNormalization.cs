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

        if (!network.BaseAddress.IsIPv4MappedToIPv6)
        {
            return network;
        }

        return new IPNetwork(
            network.BaseAddress.MapToIPv4(),
            network.PrefixLength - EmbeddingPrefixLength);
    }

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
    /// Whether <paramref name="address"/> is one an interface configures for itself rather
    /// than one anybody routes to.
    /// </summary>
    /// <remarks>
    /// The two ranges together are what a host reaches without leaving the wire it is
    /// plugged into, and the well-known address that hosted services answer configuration
    /// questions on — credentials included — is one of them.
    /// </remarks>
    public static bool IsLinkLocal(IPAddress address)
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

    /// <summary>
    /// Whether every address in <paramref name="network"/> is one a grant over a range does
    /// not reach.
    /// </summary>
    public static bool IsWhollyLinkLocal(IPNetwork network) =>
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
