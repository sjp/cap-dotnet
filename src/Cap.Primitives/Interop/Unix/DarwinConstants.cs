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
