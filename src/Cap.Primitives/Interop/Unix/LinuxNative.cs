using System.Runtime.InteropServices;

namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// The Linux syscalls confined resolution is built from.
/// </summary>
/// <remarks>
/// <para>
/// Every import is source-generated rather than marshalled at runtime, so the whole layer
/// survives ahead-of-time compilation and trimming, and so there is no hidden marshalling
/// step between the arguments written here and the ones the kernel sees. Paths are passed as
/// raw byte pointers for the same reason: a Unix filename is a byte string, and letting a
/// marshaller re-encode it would mean opening a name different from the one the caller
/// holds.
/// </para>
/// <para>
/// Two of these go through <c>syscall</c> by number instead of a named C function, because
/// the C library exposes no wrapper for them that can be relied on — <c>openat2</c> has none
/// at all, and a <c>statx</c> wrapper is only present in newer library versions. Calling by
/// number sidesteps the question of which C library the process was linked against.
/// </para>
/// <para>
/// On 32-bit ARM the C library's default interfaces take a 32-bit file offset and a 32-bit
/// time, and the names of their 64-bit counterparts differ between glibc and musl. The calls
/// that carry an offset, a timestamp or a filesystem size therefore go to the kernel by
/// number on that target, and every open that is not a traversal-only one asks for
/// <see cref="LinuxConstants.O_LARGEFILE"/>, which a 64-bit kernel adds by itself. The
/// wrappers below make that choice, so callers write the same code for every target.
/// </para>
/// </remarks>
internal static unsafe partial class LinuxNative
{
    /// <summary>Closes a descriptor.</summary>
    /// <remarks>
    /// Never retried. A close interrupted by a signal has still released the number, and a
    /// second call would close whatever has since been handed it.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int fd);

    /// <summary>Opens a name relative to a directory descriptor.</summary>
    internal static int OpenAt(int directoryFd, byte* path, int flags) =>
        OpenAtImport(directoryFd, path, WithLargeFile(flags));

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static partial int OpenAtImport(int directoryFd, byte* path, int flags);

    /// <summary>
    /// Opens a name relative to a directory descriptor, creating it with
    /// <paramref name="mode"/> if the flags ask for creation.
    /// </summary>
    /// <remarks>
    /// A second import of the same entry point rather than an optional argument, because the
    /// C function is variadic: the mode is read off the call stack only when the flags say
    /// creation was asked for, and an import that always passed one would be describing a
    /// different function. Calling the three-argument form with a creating flag set is
    /// therefore not merely untidy, it hands the kernel whatever happened to be in the
    /// register the mode is read from.
    /// </remarks>
    internal static int OpenAtWithMode(int directoryFd, byte* path, int flags, uint mode) =>
        OpenAtWithModeImport(directoryFd, path, WithLargeFile(flags), mode);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static partial int OpenAtWithModeImport(int directoryFd, byte* path, int flags, uint mode);

    /// <summary>
    /// Adds <see cref="LinuxConstants.O_LARGEFILE"/> where the kernel will not add it itself.
    /// </summary>
    /// <remarks>
    /// Only on 32-bit ARM, and never to a traversal-only open: the kernel refuses to combine
    /// that with any flag beyond the close-on-exec, directory and no-follow ones, and a
    /// traversal-only descriptor has no size for the flag to be about.
    /// </remarks>
    private static int WithLargeFile(int flags) =>
        LinuxConstants.HasNarrowCTypes && (flags & LinuxConstants.O_PATH) == 0
            ? flags | LinuxConstants.O_LARGEFILE
            : flags;

    /// <summary>
    /// Reserves space for a file, so that a later write cannot fail for want of room.
    /// </summary>
    /// <remarks>
    /// This platform's own call rather than the portable one, because only this one can
    /// reserve without also extending the file. The portable call leaves a file that reports
    /// the reserved length as its contents, which is a different thing from an empty file
    /// with room behind it — and the second is what every other platform's reservation
    /// produces.
    /// </remarks>
    internal static int Fallocate(int fd, int mode, long offset, long length) =>
        LinuxConstants.HasNarrowCTypes
            ? (int)ArmSyscall(
                LinuxConstants.SYS_arm_fallocate,
                fd,
                mode,
                Low(offset),
                High(offset),
                Low(length),
                High(length))
            : FallocateImport(fd, mode, offset, length);

    [LibraryImport("libc", EntryPoint = "fallocate", SetLastError = true)]
    private static partial int FallocateImport(int fd, int mode, long offset, long length);

    /// <summary>Reads a symbolic link relative to a directory descriptor.</summary>
    /// <returns>The number of bytes written, which is <em>not</em> null-terminated.</returns>
    [LibraryImport("libc", EntryPoint = "readlinkat", SetLastError = true)]
    internal static partial nint ReadLinkAt(int directoryFd, byte* path, byte* buffer, nuint bufferSize);

    /// <summary>
    /// Commits everything the kernel is holding for an open object to the storage it lives
    /// on.
    /// </summary>
    /// <remarks>
    /// Accepts a directory descriptor as well as a file one, which is the reason it is here:
    /// committing a directory is how the name that reaches a file is made to survive a power
    /// loss, and there is no other call that does it.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    internal static partial int FSync(int fd);

    /// <summary>Sets the permission bits of an open object.</summary>
    /// <remarks>
    /// By descriptor rather than by name, so nothing is resolved a second time and there is
    /// no name for a link to be planted at between deciding what to set and setting it.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    internal static partial int FChmod(int fd, uint mode);

    /// <summary>
    /// Sets the last-access and last-write times of a name relative to a directory
    /// descriptor, or of the descriptor itself when the name is empty and the flags say so.
    /// </summary>
    /// <remarks>
    /// <paramref name="times"/> points at two entries, access first. A symbolic link at the
    /// name is followed unless <c>AT_SYMLINK_NOFOLLOW</c> is passed.
    /// </remarks>
    internal static int UtimensAt(int directoryFd, byte* path, UnixTimespec* times, int flags) =>
        LinuxConstants.HasNarrowCTypes
            ? (int)ArmSyscall(
                LinuxConstants.SYS_arm_utimensat_time64, directoryFd, (nint)path, (nint)times, flags, 0, 0)
            : UtimensAtImport(directoryFd, path, times, flags);

    [LibraryImport("libc", EntryPoint = "utimensat", SetLastError = true)]
    private static partial int UtimensAtImport(int directoryFd, byte* path, UnixTimespec* times, int flags);

    /// <summary>Sets the last-access and last-write times of an open object.</summary>
    /// <remarks>
    /// By descriptor rather than by name, for the reason <see cref="FChmod"/> is.
    /// <paramref name="times"/> points at two entries, access first.
    /// </remarks>
    /// <remarks>
    /// On 32-bit ARM this is the time64 form of the call above with no name, which is how the
    /// C library implements it everywhere.
    /// </remarks>
    internal static int FUtimens(int fd, UnixTimespec* times) =>
        LinuxConstants.HasNarrowCTypes
            ? (int)ArmSyscall(LinuxConstants.SYS_arm_utimensat_time64, fd, 0, (nint)times, 0, 0, 0)
            : FUtimensImport(fd, times);

    [LibraryImport("libc", EntryPoint = "futimens", SetLastError = true)]
    private static partial int FUtimensImport(int fd, UnixTimespec* times);

    /// <summary>
    /// Manipulates a descriptor. Used to duplicate one, to clear a status flag, and to read
    /// and change whether a file appends.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    internal static partial int Fcntl(int fd, int command, int argument);

    /// <summary>
    /// Writes at a given offset without moving the descriptor's position. On a descriptor
    /// that appends, the kernel writes at the end of the file instead.
    /// </summary>
    /// <remarks>
    /// On 32-bit ARM the offset is a register pair that has to start on an even register, so
    /// the argument before it is a slot of padding.
    /// </remarks>
    internal static nint PWrite(int fd, byte* buffer, nuint count, long offset) =>
        LinuxConstants.HasNarrowCTypes
            ? ArmSyscall(
                LinuxConstants.SYS_arm_pwrite64, fd, (nint)buffer, (nint)count, 0, Low(offset), High(offset))
            : PWriteImport(fd, buffer, count, offset);

    [LibraryImport("libc", EntryPoint = "pwrite", SetLastError = true)]
    private static partial nint PWriteImport(int fd, byte* buffer, nuint count, long offset);

    /// <summary>Creates a directory relative to a directory descriptor.</summary>
    [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    internal static partial int MkdirAt(int directoryFd, byte* path, uint mode);

    /// <summary>
    /// Removes a name relative to a directory descriptor.
    /// </summary>
    /// <remarks>
    /// One entry point for both kinds of removal, selected by
    /// <see cref="LinuxConstants.AT_REMOVEDIR"/>. It never follows a symbolic link in the
    /// name it is given: removing a name removes the name.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    internal static partial int UnlinkAt(int directoryFd, byte* path, int flags);

    /// <summary>Moves a name from one directory descriptor to another.</summary>
    [LibraryImport("libc", EntryPoint = "renameat", SetLastError = true)]
    internal static partial int RenameAt(
        int oldDirectoryFd, byte* oldPath, int newDirectoryFd, byte* newPath);

    /// <summary>
    /// <c>renameat2</c>, by syscall number, for the flag that refuses to replace an existing
    /// destination.
    /// </summary>
    /// <remarks>
    /// By number for the same reason <see cref="OpenAt2"/> is: the C library grew a wrapper
    /// for it only in version 2.28, and which library the process was linked against is not
    /// something this layer should have to know. <paramref name="number"/> must be
    /// <see cref="LinuxConstants.SYS_renameat2"/>.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    internal static partial nint RenameAt2(
        nint number, int oldDirectoryFd, byte* oldPath, int newDirectoryFd, byte* newPath, uint flags);

    /// <summary>Creates a symbolic link relative to a directory descriptor.</summary>
    /// <remarks>
    /// The target is stored as the bytes given and is not resolved, so it may name something
    /// that does not exist, or never will.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "symlinkat", SetLastError = true)]
    internal static partial int SymlinkAt(byte* target, int directoryFd, byte* linkPath);

    /// <summary>
    /// Creates a second name for an existing object, both relative to directory descriptors.
    /// </summary>
    /// <remarks>
    /// Called with no flags, which on this platform means the existing name is used as
    /// written: a symbolic link is linked to as itself rather than as whatever it points at.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "linkat", SetLastError = true)]
    internal static partial int LinkAt(
        int oldDirectoryFd, byte* oldPath, int newDirectoryFd, byte* newPath, int flags);

    /// <summary>
    /// <c>openat2</c>, by syscall number.
    /// </summary>
    /// <remarks>
    /// <paramref name="number"/> must be <see cref="LinuxConstants.SYS_openat2"/>, and
    /// <paramref name="size"/> the size of the structure <paramref name="how"/> points at.
    /// The kernel validates that size, and a value it does not recognise is rejected as an
    /// invalid argument — which is indistinguishable, from the outside, from the syscall not
    /// existing at all.
    /// </remarks>
    internal static nint OpenAt2(nint number, int directoryFd, byte* path, OpenHow* how, nuint size)
    {
        if (!LinuxConstants.HasNarrowCTypes)
        {
            return OpenAt2Import(number, directoryFd, path, how, size);
        }

        OpenHow request = *how;
        request.Flags = (ulong)(uint)WithLargeFile((int)request.Flags);
        return OpenAt2Import(number, directoryFd, path, &request, size);
    }

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial nint OpenAt2Import(nint number, int directoryFd, byte* path, OpenHow* how, nuint size);

    /// <summary>
    /// Reads a run of directory entries into a buffer, by syscall number.
    /// </summary>
    /// <remarks>
    /// <paramref name="number"/> must be <see cref="LinuxConstants.SYS_getdents64"/>. By
    /// number because the C library grew a wrapper for this only recently, and which library
    /// a process is linked against is not something this layer wants to depend on.
    /// </remarks>
    /// <returns>
    /// The number of bytes written, which is zero at the end of the directory and never a
    /// partial record.
    /// </returns>
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    internal static partial nint GetDents64(nint number, int fd, byte* buffer, nuint count);

    /// <summary>
    /// Describes the filesystem a descriptor is on, into a buffer of at least
    /// <see cref="LinuxConstants.StatfsBufferBytes"/> bytes.
    /// </summary>
    /// <remarks>
    /// Only the first field, the filesystem's type number, is read, and it is a machine word
    /// on every target. The structure is not declared: its tail differs between C libraries
    /// and nothing past the first field is wanted, so a buffer comfortably larger than every
    /// layout is passed instead of a declaration that would have to match each one. On
    /// 32-bit ARM the kernel's <c>fstatfs64</c> is called instead, whose structure starts
    /// with the same word and which does not fail on a filesystem with more than 2^32 blocks.
    /// </remarks>
    internal static int Fstatfs(int fd, byte* buffer) =>
        LinuxConstants.HasNarrowCTypes
            ? (int)ArmSyscall(
                LinuxConstants.SYS_arm_fstatfs64, fd, (nint)LinuxConstants.ArmStatfs64Bytes, (nint)buffer, 0, 0, 0)
            : FstatfsImport(fd, buffer);

    [LibraryImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static partial int FstatfsImport(int fd, byte* buffer);

    /// <summary><c>statx</c>, by syscall number.</summary>
    /// <remarks><paramref name="number"/> must be <see cref="LinuxConstants.SYS_statx"/>.</remarks>
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    internal static partial nint Statx(nint number, int directoryFd, byte* path, int flags, uint mask, StatxBuffer* result);

    /// <summary>The account this process acts as when the filesystem checks permissions.</summary>
    /// <remarks>Cannot fail, so there is no error to collect.</remarks>
    [LibraryImport("libc", EntryPoint = "geteuid")]
    internal static partial uint GetEffectiveUserId();

    /// <summary>
    /// A syscall by number with six word-sized arguments, for the 32-bit ARM routes above.
    /// </summary>
    /// <remarks>
    /// Every argument is a machine word so that each lands in exactly one register, which is
    /// what the kernel's calling convention on that target counts in. A 64-bit value is two
    /// of them, low word first (<see cref="Low"/>, <see cref="High"/>); unused trailing
    /// arguments are zero and ignored by the kernel.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial nint ArmSyscall(nint number, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6);

    /// <summary>The low 32 bits of a 64-bit argument, as a register.</summary>
    internal static nint Low(long value) => (nint)(int)(uint)(ulong)value;

    /// <summary>The high 32 bits of a 64-bit argument, as a register.</summary>
    internal static nint High(long value) => (nint)(int)(uint)((ulong)value >> 32);
}
