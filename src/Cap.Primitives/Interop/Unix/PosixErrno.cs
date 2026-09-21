namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// The <c>errno</c> values that hold the same number on every Unix this library targets.
/// </summary>
/// <remarks>
/// <para>
/// The low range — everything up to <c>ERANGE</c> — comes from the original V7 list and has
/// not moved since. Above it the platforms diverge, so nothing above <c>ERANGE</c> belongs
/// here even when both platforms happen to define the name.
/// </para>
/// <para>
/// The divergence is not a curiosity. On Linux <c>EAGAIN</c> is 11; on macOS 11 is
/// <c>EDEADLK</c> and <c>EAGAIN</c> is 35, which on Linux is <c>EDEADLK</c>. Confined
/// resolution treats <c>EAGAIN</c> as "retry, this raced" — so a shared table would, on one
/// of the two platforms, retry a deadlock report and report a race as a deadlock.
/// </para>
/// </remarks>
internal static class PosixErrno
{
    public const int EPERM = 1;
    public const int ENOENT = 2;
    public const int EINTR = 4;
    public const int EIO = 5;
    public const int ENXIO = 6;
    public const int EBADF = 9;
    public const int ENOMEM = 12;
    public const int EACCES = 13;
    public const int EFAULT = 14;
    public const int EBUSY = 16;
    public const int EEXIST = 17;
    public const int EXDEV = 18;
    public const int ENODEV = 19;
    public const int ENOTDIR = 20;
    public const int EISDIR = 21;
    public const int EINVAL = 22;
    public const int ENFILE = 23;
    public const int EMFILE = 24;
    public const int ENOSPC = 28;
    public const int EROFS = 30;
    public const int EMLINK = 31;
    public const int ERANGE = 34;

    /// <summary>
    /// Classifies an <c>errno</c> from the shared range.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the value was recognised. <see langword="false"/> means
    /// only that it is outside the shared range and the caller's own table must answer.
    /// </returns>
    public static bool TryClassify(int errno, out CapErrorCategory category)
    {
        switch (errno)
        {
            case ENOENT:
            case ENXIO:
            case ENODEV:
                category = CapErrorCategory.NotFound;
                return true;
            case EPERM:
            case EACCES:
                category = CapErrorCategory.PermissionDenied;
                return true;
            case EEXIST:
                category = CapErrorCategory.AlreadyExists;
                return true;
            case ENOTDIR:
                category = CapErrorCategory.NotADirectory;
                return true;
            case EISDIR:
                category = CapErrorCategory.IsADirectory;
                return true;
            case EXDEV:
                category = CapErrorCategory.CrossDevice;
                return true;
            case EINTR:
                category = CapErrorCategory.Interrupted;
                return true;
            case EINVAL:
                category = CapErrorCategory.InvalidArgument;
                return true;
            case EMFILE:
            case ENFILE:
                category = CapErrorCategory.OutOfHandles;
                return true;
            case EROFS:
                category = CapErrorCategory.ReadOnlyFilesystem;
                return true;
            case EBADF:
            case EFAULT:
            case EIO:
            case EBUSY:
            case ENOMEM:
            case ENOSPC:
            case EMLINK:
            case ERANGE:
                category = CapErrorCategory.Unknown;
                return true;
            default:
                category = CapErrorCategory.Unknown;
                return false;
        }
    }
}
