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
/// </remarks>
internal static unsafe partial class LinuxNative
{
    /// <summary>Opens a name relative to a directory descriptor.</summary>
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true)]
    internal static partial int OpenAt(int directoryFd, byte* path, int flags);

    /// <summary>Reads a symbolic link relative to a directory descriptor.</summary>
    /// <returns>The number of bytes written, which is <em>not</em> null-terminated.</returns>
    [LibraryImport("libc", EntryPoint = "readlinkat", SetLastError = true)]
    internal static partial nint ReadLinkAt(int directoryFd, byte* path, byte* buffer, nuint bufferSize);

    /// <summary>Manipulates a descriptor. Used to duplicate one and to clear a status flag.</summary>
    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    internal static partial int Fcntl(int fd, int command, int argument);

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
    internal static partial long RenameAt2(
        long number, int oldDirectoryFd, byte* oldPath, int newDirectoryFd, byte* newPath, uint flags);

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
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    internal static partial long OpenAt2(long number, int directoryFd, byte* path, OpenHow* how, nuint size);

    /// <summary><c>statx</c>, by syscall number.</summary>
    /// <remarks><paramref name="number"/> must be <see cref="LinuxConstants.SYS_statx"/>.</remarks>
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    internal static partial long Statx(long number, int directoryFd, byte* path, int flags, uint mask, StatxBuffer* result);
}
