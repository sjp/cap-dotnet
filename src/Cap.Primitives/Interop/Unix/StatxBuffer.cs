using System.Runtime.InteropServices;

namespace Cap.Primitives.Interop.Unix;

/// <summary>One timestamp in a <c>statx</c> result.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StatxTimestamp
{
    public long Seconds;
    public uint Nanoseconds;
    public int Reserved;
}

/// <summary>
/// The <c>statx</c> result structure.
/// </summary>
/// <remarks>
/// <para>
/// <c>statx</c> rather than <c>fstatat</c>, for one reason above all others: this structure
/// has the same layout on every architecture, where <c>struct stat</c> does not. Declaring
/// <c>struct stat</c> would mean one declaration per architecture, each checked only by the
/// build agent that runs that architecture, and a mistake in either would produce a
/// plausible-looking inode read out of the wrong offset — an identity check that compares
/// the wrong numbers and silently always agrees, or silently never does.
/// </para>
/// <para>
/// The cost is a floor of Linux 4.11, where <c>statx</c> was added. Every distribution
/// release and container base image still in support is well above it; a kernel below it
/// would fail every stat with "function not implemented" rather than misbehaving.
/// </para>
/// <para>
/// Most of the structure is unused. Resolution asks three questions — what is this, which
/// filesystem is it on, and which object is it — and the request mask is set to match, so
/// the kernel is not asked to fill in fields nobody reads. The fields are still declared in
/// full because the structure's size is part of the kernel's contract.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct StatxBuffer
{
    /// <summary>Which fields the kernel actually filled in. Not every request is honoured.</summary>
    public uint Mask;

    public uint BlockSize;
    public ulong Attributes;
    public uint HardLinkCount;
    public uint UserId;
    public uint GroupId;

    /// <summary>The type bits and permission bits, as in <c>st_mode</c>.</summary>
    public ushort Mode;

    /// <summary>Kernel padding. Declared because the structure's size is part of the contract.</summary>
    public ushort Reserved0;

    /// <summary>The inode number.</summary>
    public ulong Inode;

    public ulong Size;
    public ulong Blocks;
    public ulong AttributesMask;

    public StatxTimestamp AccessTime;
    public StatxTimestamp BirthTime;
    public StatxTimestamp ChangeTime;
    public StatxTimestamp ModifyTime;

    public uint RepresentedDeviceMajor;
    public uint RepresentedDeviceMinor;

    /// <summary>Major number of the filesystem the node lives on.</summary>
    public uint DeviceMajor;

    /// <summary>Minor number of the filesystem the node lives on.</summary>
    public uint DeviceMinor;

    public ulong MountId;
    public uint DirectIoMemoryAlignment;
    public uint DirectIoOffsetAlignment;

    /// <summary>Kernel padding, reserved for fields later versions may add.</summary>
    public unsafe fixed ulong Reserved3[12];

    /// <summary>The size the kernel expects, checked by the layout tests.</summary>
    public static unsafe int StructSize => sizeof(StatxBuffer);

    /// <summary>
    /// The filesystem identity, as one opaque number.
    /// </summary>
    /// <remarks>
    /// The two halves are packed rather than run through the C <c>makedev</c> encoding,
    /// which is not the same on every platform and which nothing here needs to decode. What
    /// matters is only that two nodes on the same filesystem produce the same value and two
    /// on different filesystems do not.
    /// </remarks>
    public readonly ulong VolumeId => ((ulong)DeviceMajor << 32) | DeviceMinor;

    /// <summary>Reads the type bits.</summary>
    public readonly CapNodeType NodeType => (Mode & LinuxConstants.S_IFMT) switch
    {
        LinuxConstants.S_IFDIR => CapNodeType.Directory,
        LinuxConstants.S_IFREG => CapNodeType.File,
        LinuxConstants.S_IFLNK => CapNodeType.SymbolicLink,
        _ => CapNodeType.Other,
    };
}
