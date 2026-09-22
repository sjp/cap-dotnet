using System.Runtime.InteropServices;

namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// The macOS syscalls confined resolution is built from.
/// </summary>
/// <remarks>
/// The stat entry points are declared twice. Apple's Intel platform still resolves the
/// undecorated names to the original structure layout, with a 32-bit inode number, and
/// reaches the 64-bit layout only through decorated ones; Apple silicon has only the 64-bit
/// layout and only the undecorated names. Which pair is live is decided at run time, because
/// an import's entry point is fixed when the assembly is compiled and one build has to serve
/// both.
/// </remarks>
internal static unsafe partial class DarwinNative
{
    /// <summary>Opens a name relative to a directory descriptor.</summary>
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true)]
    internal static partial int OpenAt(int directoryFd, byte* path, int flags);

    /// <summary>Reads a symbolic link relative to a directory descriptor.</summary>
    [LibraryImport("libc", EntryPoint = "readlinkat", SetLastError = true)]
    internal static partial nint ReadLinkAt(int directoryFd, byte* path, byte* buffer, nuint bufferSize);

    /// <summary>Manipulates a descriptor.</summary>
    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    internal static partial int Fcntl(int fd, int command, int argument);

    /// <summary>Creates a directory relative to a directory descriptor.</summary>
    [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    internal static partial int MkdirAt(int directoryFd, byte* path, uint mode);

    /// <summary>
    /// Removes a name relative to a directory descriptor, never following a link in it.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    internal static partial int UnlinkAt(int directoryFd, byte* path, int flags);

    /// <summary>Moves a name from one directory descriptor to another.</summary>
    [LibraryImport("libc", EntryPoint = "renameat", SetLastError = true)]
    internal static partial int RenameAt(
        int oldDirectoryFd, byte* oldPath, int newDirectoryFd, byte* newPath);

    /// <summary>
    /// The same move, with the platform's own flags — among them the one that refuses to
    /// replace an existing destination.
    /// </summary>
    /// <remarks>
    /// This platform's answer to the problem Linux solves with a newer syscall number. The
    /// name carries the suffix Apple gives calls that are not part of any standard, which is
    /// the only spelling that exists: there is no portable call here that can make the
    /// refusal part of the rename.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "renameatx_np", SetLastError = true)]
    internal static partial int RenameAtX(
        int oldDirectoryFd, byte* oldPath, int newDirectoryFd, byte* newPath, uint flags);

    /// <summary>Creates a symbolic link relative to a directory descriptor.</summary>
    [LibraryImport("libc", EntryPoint = "symlinkat", SetLastError = true)]
    internal static partial int SymlinkAt(byte* target, int directoryFd, byte* linkPath);

    /// <summary>Creates a second name for an existing object, both relative to descriptors.</summary>
    /// <remarks>
    /// Called with no flags, so the existing name is used as written and a symbolic link is
    /// linked to as itself rather than as its target.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "linkat", SetLastError = true)]
    internal static partial int LinkAt(
        int oldDirectoryFd, byte* oldPath, int newDirectoryFd, byte* newPath, int flags);

    /// <summary>
    /// The same call, for the commands whose argument is a buffer rather than a number.
    /// </summary>
    /// <remarks>
    /// Declared separately because the C function is variadic and this platform passes a
    /// pointer and an integer differently; one declaration serving both would put the wrong
    /// kind of value in the register the kernel reads.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    internal static partial int FcntlBuffer(int fd, int command, byte* buffer);

    /// <summary>Reports on a name relative to a directory descriptor. Apple silicon spelling.</summary>
    [LibraryImport("libc", EntryPoint = "fstatat", SetLastError = true)]
    private static partial int FStatAtPlain(int directoryFd, byte* path, DarwinStat* result, int flags);

    /// <summary>Reports on a name relative to a directory descriptor. Intel spelling.</summary>
    [LibraryImport("libc", EntryPoint = "fstatat$INODE64", SetLastError = true)]
    private static partial int FStatAtInode64(int directoryFd, byte* path, DarwinStat* result, int flags);

    /// <summary>Reports on an open descriptor. Apple silicon spelling.</summary>
    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FStatPlain(int fd, DarwinStat* result);

    /// <summary>Reports on an open descriptor. Intel spelling.</summary>
    [LibraryImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static partial int FStatInode64(int fd, DarwinStat* result);

    /// <summary>True when the undecorated stat symbols already mean the 64-bit-inode layout.</summary>
    private static readonly bool UsesPlainStatSymbols =
        RuntimeInformation.ProcessArchitecture != Architecture.X64;

    /// <summary>Reports on a name relative to a directory descriptor.</summary>
    internal static int FStatAt(int directoryFd, byte* path, DarwinStat* result, int flags) =>
        UsesPlainStatSymbols
            ? FStatAtPlain(directoryFd, path, result, flags)
            : FStatAtInode64(directoryFd, path, result, flags);

    /// <summary>Reports on an open descriptor.</summary>
    internal static int FStat(int fd, DarwinStat* result) =>
        UsesPlainStatSymbols ? FStatPlain(fd, result) : FStatInode64(fd, result);
}
