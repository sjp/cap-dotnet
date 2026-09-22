namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// macOS <c>errno</c> values above the shared POSIX range, and how this library reads them.
/// </summary>
/// <remarks>
/// Every number here differs from the Linux value for the same name. That is the whole
/// reason the two tables are separate files rather than one with a runtime branch: a
/// mistake that swaps them is invisible on the platform the mistake was not made for, and
/// would turn a link loop into a name-too-long error, or a missing syscall into a
/// permission failure.
/// </remarks>
internal static class DarwinErrno
{
    /// <summary>
    /// Resource temporarily unavailable. 35 here, where Linux has 11 — and 11 on macOS is
    /// <c>EDEADLK</c>.
    /// </summary>
    public const int EAGAIN = 35;

    /// <summary>
    /// Operation not supported. 45 here, and not to be confused with <see cref="EOPNOTSUPP"/>
    /// at 102, which this platform reserves for sockets — a filesystem declining a rename
    /// flag reports this one.
    /// </summary>
    public const int ENOTSUP = 45;

    public const int ELOOP = 62;
    public const int ENAMETOOLONG = 63;
    public const int ENOTEMPTY = 66;
    public const int ENOSYS = 78;
    public const int EOPNOTSUPP = 102;

    /// <summary>Reads a macOS <c>errno</c> as a portable category.</summary>
    public static CapErrorCategory Classify(int errno)
    {
        if (PosixErrno.TryClassify(errno, out CapErrorCategory shared))
        {
            return shared;
        }

        return errno switch
        {
            EAGAIN => CapErrorCategory.Raced,
            ELOOP => CapErrorCategory.SymbolicLinkLoop,
            ENAMETOOLONG => CapErrorCategory.NameTooLong,
            ENOSYS or ENOTSUP or EOPNOTSUPP => CapErrorCategory.NotSupported,
            ENOTEMPTY => CapErrorCategory.NotEmpty,
            _ => CapErrorCategory.Unknown,
        };
    }

    /// <summary>Wraps a macOS <c>errno</c> as a failure, or success when it is zero.</summary>
    public static CapError ToError(int errno) =>
        errno == 0
            ? CapError.Success
            : CapError.Create(Classify(errno), CapErrorSource.Errno, errno);
}
