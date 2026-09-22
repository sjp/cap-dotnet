using Cap.Primitives;
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

    /// <summary>
    /// True when a read of the directory holding this entry should decline to say what it
    /// is, leaving the kind to be looked up separately.
    /// </summary>
    /// <remarks>
    /// Several real filesystems answer that way for every entry they hold. Modelling it is
    /// the only way to exercise the lookup that covers for them without needing one of those
    /// filesystems mounted on the machine running the tests.
    /// </remarks>
    public bool HidesKindFromDirectoryRead { get; set; }

    /// <summary>
    /// What a caller reading the directory is told this is, overriding what
    /// <see cref="Type"/> implies.
    /// </summary>
    /// <remarks>
    /// Resolution collapses sockets, pipes and device nodes into one case because it has no
    /// use for the difference; a caller listing a directory is told which it is. Setting
    /// this is how a test produces one of those without the simulation having to model what
    /// they are.
    /// </remarks>
    public CapFileType? EntryType { get; set; }

    /// <summary>Entries, when this is a directory.</summary>
    public Dictionary<string, FakeNode> Entries { get; } = new(StringComparer.Ordinal);

    /// <summary>Its description, as the platform layer would report it.</summary>
    public CapNodeInfo Info => new(Type, VolumeId, NodeId, ReparseTag);

    /// <summary>Its kind, as a caller reading the directory is told it.</summary>
    public CapFileType FileType => EntryType ?? Type switch
    {
        CapNodeType.File => CapFileType.File,
        CapNodeType.Directory => CapFileType.Directory,
        CapNodeType.SymbolicLink => CapFileType.Symlink,
        CapNodeType.UnknownReparsePoint => CapFileType.ReparsePoint,
        _ => CapFileType.Unknown,
    };
}
