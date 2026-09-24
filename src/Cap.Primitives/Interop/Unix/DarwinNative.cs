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

    /// <summary>
    /// Opens a name relative to a directory descriptor, creating it with
    /// <paramref name="mode"/> if the flags ask for creation.
    /// </summary>
    /// <remarks>
    /// A separate import of the same entry point, because the C function is variadic and
    /// reads the mode off the call stack only when the flags say creation was asked for. An
    /// import that always passed one would be describing a different function, and the
    /// three-argument form used with a creating flag hands the kernel whatever happened to
    /// be in the register the mode is read from.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true)]
    internal static partial int OpenAtWithMode(int directoryFd, byte* path, int flags, uint mode);

    /// <summary>Manipulates a descriptor with a structure argument, for the reservation call.</summary>
    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    internal static partial int FcntlStore(int fd, int command, FileStore* store);

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
    [LibraryImport("libc", EntryPoint = "utimensat", SetLastError = true)]
    internal static partial int UtimensAt(int directoryFd, byte* path, UnixTimespec* times, int flags);

    /// <summary>Sets the last-access and last-write times of an open object.</summary>
    /// <remarks>
    /// By descriptor rather than by name, for the reason <see cref="FChmod"/> is.
    /// <paramref name="times"/> points at two entries, access first.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "futimens", SetLastError = true)]
    internal static partial int FUtimens(int fd, UnixTimespec* times);

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

    /// <summary>
    /// Closes a directory stream, and the descriptor it was built on.
    /// </summary>
    /// <remarks>
    /// Undecorated on both architectures: it takes the stream and nothing whose shape
    /// depends on the size of an inode number.
    /// </remarks>
    [LibraryImport("libc", EntryPoint = "closedir", SetLastError = true)]
    internal static partial int CloseDir(nint stream);

    /// <summary>Builds a directory stream on an open descriptor. Apple silicon spelling.</summary>
    [LibraryImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static partial nint FdOpenDirPlain(int fd);

    /// <summary>Builds a directory stream on an open descriptor. Intel spelling.</summary>
    [LibraryImport("libc", EntryPoint = "fdopendir$INODE64", SetLastError = true)]
    private static partial nint FdOpenDirInode64(int fd);

    /// <summary>Reads one entry into a caller-supplied record. Apple silicon spelling.</summary>
    [LibraryImport("libc", EntryPoint = "readdir_r", SetLastError = true)]
    private static partial int ReadDirRPlain(
        nint stream, DarwinDirectoryEntry* entry, DarwinDirectoryEntry** result);

    /// <summary>Reads one entry into a caller-supplied record. Intel spelling.</summary>
    [LibraryImport("libc", EntryPoint = "readdir_r$INODE64", SetLastError = true)]
    private static partial int ReadDirRInode64(
        nint stream, DarwinDirectoryEntry* entry, DarwinDirectoryEntry** result);

    /// <summary>
    /// Builds a directory stream on an open descriptor, which the stream then owns.
    /// </summary>
    /// <remarks>
    /// On failure the descriptor is <em>not</em> taken over, so the caller still has to close
    /// it. That asymmetry is the one thing about this call worth stating twice.
    /// </remarks>
    internal static nint FdOpenDir(int fd) =>
        UsesPlainStatSymbols ? FdOpenDirPlain(fd) : FdOpenDirInode64(fd);

    /// <summary>
    /// Reads one directory entry into <paramref name="entry"/>.
    /// </summary>
    /// <returns>
    /// Zero on success, where <paramref name="result"/> is set to
    /// <paramref name="entry"/> or to null at the end of the directory; otherwise the error
    /// number, which this call returns rather than leaving in <c>errno</c>.
    /// </returns>
    internal static int ReadDirR(
        nint stream, DarwinDirectoryEntry* entry, DarwinDirectoryEntry** result) =>
        UsesPlainStatSymbols
            ? ReadDirRPlain(stream, entry, result)
            : ReadDirRInode64(stream, entry, result);

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

    /// <summary>The account this process acts as when the filesystem checks permissions.</summary>
    /// <remarks>Cannot fail, so there is no error to collect.</remarks>
    [LibraryImport("libc", EntryPoint = "geteuid")]
    internal static partial uint GetEffectiveUserId();
}

/// <summary>
/// The <c>fstore</c> argument to this platform's space-reservation request.
/// </summary>
/// <remarks>
/// Laid out exactly as the header declares it: two 32-bit fields and then three 64-bit
/// ones. Getting the layout wrong here does not fail loudly — the kernel reads whichever
/// bytes land where it expects the length, so a shifted field asks for a reservation of an
/// arbitrary size rather than reporting that the request was malformed.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct FileStore
{
    /// <summary>Whether the space must be contiguous, and whether a partial result is acceptable.</summary>
    public uint Flags;

    /// <summary>What the offset is measured from.</summary>
    public int PositionMode;

    /// <summary>Where the reservation starts, in the mode's own terms.</summary>
    public long Offset;

    /// <summary>How much to reserve.</summary>
    public long Length;

    /// <summary>How much was reserved, filled in by the kernel.</summary>
    public long BytesAllocated;
}
