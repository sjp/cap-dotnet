namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// macOS flag values.
/// </summary>
/// <remarks>
/// <para>
/// Every value here differs from its Linux namesake, and several differ in a way that would
/// not announce itself. <c>O_DIRECTORY</c> is <c>0x100000</c> here and <c>0x10000</c> on
/// x86-64 Linux; <c>O_CLOEXEC</c> is <c>0x1000000</c> here and <c>0x80000</c> there; the
/// value that means "do not follow the final link" here is a flag about data alignment
/// there. Borrowing a constant across the two platforms produces an open that succeeds and
/// does something other than what was asked.
/// </para>
/// <para>
/// Unlike Linux, these do not vary by architecture: Apple's 64-bit platforms share one set.
/// </para>
/// </remarks>
internal static class DarwinConstants
{
    public const int O_RDONLY = 0x0000;
    public const int O_WRONLY = 0x0001;
    public const int O_RDWR = 0x0002;

    /// <summary>Do not block waiting for the other end of a FIFO or device.</summary>
    public const int O_NONBLOCK = 0x0004;

    /// <summary>
    /// Move to the end of the file before every write, atomically with the write itself.
    /// </summary>
    /// <remarks>
    /// A property of the open description rather than of a call, so a write given an explicit
    /// offset still lands at the end on a handle opened this way. The kernel decides that,
    /// not this library.
    /// </remarks>
    public const int O_APPEND = 0x0008;

    /// <summary>
    /// Do not return from a write until the data has reached the storage device.
    /// </summary>
    /// <remarks>
    /// Spelled here as the older name for full synchronisation. The value is not the Linux
    /// one, and the Linux value is this platform's "keep the descriptor only while the file
    /// is being watched" flag, so borrowing it would produce an open that succeeds and does
    /// something else entirely.
    /// </remarks>
    public const int O_SYNC = 0x0080;

    /// <summary>Create the name if nothing holds it. Requires a mode argument.</summary>
    public const int O_CREAT = 0x0200;

    /// <summary>Discard the contents of a file that was already there.</summary>
    public const int O_TRUNC = 0x0400;

    /// <summary>
    /// With <see cref="O_CREAT"/>, refuse the open if anything at all holds the name,
    /// a symbolic link included and without consulting what it points at.
    /// </summary>
    public const int O_EXCL = 0x0800;

    /// <summary>Refuse the open if the final component is a symbolic link.</summary>
    public const int O_NOFOLLOW = 0x0100;

    /// <summary>Refuse the open unless the name is a directory.</summary>
    public const int O_DIRECTORY = 0x100000;

    /// <summary>Close the descriptor across an <c>exec</c>, so it cannot be inherited.</summary>
    public const int O_CLOEXEC = 0x1000000;

    /// <summary>Resolve relative to the process working directory. Note that this is not the Linux value.</summary>
    public const int AT_FDCWD = -2;

    /// <summary>Report on a symbolic link itself rather than on what it points at.</summary>
    public const int AT_SYMLINK_NOFOLLOW = 0x0020;

    /// <summary>Remove a directory rather than a name of any other kind. Not the Linux value.</summary>
    public const int AT_REMOVEDIR = 0x0080;

    /// <summary>
    /// Fail rather than replace an entry already holding the destination name.
    /// </summary>
    /// <remarks>
    /// Makes the refusal part of the rename, so there is no window between checking that the
    /// destination is free and taking it. A lookup followed by a plain rename would have one,
    /// and something appearing in that window would be destroyed by the very call that was
    /// told not to destroy anything.
    /// </remarks>
    public const uint RENAME_EXCL = 0x0004;

    /// <summary>
    /// The permissions a newly created directory is asked for, before the process umask is
    /// applied. The same request every directory-creating tool on the system makes.
    /// </summary>
    public const uint DirectoryCreateMode = 0x1FF;

    /// <summary>
    /// The permissions a directory this library chose the location of is asked for, before
    /// the process umask is applied.
    /// </summary>
    /// <remarks>
    /// Read, write and search for the owning account alone, which is what <c>mkdtemp</c>
    /// asks for. A caller who names a directory should get what any other program creating
    /// it there would get; a scratch directory is put in a location shared with every
    /// account on the machine without the caller naming anywhere, so closing it to everybody
    /// else is part of putting it there.
    /// </remarks>
    public const uint OwnerOnlyDirectoryCreateMode = 0x1C0;

    /// <summary>
    /// The permissions a newly created file is asked for, before the process umask is
    /// applied. The same request every file-creating program on the system makes, so a file
    /// created through a capability is not quietly different from any other.
    /// </summary>
    public const uint FileCreateMode = 0x1B6;

    public const int F_GETFL = 3;
    public const int F_SETFL = 4;

    /// <summary>Duplicate a descriptor with the close-on-exec flag already set.</summary>
    public const int F_DUPFD_CLOEXEC = 67;

    /// <summary>
    /// Ask a descriptor what path it is reachable by. The buffer must hold
    /// <see cref="MaxPathBytes"/> bytes whatever the answer turns out to be.
    /// </summary>
    public const int F_GETPATH = 50;

    /// <summary>
    /// Reserve space for a file. This platform's stand-in for the portable reservation call,
    /// which it does not have.
    /// </summary>
    /// <remarks>
    /// It reserves without extending: the file's length is unchanged and only the space
    /// behind it is claimed, so a reservation has to be followed by a truncation to the
    /// requested length for the result to mean what the caller asked for.
    /// </remarks>
    public const int F_PREALLOCATE = 42;

    /// <summary>Reserve the space contiguously if the filesystem can.</summary>
    public const uint F_ALLOCATECONTIG = 0x0002;

    /// <summary>Reserve all of it or none of it, rather than as much as happens to fit.</summary>
    public const uint F_ALLOCATEALL = 0x0004;

    /// <summary>Measure the reservation from the end of the file.</summary>
    public const int F_PEOFPOSMODE = 3;

    /// <summary>
    /// The size this platform requires of the buffer handed to <see cref="F_GETPATH"/>.
    /// </summary>
    /// <remarks>
    /// Not a guess at how long the answer will be: the call writes into the buffer without
    /// being told its size, so a smaller one is a buffer overrun rather than a truncated
    /// reply. It is the platform's own <c>MAXPATHLEN</c>.
    /// </remarks>
    public const int MaxPathBytes = 1024;

    public const ushort S_IFMT = 0xF000;
    public const ushort S_IFREG = 0x8000;
    public const ushort S_IFDIR = 0x4000;
    public const ushort S_IFLNK = 0xA000;
}
