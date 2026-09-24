using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;
using Cap.Primitives.Interop.Windows;
using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Tests;

/// <summary>
/// How platform failure codes are read, and why they cannot share one table.
/// </summary>
public sealed class PlatformErrorTests
{
    /// <summary>
    /// The retry signal a confined open depends on is a different number on each Unix, and
    /// each number means something else on the other platform.
    /// </summary>
    /// <remarks>
    /// This is the case that would be silently wrong if the two tables were merged. Reading
    /// a deadlock report as "resolution raced, try again" would spin; reading a race as a
    /// deadlock would turn an ordinary outcome into a reported failure.
    /// </remarks>
    [Fact]
    public void The_retry_signal_is_numbered_differently_on_each_unix()
    {
        Assert.Equal(11, LinuxErrno.EAGAIN);
        Assert.Equal(35, DarwinErrno.EAGAIN);

        Assert.Equal(CapErrorCategory.Raced, LinuxErrno.Classify(LinuxErrno.EAGAIN));
        Assert.Equal(CapErrorCategory.Raced, DarwinErrno.Classify(DarwinErrno.EAGAIN));

        // Each platform's number, read by the other's table, is not the retry signal.
        Assert.NotEqual(CapErrorCategory.Raced, DarwinErrno.Classify(LinuxErrno.EAGAIN));
        Assert.NotEqual(CapErrorCategory.Raced, LinuxErrno.Classify(DarwinErrno.EAGAIN));
    }

    /// <summary>The link-loop and name-length codes diverge the same way.</summary>
    [Fact]
    public void Link_and_name_failures_are_numbered_differently_on_each_unix()
    {
        Assert.Equal(CapErrorCategory.SymbolicLinkLoop, LinuxErrno.Classify(LinuxErrno.ELOOP));
        Assert.Equal(CapErrorCategory.SymbolicLinkLoop, DarwinErrno.Classify(DarwinErrno.ELOOP));
        Assert.NotEqual(LinuxErrno.ELOOP, DarwinErrno.ELOOP);

        Assert.Equal(CapErrorCategory.NameTooLong, LinuxErrno.Classify(LinuxErrno.ENAMETOOLONG));
        Assert.Equal(CapErrorCategory.NameTooLong, DarwinErrno.Classify(DarwinErrno.ENAMETOOLONG));
        Assert.NotEqual(LinuxErrno.ENAMETOOLONG, DarwinErrno.ENAMETOOLONG);
    }

    /// <summary>The low range is shared, and both tables agree on it.</summary>
    [Theory]
    [InlineData(PosixErrno.ENOENT, CapErrorCategory.NotFound)]
    [InlineData(PosixErrno.EACCES, CapErrorCategory.PermissionDenied)]
    [InlineData(PosixErrno.ENOTDIR, CapErrorCategory.NotADirectory)]
    [InlineData(PosixErrno.EISDIR, CapErrorCategory.IsADirectory)]
    [InlineData(PosixErrno.EXDEV, CapErrorCategory.CrossDevice)]
    [InlineData(PosixErrno.EEXIST, CapErrorCategory.AlreadyExists)]
    [InlineData(PosixErrno.EINVAL, CapErrorCategory.InvalidArgument)]
    internal void The_shared_range_reads_the_same_on_both_platforms(int errno, CapErrorCategory expected)
    {
        Assert.Equal(expected, LinuxErrno.Classify(errno));
        Assert.Equal(expected, DarwinErrno.Classify(errno));
    }

    /// <summary>A code carries its platform with it, so two schemes cannot be confused.</summary>
    [Fact]
    public void A_failure_carries_both_its_reading_and_its_raw_code()
    {
        CapError unix = LinuxErrno.ToError(PosixErrno.ENOENT);
        Assert.True(unix.IsFailure);
        Assert.Equal(CapErrorCategory.NotFound, unix.Category);
        Assert.Equal(CapErrorSource.Errno, unix.Source);
        Assert.Equal(PosixErrno.ENOENT, unix.RawCode);

        CapError windows = CapError.Create(
            CapErrorCategory.NotFound, CapErrorSource.NtStatus, NtStatusCodes.STATUS_OBJECT_NAME_NOT_FOUND);
        Assert.Equal(CapErrorSource.NtStatus, windows.Source);

        // Same reading, different platform: not the same failure.
        Assert.NotEqual(unix, windows);
    }

    /// <summary>Zero is success, and success is the default value.</summary>
    [Fact]
    public void Success_is_the_default()
    {
        Assert.True(default(CapError).IsSuccess);
        Assert.True(LinuxErrno.ToError(0).IsSuccess);
        Assert.True(DarwinErrno.ToError(0).IsSuccess);
        Assert.Null(CapError.Success.FailureDescription);
    }

    /// <summary>
    /// Windows reports the several kinds of reparse point separately, and all of them are
    /// read as a reparse point rather than as a generic failure.
    /// </summary>
    [Theory]
    [InlineData(NtStatusCodes.STATUS_REPARSE_POINT_ENCOUNTERED)]
    [InlineData(NtStatusCodes.STATUS_IO_REPARSE_TAG_NOT_HANDLED)]
    [InlineData(NtStatusCodes.STATUS_REPARSE)]
    public void Reparse_statuses_survive_translation(int status) =>
        Assert.Equal(CapErrorCategory.Reparse, NtStatusCodes.Classify(status));

    /// <summary>A non-negative status is not a failure, whatever else it says.</summary>
    [Fact]
    public void Only_negative_statuses_are_failures()
    {
        Assert.False(NtStatusCodes.IsFailure(NtStatusCodes.STATUS_SUCCESS));
        Assert.False(NtStatusCodes.IsFailure(NtStatusCodes.STATUS_REPARSE));
        Assert.True(NtStatusCodes.IsFailure(NtStatusCodes.STATUS_ACCESS_DENIED));
    }

    /// <summary>The Win32 table answers where the status table is consulted as a fallback.</summary>
    [Theory]
    [InlineData(Win32Errors.ERROR_FILE_NOT_FOUND, CapErrorCategory.NotFound)]
    [InlineData(Win32Errors.ERROR_ACCESS_DENIED, CapErrorCategory.PermissionDenied)]
    [InlineData(Win32Errors.ERROR_DIR_NOT_EMPTY, CapErrorCategory.NotEmpty)]
    [InlineData(Win32Errors.ERROR_NOT_A_REPARSE_POINT, CapErrorCategory.Reparse)]
    internal void Win32_errors_are_read_from_their_own_table(int error, CapErrorCategory expected) =>
        Assert.Equal(expected, Win32Errors.Classify(error));

    /// <summary>
    /// A hard link refused with the permission code is reread as unsupported only on a volume
    /// that cannot hold one, and that volume is recognised by the type number the kernel
    /// reports for it. Asked of <c>/proc</c>, whose type is the same on every Linux.
    /// </summary>
    /// <remarks>
    /// Read from the wrong place, the number would be some other field — a block size, a
    /// count — that never equals a type the library compares against, and a FAT volume's
    /// refusal would silently go on being reported as a permission failure.
    /// </remarks>
    [Fact]
    public void Linux_recognises_a_volume_by_its_filesystem_type()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using SafeFileHandle proc = File.OpenHandle("/proc/self/stat", FileMode.Open, FileAccess.Read);
        int fd = (int)proc.DangerousGetHandle();

        Assert.True(LinuxPlatformOps.TryGetFilesystemType(fd, out long type), "fstatfs failed on /proc.");
        Assert.Equal(LinuxConstants.PROC_SUPER_MAGIC, type);
        Assert.False(LinuxPlatformOps.HasNoHardLinks(fd));
    }
}
