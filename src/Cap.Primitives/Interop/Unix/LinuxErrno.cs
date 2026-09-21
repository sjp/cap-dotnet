namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// Linux <c>errno</c> values above the shared POSIX range, and how this library reads them.
/// </summary>
/// <remarks>
/// The numbers here are the ones from the kernel's generic table, which is what x86-64 and
/// AArch64 both use. A handful of older architectures — Alpha, MIPS, PA-RISC, SPARC —
/// override it; none is a target, and adding one would mean revisiting this file rather than
/// assuming it still applies.
/// </remarks>
internal static class LinuxErrno
{
    /// <summary>
    /// Resource temporarily unavailable. Confined resolution reports this when it lost a
    /// race with a concurrent rename and should simply be tried again.
    /// </summary>
    public const int EAGAIN = 11;

    public const int ENAMETOOLONG = 36;
    public const int ENOSYS = 38;
    public const int ENOTEMPTY = 39;
    public const int ELOOP = 40;
    public const int EOPNOTSUPP = 95;
    public const int ESTALE = 116;

    /// <summary>Reads a Linux <c>errno</c> as a portable category.</summary>
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
            ENOSYS or EOPNOTSUPP => CapErrorCategory.NotSupported,
            ENOTEMPTY => CapErrorCategory.NotEmpty,
            ESTALE => CapErrorCategory.NotFound,
            _ => CapErrorCategory.Unknown,
        };
    }

    /// <summary>Wraps a Linux <c>errno</c> as a failure, or success when it is zero.</summary>
    public static CapError ToError(int errno) =>
        errno == 0
            ? CapError.Success
            : CapError.Create(Classify(errno), CapErrorSource.Errno, errno);
}
