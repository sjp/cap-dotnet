namespace Cap.Primitives.Interop;

/// <summary>
/// Everything a caller is told about a filesystem object, taken in one call.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="CapNodeInfo"/> for the question a caller asks rather than
/// the one resolution asks. The two are kept apart because they want different things and
/// pay differently for them. Resolution wants the type and the identity, wants them on the
/// path of every component, and is content with whatever a network filesystem has cached; a
/// caller asking what a file is wants the size and the times as well, asks once, and would
/// not thank a cached answer for reporting a length the file no longer has.
/// </para>
/// <para>
/// Every field is filled from a single call, so the snapshot is internally consistent: the
/// length and the times describe the object as it was at one instant rather than at several.
/// It is still a snapshot, and nothing keeps it true afterwards — a value like this one
/// cannot be stale in the sense of disagreeing with itself, only in the ordinary sense of
/// describing the past.
/// </para>
/// <para>
/// The permission halves are deliberately both nullable and never both filled. A Unix mode
/// and a Windows attribute set are different things that happen to occupy the same place in
/// a caller's mental model, and a structure that carried a default for the absent one would
/// invite code that read it and believed the answer.
/// </para>
/// </remarks>
internal readonly struct CapNodeStat
{
    /// <summary>Creates a snapshot.</summary>
    public CapNodeStat(
        CapFileType type,
        ulong volumeId,
        UInt128 nodeId,
        long length,
        DateTimeOffset lastAccessTime,
        DateTimeOffset lastWriteTime,
        DateTimeOffset? creationTime,
        UnixFileMode? unixMode,
        FileAttributes? windowsAttributes,
        uint? unixOwnerId = null)
    {
        Type = type;
        VolumeId = volumeId;
        NodeId = nodeId;
        Length = length;
        LastAccessTime = lastAccessTime;
        LastWriteTime = lastWriteTime;
        CreationTime = creationTime;
        UnixMode = unixMode;
        WindowsAttributes = windowsAttributes;
        UnixOwnerId = unixOwnerId;
    }

    /// <summary>What the object is, in the full set of kinds a caller can be told apart.</summary>
    public CapFileType Type { get; }

    /// <summary>
    /// The filesystem the object lives on: a Unix device number, or a Windows volume serial
    /// number.
    /// </summary>
    public ulong VolumeId { get; }

    /// <summary>
    /// The object's identity within its filesystem: a Unix inode number, or a Windows file
    /// identifier.
    /// </summary>
    /// <remarks>
    /// Held at the full width the widest platform uses rather than at the width most
    /// platforms need. A Windows file identifier is 128 bits because the 64-bit one it
    /// replaced is not unique on every filesystem the system supports, and narrowing it here
    /// would make two distinct objects on such a filesystem compare equal — which is the one
    /// failure an identity check must not have.
    /// </remarks>
    public UInt128 NodeId { get; }

    /// <summary>The length the filesystem reports for the object, in bytes.</summary>
    public long Length { get; }

    /// <summary>When the object's contents were last read.</summary>
    public DateTimeOffset LastAccessTime { get; }

    /// <summary>When the object's contents were last changed.</summary>
    public DateTimeOffset LastWriteTime { get; }

    /// <summary>
    /// When the object was created, or null where the filesystem does not record it.
    /// </summary>
    public DateTimeOffset? CreationTime { get; }

    /// <summary>
    /// The Unix permission and mode bits, or null on a platform that has none.
    /// </summary>
    public UnixFileMode? UnixMode { get; }

    /// <summary>
    /// The Windows file attribute bits, or null on a platform that has none.
    /// </summary>
    public FileAttributes? WindowsAttributes { get; }

    /// <summary>
    /// The user id of the account that owns the object, or null on a platform that records
    /// ownership some other way.
    /// </summary>
    /// <remarks>
    /// Needed where a location's safety depends on who owns it rather than on what its mode
    /// allows: a directory whose mode shuts everybody else out still belongs to whoever
    /// created it, and if that is not the current account then everything placed inside it
    /// is readable by them.
    /// </remarks>
    public uint? UnixOwnerId { get; }
}
