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
/// <see cref="MountId"/> answers whether a step crossed a mount point, which a sandbox may
/// want to refuse: a mount appearing inside the subtree is a piece of an unrelated
/// filesystem — or of another part of the same one — grafted in, and whoever controls the
/// mount table is outside the trust boundary.
/// </para>
/// </remarks>
internal readonly struct CapNodeInfo
{
    /// <summary>Creates node information.</summary>
    public CapNodeInfo(CapNodeType type, ulong volumeId, ulong nodeId, uint reparseTag = 0, ulong? mountId = null)
    {
        Type = type;
        VolumeId = volumeId;
        NodeId = nodeId;
        ReparseTag = reparseTag;
        MountId = mountId ?? volumeId;
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
    /// The mount the node was reached through: the Linux <c>statx</c> mount id where the
    /// kernel reports one, and otherwise <see cref="VolumeId"/>.
    /// </summary>
    /// <remarks>
    /// Not the same question as <see cref="VolumeId"/>. A bind mount of a directory on the
    /// same filesystem keeps its device number, so a walk comparing volumes steps into it
    /// without noticing, where the kernel's own refusal to cross a mount does not. Two
    /// values from different sources are never compared: one host either reports mount ids
    /// for every node or for none.
    /// </remarks>
    public ulong MountId { get; }

    /// <summary>
    /// Windows only, and zero elsewhere: the reparse tag, when the node has one. That is
    /// always so when <see cref="Type"/> is <see cref="CapNodeType.SymbolicLink"/> or
    /// <see cref="CapNodeType.UnknownReparsePoint"/>, and can be so for a
    /// <see cref="CapNodeType.File"/> or <see cref="CapNodeType.Directory"/> whose contents a
    /// filter serves, such as a compressed file or a cloud placeholder. The tag is what
    /// separates a symbolic link from a junction from something that is not a link at all,
    /// and treating an unrecognised tag as a link is the failure this field exists to prevent.
    /// </summary>
    public uint ReparseTag { get; }

    /// <summary>True when the node is on a different filesystem from <paramref name="other"/>.</summary>
    public bool CrossesVolumeBoundaryFrom(in CapNodeInfo other) => VolumeId != other.VolumeId;

    /// <summary>True when the node was reached through a different mount from <paramref name="other"/>.</summary>
    public bool CrossesMountFrom(in CapNodeInfo other) => MountId != other.MountId;

    /// <summary>True when this and <paramref name="other"/> name the same filesystem object.</summary>
    public bool IsSameNodeAs(in CapNodeInfo other) =>
        VolumeId == other.VolumeId && NodeId == other.NodeId;
}
