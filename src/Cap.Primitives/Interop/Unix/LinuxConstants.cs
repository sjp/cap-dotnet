using System.Runtime.InteropServices;

namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// Linux syscall numbers and flag values, selected for the running architecture.
/// </summary>
/// <remarks>
/// <para>
/// Three of these flags are architecture-dependent, which is the part that surprises people:
/// AArch64 inherited 32-bit ARM's values for <c>O_DIRECTORY</c> and <c>O_NOFOLLOW</c> rather
/// than the generic ones x86-64 uses. Getting them the wrong way round does not fail
/// loudly. <c>O_NOFOLLOW</c>'s x86-64 value is <c>O_DIRECT</c> on AArch64, so an open
/// intended to refuse a symlink would instead ask for unbuffered IO and follow the link —
/// a containment failure that no test looking only at return codes would notice.
/// </para>
/// <para>
/// The syscall numbers split the same way. <c>statx</c> is 332 on x86-64 and 291 on
/// AArch64; calling 332 on AArch64 lands on <c>fsconfig</c>. Both are asserted by tests that
/// run on both architectures, because there is no way to tell from a Linux-x64 build agent
/// that the AArch64 values were ever right.
/// </para>
/// </remarks>
internal static class LinuxConstants
{
    /// <summary>True when the process is running on 64-bit ARM.</summary>
    private static readonly bool IsArm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    // --- Architecture-dependent open flags ------------------------------------------------

    /// <summary>Refuse the open unless the name is a directory.</summary>
    public static int O_DIRECTORY => IsArm64 ? 0x4000 : 0x10000;

    /// <summary>Refuse the open if the final component is a symbolic link.</summary>
    public static int O_NOFOLLOW => IsArm64 ? 0x8000 : 0x20000;

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
    /// The permissions a file with no name is asked for.
    /// </summary>
    /// <remarks>
    /// Owner-only, and the choice costs nothing: a file with no entry in any directory
    /// cannot be opened by anybody at all, so the mode is a statement about what it would be
    /// if it were ever given a name rather than about what it is now.
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
    public const uint STATX_UID = 0x0008;
    public const uint STATX_INO = 0x0100;

    public const uint STATX_ATIME = 0x0020;
    public const uint STATX_MTIME = 0x0040;
    public const uint STATX_SIZE = 0x0200;

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

    // --- Syscall numbers -------------------------------------------------------------------------

    /// <summary>
    /// <c>openat2</c>. The same number on x86-64 and AArch64 only because it was added long
    /// after the two tables stopped growing independently.
    /// </summary>
    public const long SYS_openat2 = 437;

    /// <summary><c>statx</c>: 332 on x86-64, 291 on AArch64.</summary>
    public static long SYS_statx => IsArm64 ? 291 : 332;

    /// <summary><c>renameat2</c>: 316 on x86-64, 276 on AArch64.</summary>
    public static long SYS_renameat2 => IsArm64 ? 276 : 316;

    /// <summary><c>getdents64</c>: 217 on x86-64, 61 on AArch64.</summary>
    public static long SYS_getdents64 => IsArm64 ? 61 : 217;
}
