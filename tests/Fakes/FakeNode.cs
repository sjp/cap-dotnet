using Cap.Primitives.Interop;

namespace Cap.Tests.Fakes;

/// <summary>
/// One object in a simulated filesystem.
/// </summary>
/// <remarks>
/// Deliberately mutable and reachable from a test while a resolution is in flight. That is
/// the whole point of the simulation: the interesting failures in a path walk are the ones
/// where a component is one thing when it is looked at and another when it is opened, and
/// against a real kernel those can only be provoked by running an attack in a loop and
/// hoping to land in the window.
/// </remarks>
internal sealed class FakeNode
{
    /// <summary>What this object is.</summary>
    public CapNodeType Type { get; set; } = CapNodeType.Directory;

    /// <summary>Which simulated filesystem it lives on.</summary>
    public ulong VolumeId { get; set; }

    /// <summary>Its identity within that filesystem.</summary>
    public ulong NodeId { get; set; }

    /// <summary>The stored target, when this is a link.</summary>
    public string? LinkTarget { get; set; }

    /// <summary>The reparse tag, for modelling a Windows link that is not a filesystem link.</summary>
    public uint ReparseTag { get; set; }

    /// <summary>True when every operation on this object should be refused.</summary>
    public bool Unreadable { get; set; }

    /// <summary>Entries, when this is a directory.</summary>
    public Dictionary<string, FakeNode> Entries { get; } = new(StringComparer.Ordinal);

    /// <summary>Its description, as the platform layer would report it.</summary>
    public CapNodeInfo Info => new(Type, VolumeId, NodeId, ReparseTag);
}
