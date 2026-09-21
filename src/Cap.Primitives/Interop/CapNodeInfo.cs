namespace Cap.Primitives.Interop;

/// <summary>
/// The facts about a filesystem node that resolution decisions are made from.
/// </summary>
/// <remarks>
/// <para>
/// This is not a metadata type for callers. It carries three things and no more: what the
/// node is, which filesystem it lives on, and which node it is on that filesystem.
/// </para>
/// <para>
/// The identity pair matters as much as the type. A walk that has opened a directory and
/// wants to confirm it opened the directory it looked at has to compare something, and
/// comparing names is exactly the mistake — the name can have been reassigned between the
/// two calls. <see cref="VolumeId"/> and <see cref="NodeId"/> together are the kernel's own
/// answer to "is this the same object", and they are stable across a rename.
/// </para>
/// <para>
/// <see cref="VolumeId"/> also answers whether a step crossed a mount point, which a
/// sandbox may want to refuse: a mount appearing inside the subtree is a piece of an
/// unrelated filesystem grafted in, and whoever controls the mount table is outside the
/// trust boundary.
/// </para>
/// </remarks>
internal readonly struct CapNodeInfo
{
    /// <summary>Creates node information.</summary>
    public CapNodeInfo(CapNodeType type, ulong volumeId, ulong nodeId, uint reparseTag = 0)
    {
        Type = type;
        VolumeId = volumeId;
        NodeId = nodeId;
        ReparseTag = reparseTag;
    }

    /// <summary>What the node is.</summary>
    public CapNodeType Type { get; }

    /// <summary>
    /// The filesystem the node lives on: a Unix device number, or a Windows volume serial
    /// number. Two nodes with different values are on different filesystems, so a step
    /// between them crossed a mount point.
    /// </summary>
    public ulong VolumeId { get; }

    /// <summary>
    /// The node's identity within its filesystem: a Unix inode number, or the low 64 bits of
    /// a Windows file id. Unique per volume at any instant, though a filesystem is free to
    /// reuse one after the node is deleted, so it identifies rather than authenticates.
    /// </summary>
    public ulong NodeId { get; }

    /// <summary>
    /// Windows only, and zero elsewhere: the reparse tag, when <see cref="Type"/> is
    /// <see cref="CapNodeType.SymbolicLink"/> or
    /// <see cref="CapNodeType.UnknownReparsePoint"/>. The tag is what separates a symbolic
    /// link from a junction from something that is not a link at all, and treating an
    /// unrecognised tag as a link is the failure this field exists to prevent.
    /// </summary>
    public uint ReparseTag { get; }

    /// <summary>True when the node is on a different filesystem from <paramref name="other"/>.</summary>
    public bool CrossesVolumeBoundaryFrom(in CapNodeInfo other) => VolumeId != other.VolumeId;

    /// <summary>True when this and <paramref name="other"/> name the same filesystem object.</summary>
    public bool IsSameNodeAs(in CapNodeInfo other) =>
        VolumeId == other.VolumeId && NodeId == other.NodeId;
}
