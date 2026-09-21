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
