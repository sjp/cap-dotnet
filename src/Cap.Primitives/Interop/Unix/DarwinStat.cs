using System.Runtime.InteropServices;

namespace Cap.Primitives.Interop.Unix;

/// <summary>A <c>struct timespec</c> as macOS lays it out on a 64-bit platform.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DarwinTimespec
{
    public long Seconds;
    public long Nanoseconds;
}

/// <summary>
/// The macOS <c>struct stat</c>, in its 64-bit-inode form.
/// </summary>
/// <remarks>
/// <para>
/// Declared in full, including the fields nothing reads, because what the kernel copies out
/// is a fixed-size record and a short declaration would be written past. The fields that
/// matter to resolution are three: the type bits, the device, and the inode.
/// </para>
/// <para>
/// macOS has two of these. The older one carried a 32-bit inode number, and the 64-bit
/// version was introduced alongside it under decorated symbol names so that existing
/// binaries kept the old layout. On Apple silicon only the 64-bit form exists and the plain
/// names refer to it; on Intel both do, and the plain names still refer to the *old* one.
/// This is the 64-bit layout, which is why the import picks its entry point by
/// architecture — calling the undecorated name on Intel would fill this structure from a
/// differently shaped one, and every field after the mode bits would be read from the wrong
/// offset.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct DarwinStat
{
    /// <summary>The filesystem the node lives on.</summary>
    public int Device;

    /// <summary>The type bits and permission bits.</summary>
    public ushort Mode;

    public ushort HardLinkCount;

    /// <summary>The inode number.</summary>
    public ulong Inode;

    public uint UserId;
    public uint GroupId;

    /// <summary>The device this node represents, when it is a device node.</summary>
    public int RepresentedDevice;

    public DarwinTimespec AccessTime;
    public DarwinTimespec ModifyTime;
    public DarwinTimespec ChangeTime;
    public DarwinTimespec BirthTime;

    public long Size;
    public long Blocks;
    public int BlockSize;
    public uint Flags;
    public uint Generation;

    /// <summary>Reserved by the kernel. Declared so the structure is the size the kernel writes.</summary>
    public int ReservedSpare;

    /// <summary>Reserved by the kernel.</summary>
    public long ReservedQuad0;

    /// <summary>Reserved by the kernel.</summary>
    public long ReservedQuad1;

    /// <summary>The size the kernel expects, checked by the layout tests.</summary>
    public static unsafe int StructSize => sizeof(DarwinStat);

    /// <summary>The filesystem identity, widened to the shared representation.</summary>
    public readonly ulong VolumeId => unchecked((ulong)(long)Device);

    /// <summary>Reads the type bits.</summary>
    public readonly CapNodeType NodeType => (Mode & DarwinConstants.S_IFMT) switch
    {
        DarwinConstants.S_IFDIR => CapNodeType.Directory,
        DarwinConstants.S_IFREG => CapNodeType.File,
        DarwinConstants.S_IFLNK => CapNodeType.SymbolicLink,
        _ => CapNodeType.Other,
    };
}
