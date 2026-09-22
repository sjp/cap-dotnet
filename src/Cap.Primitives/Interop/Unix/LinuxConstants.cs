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

    // --- fcntl ------------------------------------------------------------------------------

    public const int F_GETFL = 3;
    public const int F_SETFL = 4;

    /// <summary>Duplicate a descriptor with the close-on-exec flag already set.</summary>
    public const int F_DUPFD_CLOEXEC = 1030;

    // --- statx --------------------------------------------------------------------------------

    /// <summary>Ask only for the fields resolution uses: the type, the mode and the inode.</summary>
    public const uint STATX_TYPE = 0x0001;
    public const uint STATX_MODE = 0x0002;
    public const uint STATX_INO = 0x0100;

    /// <summary>Do not force a network filesystem to revalidate; the cached answer is enough.</summary>
    public const int AT_STATX_DONT_SYNC = 0x4000;

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
}
