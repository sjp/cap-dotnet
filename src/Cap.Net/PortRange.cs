using System.Globalization;

namespace Cap.Net;

/// <summary>
/// A contiguous run of ports, as one half of a grant over a range of addresses.
/// </summary>
/// <remarks>
/// <para>
/// A grant written in terms of addresses alone would say "this component may reach these
/// machines", which is almost never what anybody means. What they mean is that it may reach
/// a service, and a service is a port. Requiring the ports alongside the addresses is what
/// keeps a grant for one service on a subnet from also being a grant for every other service
/// on it — the database as well as the cache, the administrative interface as well as the
/// API.
/// </para>
/// <para>
/// Port zero is not a port and is refused everywhere here. It is the request "choose one for
/// me", so it names nothing a grant could be about; a socket that asks for it is checked
/// against the port the system actually assigned, once that is known.
/// </para>
/// <para>
/// The default value is the empty range, which contains nothing. That is deliberate: a range
/// nobody built should grant nothing rather than grant whatever sits at the bottom of the
/// numbering.
/// </para>
/// </remarks>
public readonly struct PortRange : IEquatable<PortRange>
{
    /// <summary>The lowest port that can be named.</summary>
    private const int LowestPort = 1;

    /// <summary>The highest port that can be named.</summary>
    private const int HighestPort = 65535;

    private readonly int _first;
    private readonly int _count;

    private PortRange(int first, int count)
    {
        _first = first;
        _count = count;
    }

    /// <summary>Every port there is.</summary>
    /// <remarks>
    /// Still a range rather than a wildcard: it says nothing about which addresses may be
    /// reached, and a grant using it is as narrow as the network it is paired with.
    /// </remarks>
    public static PortRange Every => new(LowestPort, HighestPort - LowestPort + 1);

    /// <summary>The range that contains no port at all.</summary>
    public static PortRange None => default;

    /// <summary>Whether this range contains no port.</summary>
    public bool IsEmpty => _count == 0;

    /// <summary>The lowest port in the range, or -1 when it is empty.</summary>
    public int First => _count == 0 ? -1 : _first;

    /// <summary>The highest port in the range, or -1 when it is empty.</summary>
    public int Last => _count == 0 ? -1 : _first + _count - 1;

    /// <summary>A range of the single port <paramref name="port"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="port"/> is not a port a service can be reached at.
    /// </exception>
    public static PortRange Only(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, LowestPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, HighestPort);

        return new PortRange(port, 1);
    }

    /// <summary>
    /// A range from <paramref name="first"/> to <paramref name="last"/>, both included.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Either end is not a port a service can be reached at, or the range runs backwards.
    /// </exception>
    public static PortRange Between(int first, int last)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(first, LowestPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(last, HighestPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(first, last);

        return new PortRange(first, last - first + 1);
    }

    /// <summary>Whether <paramref name="port"/> falls in this range.</summary>
    public bool Contains(int port) => _count != 0 && port >= _first && port <= _first + _count - 1;

    /// <inheritdoc/>
    public bool Equals(PortRange other) => _first == other._first && _count == other._count;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PortRange other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_first, _count);

    /// <summary>The range as it would be written down.</summary>
    /// <remarks>For logs and assertion messages; nothing parses it.</remarks>
    public override string ToString() => _count switch
    {
        0 => "no port",
        1 => _first.ToString(CultureInfo.InvariantCulture),
        _ => string.Create(CultureInfo.InvariantCulture, $"{_first}-{_first + _count - 1}"),
    };

    /// <summary>Whether two ranges cover the same ports.</summary>
    public static bool operator ==(PortRange left, PortRange right) => left.Equals(right);

    /// <summary>Whether two ranges cover different ports.</summary>
    public static bool operator !=(PortRange left, PortRange right) => !left.Equals(right);
}
