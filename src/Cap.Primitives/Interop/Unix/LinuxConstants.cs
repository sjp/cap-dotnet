using System.Runtime.InteropServices;

namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// The Linux targets this backend carries flag values and syscall numbers for: the ones .NET
/// itself supports on Linux.
/// </summary>
/// <remarks>
/// Anything else is <see cref="Unsupported"/>, and the backend refuses to start there rather
/// than guess. A community port of the runtime to another architecture loads this library
/// perfectly well, which is exactly why the refusal has to be explicit: nothing else would
/// stop it issuing another architecture's syscall numbers and flag bits.
/// </remarks>
internal enum LinuxAbi
{
    /// <summary>An architecture with no table here. Every lookup against it throws.</summary>
    Unsupported,

    /// <summary>x86-64: the generic open flags, and its own syscall table.</summary>
    X64,

    /// <summary>AArch64: 32-bit ARM's open flags, and the generic syscall table.</summary>
    Arm64,

    /// <summary>
    /// 32-bit ARM (EABI): the same open flags as AArch64, its own syscall table, and a 32-bit
    /// <c>long</c>, <c>off_t</c> and <c>time_t</c> in the C library's default interfaces.
    /// </summary>
    Arm,
}

/// <summary>
/// Linux syscall numbers and flag values, selected for the running architecture.
/// </summary>
/// <remarks>
/// <para>
/// Some of these flags are architecture-dependent, which is the part that surprises people:
/// AArch64 inherited 32-bit ARM's values for <c>O_DIRECTORY</c> and <c>O_NOFOLLOW</c> rather
/// than the generic ones x86-64 uses. Getting them the wrong way round does not fail
/// loudly. <c>O_NOFOLLOW</c>'s x86-64 value is <c>O_LARGEFILE</c> on the ARM targets, so an
/// open intended to refuse a symlink would instead follow the link — a containment failure
/// that no test looking only at return codes would notice.
/// </para>
/// <para>
/// The syscall numbers split three ways. <c>statx</c> is 332 on x86-64, 291 on AArch64 and
/// 397 on 32-bit ARM, and a number from the wrong table is either unassigned or a different
/// call entirely. Every table is asserted by tests that run on every leg, through the
/// per-ABI lookups below, because there is no way to tell from a Linux-x64 build agent that
/// the ARM values were ever right.
/// </para>
/// <para>
/// <strong>Supported:</strong> x86-64, AArch64 and 32-bit ARM — the Linux architectures .NET
/// supports. On any other architecture <see cref="Abi"/> is <see cref="LinuxAbi.Unsupported"/>,
/// <see cref="LinuxPlatformOps"/> refuses to be constructed, and every architecture-dependent
/// value here throws rather than answer. That refusal is deliberately not made in a static
/// constructor: the architecture-independent values are read by tests on every platform, and
/// a type that failed to initialise would take them down too. To add an architecture, add a
/// member to <see cref="LinuxAbi"/>, a row to <see cref="AbiFor"/>, and a value to every
/// lookup that switches on it — the compiler does not insist on the last, the tests do.
/// </para>
/// </remarks>
internal static class LinuxConstants
{
    /// <summary>The table the running process uses.</summary>
    private static readonly LinuxAbi s_abi = AbiFor(RuntimeInformation.ProcessArchitecture, IntPtr.Size);

    /// <summary>
    /// What <see cref="LinuxPlatformOps"/> says when it refuses to start on an architecture
    /// without a table.
    /// </summary>
    internal const string UnsupportedArchitectureMessage =
        "cap-dotnet's Linux backend carries open flags and syscall numbers for x86-64, AArch64 " +
        "and 32-bit ARM only. Values from another architecture's table would not fail: the " +
        "flag that refuses to follow a symbolic link is a different flag elsewhere, and the " +
        "containment the library exists to provide rests on it. See " +
        "src/Cap.Primitives/Interop/Unix/LinuxConstants.cs to add this architecture.";

    /// <summary>The table the running process uses.</summary>
    internal static LinuxAbi Abi => s_abi;

    /// <summary>
    /// The table for a process of the given architecture and pointer size, or
    /// <see cref="LinuxAbi.Unsupported"/> when there is none.
    /// </summary>
    /// <remarks>
    /// The pointer size is part of the key because the architecture alone does not settle the
    /// C types the kernel interface is described in: a 64-bit table read by a process whose
    /// <c>long</c> is 32 bits would pass every argument at the wrong width.
    /// </remarks>
    internal static LinuxAbi AbiFor(Architecture architecture, int pointerSize) =>
        (architecture, pointerSize) switch
        {
            (Architecture.X64, 8) => LinuxAbi.X64,
            (Architecture.Arm64, 8) => LinuxAbi.Arm64,
            (Architecture.Arm, 4) => LinuxAbi.Arm,
            _ => LinuxAbi.Unsupported,
        };

    /// <summary>
    /// Whether this backend has flag values and syscall numbers for a process of the given
    /// architecture and pointer size.
    /// </summary>
    internal static bool IsSupportedArchitecture(Architecture architecture, int pointerSize) =>
        AbiFor(architecture, pointerSize) != LinuxAbi.Unsupported;

    /// <summary>
    /// True when the C library's default interfaces take a 32-bit <c>off_t</c> and
    /// <c>time_t</c>, so that file offsets and timestamps have to go to the kernel through
    /// the calls that take 64-bit ones.
    /// </summary>
    internal static bool HasNarrowCTypes => s_abi == LinuxAbi.Arm;

    private static PlatformNotSupportedException Unsupported() => new(UnsupportedArchitectureMessage);

    // --- Architecture-dependent open flags ------------------------------------------------

    /// <summary>Refuse the open unless the name is a directory.</summary>
    public static int O_DIRECTORY => DirectoryFlag(s_abi);

    /// <summary>Refuse the open if the final component is a symbolic link.</summary>
    public static int O_NOFOLLOW => NoFollowFlag(s_abi);

    /// <summary>
    /// Allow an open file to be larger than a 32-bit offset can reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A 64-bit kernel adds this to every open itself, so on the 64-bit targets it is never
    /// passed. A 32-bit kernel does not, and without it an open of a file past 2 GiB fails
    /// with an overflow — through <c>openat2</c> as well, which applies nothing on the
    /// caller's behalf. <see cref="LinuxNative"/> adds it to every open on 32-bit ARM that is
    /// not a traversal-only one, which the kernel refuses to combine with it.
    /// </para>
    /// <para>
    /// Architecture-dependent, and the reason <see cref="O_NOFOLLOW"/> matters so much: on
    /// the ARM targets this is the bit x86-64 uses to refuse a symbolic link.
    /// </para>
    /// </remarks>
    public static int O_LARGEFILE => LargeFileFlag(s_abi);

    /// <summary><see cref="O_DIRECTORY"/> in a given table.</summary>
    internal static int DirectoryFlag(LinuxAbi abi) => abi switch
    {
        LinuxAbi.X64 => 0x10000,
        LinuxAbi.Arm64 or LinuxAbi.Arm => 0x4000,
        _ => throw Unsupported(),
    };

    /// <summary><see cref="O_NOFOLLOW"/> in a given table.</summary>
    internal static int NoFollowFlag(LinuxAbi abi) => abi switch
    {
        LinuxAbi.X64 => 0x20000,
        LinuxAbi.Arm64 or LinuxAbi.Arm => 0x8000,
        _ => throw Unsupported(),
    };

    /// <summary>
    /// <c>O_DIRECT</c> in a given table. Never passed; it is here so the tests can show that
    /// no table's directory or no-follow bit means unbuffered IO in another.
    /// </summary>
    internal static int DirectFlag(LinuxAbi abi) => abi switch
    {
        LinuxAbi.X64 => 0x4000,
        LinuxAbi.Arm64 or LinuxAbi.Arm => 0x10000,
        _ => throw Unsupported(),
    };

    /// <summary><see cref="O_LARGEFILE"/> in a given table.</summary>
    internal static int LargeFileFlag(LinuxAbi abi) => abi switch
    {
        LinuxAbi.X64 => 0x8000,
        LinuxAbi.Arm64 or LinuxAbi.Arm => 0x20000,
        _ => throw Unsupported(),
    };

    /// <summary>
    /// Create a file with no name in any directory, taking its storage from the directory
    /// the open is made against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handle returned is the only reference to the file, and closing the last one
    /// returns the storage. Nothing can open it, replace it or plant a link where it sits,
    /// because it has no place in any directory for that to be aimed at.
    /// </para>
    /// <para>
    /// That holds only together with <see cref="O_EXCL"/>. On its own the flag still lets a
    /// holder of the descriptor link the file into a directory afterwards, giving it a name;
    /// with the exclusive flag the kernel refuses every such link, so the file never has one.
    /// </para>
    /// <para>
    /// Architecture-dependent, because the flag is the directory flag with one further bit
    /// set, and the directory flag is one of the two whose value AArch64 did not inherit
    /// from x86-64. Getting it wrong on one architecture would not fail: the kernel would
    /// see a combination of flags it does understand, and the open would produce something
    /// other than what was asked for.
    /// </para>
    /// <para>
    /// Not supported by every filesystem. A filesystem that has no implementation refuses
    /// the open rather than approximating it, which is the answer the caller needs — the
    /// whole value of the flag is that there is no name, so an approximation with a name
    /// would be a different thing entirely.
    /// </para>
    /// </remarks>
    public static int O_TMPFILE => 0x400000 | O_DIRECTORY;

    // --- Architecture-independent open flags ----------------------------------------------

    /// <summary>
    /// Open the object as a position in the tree rather than as something to read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The descriptor it produces carries no data access at all: it can be resolved against,
    /// asked what it is, and duplicated, and it cannot be read, written or enumerated. That
    /// is the whole authority a directory needs in order to be a place other names are
    /// resolved from, and asking for no more of it than that is the point.
    /// </para>
    /// <para>
    /// It is also the only way to express the permission the kernel itself uses when it walks
    /// a path. Traversing a directory needs execute permission, not read permission, so a
    /// directory with mode <c>0111</c> can be walked through by the kernel but cannot be
    /// opened for reading. A walk that opened intermediate directories readably would fail on
    /// such a tree where the kernel's own resolution succeeds — the same path yielding
    /// different answers depending on which backend was in use.
    /// </para>
    /// <para>
    /// It comes with a sharp edge. Combined with the flag that refuses to follow links, and
    /// <em>without</em> the flag demanding a directory, it stops resolving at a symbolic link
    /// and hands back a descriptor to the link itself rather than failing. Every directory
    /// open here passes both flags, which turns that case back into a refusal.
    /// </para>
    /// <para>
    /// Nor may it be combined with much: the kernel rejects it outright alongside any flag
    /// other than the close-on-exec, directory and no-follow ones. Adding a status flag to a
    /// traversal open is therefore not a harmless extra request, it is an invalid one.
    /// </para>
    /// </remarks>
    public const int O_PATH = 0x200000;

    public const int O_RDONLY = 0x0000;
    public const int O_WRONLY = 0x0001;
    public const int O_RDWR = 0x0002;

    /// <summary>Create the name if nothing holds it. Requires a mode argument.</summary>
    public const int O_CREAT = 0x40;

    /// <summary>
    /// With <see cref="O_CREAT"/>, refuse the open if anything at all holds the name.
    /// </summary>
    /// <remarks>
    /// Including a symbolic link, whose target is not consulted: the kernel reports the name
    /// as taken. That is the answer an exclusive create wants, because the name is what is
    /// being claimed and a link is something holding it.
    /// </remarks>
    public const int O_EXCL = 0x80;

    /// <summary>Discard the contents of a file that was already there.</summary>
    public const int O_TRUNC = 0x200;

    /// <summary>
    /// Move to the end of the file before every write, atomically with the write itself.
    /// </summary>
    /// <remarks>
    /// A property of the open description rather than of a call, which is why a write given
    /// an explicit offset still lands at the end on a handle opened this way. The kernel
    /// decides that, not this library, and pretending otherwise would be a promise that
    /// could not be kept.
    /// </remarks>
    public const int O_APPEND = 0x400;

    /// <summary>
    /// Do not return from a write until the data and the metadata needed to read it back
    /// have reached the storage device.
    /// </summary>
    /// <remarks>
    /// Two bits, not one: the data-synchronisation bit and the full-synchronisation bit
    /// together. The generic architecture defines the full form that way, and passing only
    /// the upper bit asks for something the kernel does not recognise as either.
    /// </remarks>
    public const int O_SYNC = 0x101000;

    /// <summary>
    /// Do not block waiting for the other end of a FIFO or device. Cleared again once the
    /// handle is open; it is here to stop an open from hanging, not to change how the file
    /// behaves afterwards.
    /// </summary>
    public const int O_NONBLOCK = 0x800;

    /// <summary>
    /// Close the descriptor across an <c>exec</c>. Not a nicety for a capability library: a
    /// descriptor that survives into a child process has handed that process the authority
    /// the handle carries, which is precisely the leak the whole design exists to prevent.
    /// </summary>
    public const int O_CLOEXEC = 0x80000;

    // --- setting times --------------------------------------------------------------------

    /// <summary>
    /// Stands in a time's nanoseconds field for "the moment the change is recorded", taken
    /// by the kernel.
    /// </summary>
    public const long UTIME_NOW = (1L << 30) - 1;

    /// <summary>Stands in a time's nanoseconds field for "leave this time alone".</summary>
    public const long UTIME_OMIT = (1L << 30) - 2;

    // --- *at() flags -----------------------------------------------------------------------

    /// <summary>Resolve relative to the process working directory.</summary>
    public const int AT_FDCWD = -100;

    /// <summary>Report on a symbolic link itself rather than on what it points at.</summary>
    public const int AT_SYMLINK_NOFOLLOW = 0x100;

    /// <summary>Operate on the directory descriptor itself when the name is empty.</summary>
    public const int AT_EMPTY_PATH = 0x1000;

    /// <summary>Do not trigger an automount at the final component.</summary>
    public const int AT_NO_AUTOMOUNT = 0x800;

    /// <summary>Remove a directory rather than a name of any other kind.</summary>
    public const int AT_REMOVEDIR = 0x200;

    // --- rename -----------------------------------------------------------------------------

    /// <summary>
    /// Fail rather than replace an entry already holding the destination name.
    /// </summary>
    /// <remarks>
    /// The whole reason the newer rename call is used at all. Without it the refusal would
    /// have to be a lookup followed by a rename, and between those two the destination can
    /// appear — so the check would pass and the rename would destroy what appeared. A
    /// filesystem that does not implement the flag reports an invalid argument, which is
    /// surfaced rather than answered by falling back to that race.
    /// </remarks>
    public const uint RENAME_NOREPLACE = 1;

    // --- mkdir ------------------------------------------------------------------------------

    /// <summary>
    /// The permissions a newly created directory is asked for, before the process umask is
    /// applied to them.
    /// </summary>
    /// <remarks>
    /// The value every directory-creating tool asks for. It is not the mode the directory
    /// ends up with: the kernel clears whatever the process umask names, so the result is
    /// the same as <c>mkdir</c> from a shell would produce in the same process. Asking for
    /// something narrower here would quietly make directories created through a capability
    /// differ from every other directory on the system, which is a surprise rather than a
    /// defence — the sandbox is a bound on what can be reached, not a substitute for the
    /// filesystem's own permissions.
    /// </remarks>
    public const uint DirectoryCreateMode = 0x1FF;

    /// <summary>
    /// The permissions a directory this library chose the location of is asked for, before
    /// the process umask is applied to them.
    /// </summary>
    /// <remarks>
    /// Read, write and search for the owning account and nothing for anybody else, which is
    /// what <c>mkdtemp</c> asks for. The reasoning that makes the wider mode right elsewhere
    /// runs the other way here: a caller who names a directory has chosen where it goes and
    /// should get what any other program creating it there would get, whereas a scratch
    /// directory is put in a location shared with every account on the machine without the
    /// caller naming anywhere at all. Closing it is therefore part of putting it there.
    /// </remarks>
    public const uint OwnerOnlyDirectoryCreateMode = 0x1C0;

    // --- open with creation ------------------------------------------------------------------

    /// <summary>
    /// The permissions a newly created file is asked for, before the process umask is
    /// applied to them.
    /// </summary>
    /// <remarks>
    /// Read and write for everybody, which is what every program that creates a file asks
    /// for and never what it gets: the umask clears whatever the process has been configured
    /// to clear, so a file created through a capability ends up with the same permissions as
    /// one created by any other means in the same process. Asking for less here would make
    /// these files quietly different from every other file on the system, which is a
    /// surprise rather than a defence — a capability bounds what can be reached and is not a
    /// substitute for the filesystem's own access control.
    /// </remarks>
    public const uint FileCreateMode = 0x1B6;

    /// <summary>
    /// The permissions a file with no name, or a named scratch file, is asked for.
    /// </summary>
    /// <remarks>
    /// Read and write for the owning account alone, which is what <c>mkstemp</c> asks for.
    /// For a file with no name the choice costs nothing: a file with no entry in any
    /// directory cannot be opened by anybody at all, so the mode is a statement about what it
    /// would be if it were ever given a name rather than about what it is now. A named
    /// scratch file has an entry, under a name this library drew rather than one the caller
    /// chose, and closing it to everybody else is part of making it.
    /// </remarks>
    public const uint OwnerOnlyFileCreateMode = 0x180;

    /// <summary>
    /// Reserve space behind the file without moving the end of it.
    /// </summary>
    /// <remarks>
    /// The difference between claiming room and writing zeroes into it. Without this the
    /// reservation extends the file, so a caller who asked for room in advance would get a
    /// file that already reports that many bytes of contents — which is not what was asked
    /// for and is not what the reservation does anywhere else.
    /// </remarks>
    public const int FALLOC_FL_KEEP_SIZE = 0x01;

    // --- fcntl ------------------------------------------------------------------------------

    public const int F_GETFL = 3;
    public const int F_SETFL = 4;

    /// <summary>Duplicate a descriptor with the close-on-exec flag already set.</summary>
    public const int F_DUPFD_CLOEXEC = 1030;

    // --- statx --------------------------------------------------------------------------------

    /// <summary>Ask only for the fields resolution uses: the type, the mode and the inode.</summary>
    public const uint STATX_TYPE = 0x0001;
    public const uint STATX_MODE = 0x0002;
    public const uint STATX_NLINK = 0x0004;
    public const uint STATX_UID = 0x0008;
    public const uint STATX_INO = 0x0100;

    public const uint STATX_ATIME = 0x0020;
    public const uint STATX_MTIME = 0x0040;
    public const uint STATX_CTIME = 0x0080;
    public const uint STATX_SIZE = 0x0200;

    /// <summary>
    /// Ask for the id of the mount the node was reached through. Linux 5.8 and later; an
    /// older kernel leaves it out of the reply's mask rather than failing the call.
    /// </summary>
    public const uint STATX_MNT_ID = 0x1000;

    /// <summary>
    /// Ask for the creation time. Set apart from the rest because it is the one field the
    /// kernel routinely declines: several filesystems do not record when a file was created,
    /// and the reply's own mask is the only way to find out whether this one did.
    /// </summary>
    public const uint STATX_BTIME = 0x0800;

    /// <summary>Do not force a network filesystem to revalidate; the cached answer is enough.</summary>
    public const int AT_STATX_DONT_SYNC = 0x4000;

    /// <summary>
    /// Answer as an ordinary stat would, revalidating against the server where there is one.
    /// </summary>
    /// <remarks>
    /// Zero, so passing it is the same as passing nothing — it is spelled out because the
    /// choice between this and <see cref="AT_STATX_DONT_SYNC"/> is a real one and an absent
    /// flag would read as an oversight. A length and a modification time are exactly the
    /// fields a cached answer gets wrong, and a caller asking for them has asked once and on
    /// purpose, so the round trip is what they are paying for.
    /// </remarks>
    public const int AT_STATX_SYNC_AS_STAT = 0x0000;

    // --- File type bits ------------------------------------------------------------------------

    public const ushort S_IFMT = 0xF000;
    public const ushort S_IFREG = 0x8000;
    public const ushort S_IFDIR = 0x4000;
    public const ushort S_IFLNK = 0xA000;

    // --- Filesystem type numbers ---------------------------------------------------------------

    /// <summary>
    /// The size of the buffer handed to <c>fstatfs</c>. The structure is 120 bytes on the
    /// 64-bit targets under both common C libraries, and the kernel's <c>statfs64</c> used on
    /// 32-bit ARM is <see cref="ArmStatfs64Bytes"/>; the margin is so that a layout that grows
    /// can never write past the buffer.
    /// </summary>
    public const int StatfsBufferBytes = 256;

    /// <summary>FAT in every variant the kernel mounts: <c>msdos</c>, <c>vfat</c>, <c>umsdos</c>.</summary>
    public const long MSDOS_SUPER_MAGIC = 0x4D44;

    /// <summary>exFAT.</summary>
    public const long EXFAT_SUPER_MAGIC = 0x2011BAB0;

    /// <summary>The <c>/proc</c> filesystem. Known on every system, which is what tests use it for.</summary>
    public const long PROC_SUPER_MAGIC = 0x9FA0;

    // --- Syscall numbers -------------------------------------------------------------------------

    // A syscall number is a C long, as is what the call returns, so these are machine words:
    // 32 bits on 32-bit ARM, where passing a 64-bit value would shift every argument after it.

    /// <summary>
    /// <c>openat2</c>. The same number on every supported target only because it was added
    /// long after the tables stopped growing independently.
    /// </summary>
    public const nint SYS_openat2 = 437;

    /// <summary><c>statx</c>: 332 on x86-64, 291 on AArch64, 397 on 32-bit ARM.</summary>
    public static nint SYS_statx => StatxNumber(s_abi);

    /// <summary><c>renameat2</c>: 316 on x86-64, 276 on AArch64, 382 on 32-bit ARM.</summary>
    public static nint SYS_renameat2 => Renameat2Number(s_abi);

    /// <summary><c>getdents64</c>: 217 on x86-64 and on 32-bit ARM, 61 on AArch64.</summary>
    public static nint SYS_getdents64 => Getdents64Number(s_abi);

    /// <summary><see cref="SYS_statx"/> in a given table.</summary>
    internal static nint StatxNumber(LinuxAbi abi) => abi switch
    {
        LinuxAbi.X64 => 332,
        LinuxAbi.Arm64 => 291,
        LinuxAbi.Arm => 397,
        _ => throw Unsupported(),
    };

    /// <summary><see cref="SYS_renameat2"/> in a given table.</summary>
    internal static nint Renameat2Number(LinuxAbi abi) => abi switch
    {
        LinuxAbi.X64 => 316,
        LinuxAbi.Arm64 => 276,
        LinuxAbi.Arm => 382,
        _ => throw Unsupported(),
    };

    /// <summary><see cref="SYS_getdents64"/> in a given table.</summary>
    internal static nint Getdents64Number(LinuxAbi abi) => abi switch
    {
        LinuxAbi.X64 => 217,
        LinuxAbi.Arm64 => 61,
        LinuxAbi.Arm => 217,
        _ => throw Unsupported(),
    };

    // --- 32-bit ARM only -------------------------------------------------------------------
    //
    // The C library's plain pwrite, fallocate, fstatfs, utimensat and futimens take a 32-bit
    // offset or time there, and the names of the 64-bit versions differ between glibc and
    // musl. The kernel's own calls are the same under both, so on that target these go to it
    // by number, as openat2 and statx already do everywhere.

    /// <summary><c>pwrite64</c> on 32-bit ARM. The offset is a register pair, aligned.</summary>
    public const nint SYS_arm_pwrite64 = 181;

    /// <summary><c>fallocate</c> on 32-bit ARM. Both 64-bit arguments are register pairs.</summary>
    public const nint SYS_arm_fallocate = 352;

    /// <summary>
    /// <c>fstatfs64</c> on 32-bit ARM, which reports a block count past 2^32 where the
    /// 32-bit call would fail with an overflow.
    /// </summary>
    public const nint SYS_arm_fstatfs64 = 267;

    /// <summary>
    /// <c>utimensat_time64</c> on 32-bit ARM (Linux 5.1 and later): the call that takes a
    /// timestamp as two 64-bit words, the layout <see cref="UnixTimespec"/> has.
    /// </summary>
    public const nint SYS_arm_utimensat_time64 = 412;

    /// <summary>
    /// The size of 32-bit ARM's <c>struct statfs64</c>, which the kernel checks against the
    /// size it is passed. Packed to four-byte alignment there, so it is 84 bytes rather than
    /// the 88 the fields would otherwise round to.
    /// </summary>
    public const nuint ArmStatfs64Bytes = 84;
}
