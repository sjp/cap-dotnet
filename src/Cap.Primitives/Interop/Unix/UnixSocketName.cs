using System.Runtime.InteropServices;

namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// An address for a socket in the Unix domain, and the open descriptor that gives it its
/// meaning.
/// </summary>
/// <remarks>
/// <para>
/// The two are one value because neither is usable without the other. The address is written
/// in terms of a descriptor, so it names the object that descriptor refers to for exactly as
/// long as it stays open; once it is closed the address means nothing, and — worse — the
/// number it was written from can be handed straight back out by the next open, at which
/// point the address names something else entirely. Keeping the descriptor alongside the
/// text is what makes that unrepresentable: the address cannot outlive what it refers to
/// without the code that holds it having said so.
/// </para>
/// <para>
/// Hold it across the whole of the call that uses the address, and dispose it afterwards.
/// For a bind, the socket the call created keeps its own reference to the directory entry,
/// so disposing this afterwards leaves the bound socket alone.
/// </para>
/// </remarks>
internal sealed class UnixSocketName : IDisposable
{
    private readonly SafeHandle _anchor;

    /// <summary>Pairs an address with the descriptor it is written from.</summary>
    public UnixSocketName(SafeHandle anchor, string address, CapFileType kind)
    {
        _anchor = anchor;
        Address = address;
        Kind = kind;
    }

    /// <summary>The address, as a socket call in this domain takes one.</summary>
    public string Address { get; }

    /// <summary>
    /// What the descriptor landed on, or <see cref="CapFileType.Unknown"/> where the address
    /// names something that does not exist yet.
    /// </summary>
    /// <remarks>
    /// Read from the descriptor rather than from the name, so it describes the object the
    /// address already refers to and cannot be invalidated by anything that happens to the
    /// name afterwards. Whether a kind other than a socket is acceptable is not decided
    /// here.
    /// </remarks>
    public CapFileType Kind { get; }

    /// <summary>Closes the descriptor the address was written from.</summary>
    public void Dispose() => _anchor.Dispose();
}
