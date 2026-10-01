using System.Runtime.InteropServices;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;
using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Tests;

/// <summary>
/// That a reservation asked for at open is made on a file the open starts from nothing, and
/// only there.
/// </summary>
/// <remarks>
/// <para>
/// Linux reserves the range from the start of the file; macOS reserves from its end. On an
/// empty file the two agree. On a file that already held data they would not — macOS would
/// claim the requested size again on top of what the file already uses — and neither is what
/// the request means, so a mode that may create but found the file already there reserves
/// nothing.
/// </para>
/// <para>
/// What is checked is the space the file occupies, read from the descriptor's block count,
/// because the length is deliberately left alone either way.
/// </para>
/// </remarks>
[Collection(PlatformOpsTestGroup.Name)]
public sealed class PreallocationTests : IDisposable
{
    private const long Mebibyte = 1024 * 1024;

    private readonly string _root = Directory.CreateTempSubdirectory("cap-prealloc-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A file the open brings into existence gets the room asked for, and no length.</summary>
    [Theory]
    [InlineData(FileMode.CreateNew)]
    [InlineData(FileMode.Create)]
    [InlineData(FileMode.OpenOrCreate)]
    [InlineData(FileMode.Append)]
    public void A_created_file_is_reserved_without_being_lengthened(FileMode mode)
    {
        SkipUnlessUnix();

        using SafeFileHandle file = Open("new", mode, 4 * Mebibyte);

        Assert.Equal(0, RandomAccess.GetLength(file));
        SkipUnlessReserved(AllocatedBytes(file));
        Assert.True(AllocatedBytes(file) >= 4 * Mebibyte, $"{AllocatedBytes(file)} bytes allocated");
    }

    /// <summary>An existing empty file is started from nothing, so it is reserved too.</summary>
    [Theory]
    [InlineData(FileMode.OpenOrCreate)]
    [InlineData(FileMode.Append)]
    public void An_existing_empty_file_is_reserved(FileMode mode)
    {
        SkipUnlessUnix();
        File.WriteAllBytes(Path.Combine(_root, "empty"), []);

        using SafeFileHandle file = Open("empty", mode, 4 * Mebibyte);

        Assert.Equal(0, RandomAccess.GetLength(file));
        SkipUnlessReserved(AllocatedBytes(file));
        Assert.True(AllocatedBytes(file) >= 4 * Mebibyte, $"{AllocatedBytes(file)} bytes allocated");
    }

    /// <summary>
    /// A file that already held data is opened as it stood: nothing is reserved on top of it.
    /// </summary>
    [Theory]
    [InlineData(FileMode.OpenOrCreate)]
    [InlineData(FileMode.Append)]
    public void An_existing_file_with_data_is_not_reserved(FileMode mode)
    {
        SkipUnlessUnix();
        using (SafeFileHandle probe = Open("probe", FileMode.CreateNew, 4 * Mebibyte))
        {
            SkipUnlessReserved(AllocatedBytes(probe));
        }

        File.WriteAllBytes(Path.Combine(_root, "data"), new byte[Mebibyte]);

        using SafeFileHandle file = Open("data", mode, 4 * Mebibyte);

        Assert.Equal(Mebibyte, RandomAccess.GetLength(file));
        Assert.True(AllocatedBytes(file) < 2 * Mebibyte, $"{AllocatedBytes(file)} bytes allocated");
    }

    /// <summary>A file the open empties is started from nothing and gets the room asked for.</summary>
    [Fact]
    public void A_truncated_file_is_reserved()
    {
        SkipUnlessUnix();
        File.WriteAllBytes(Path.Combine(_root, "data"), new byte[Mebibyte]);

        using SafeFileHandle file = Open("data", FileMode.Truncate, 4 * Mebibyte);

        Assert.Equal(0, RandomAccess.GetLength(file));
        SkipUnlessReserved(AllocatedBytes(file));
        Assert.True(AllocatedBytes(file) >= 4 * Mebibyte, $"{AllocatedBytes(file)} bytes allocated");
    }

    private SafeFileHandle Open(string name, FileMode mode, long preallocationSize)
    {
        CapResult<SafeDirHandle> root = PlatformOps.Host.OpenAmbientDirectory(_root, CapAccess.Read);
        Assert.True(root.IsSuccess, root.Error.FailureDescription);
        using SafeDirHandle dir = root.Value;

        FileOpenRequest request = new(
            mode,
            mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite,
            FileShare.None,
            FileOptions.None,
            preallocationSize);
        CapResult<SafeFileHandle> result = PlatformOps.Host.OpenChildFile(dir, name, request);
        Assert.True(result.IsSuccess, result.Error.FailureDescription);
        return result.Value;
    }

    private static void SkipUnlessUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("The reservation measured here is the Unix backends'.");
        }
    }

    /// <summary>
    /// Skips when the filesystem under the test directory ignores reservations, which the
    /// backends accept silently because the file is usable without one.
    /// </summary>
    private static void SkipUnlessReserved(long allocated)
    {
        if (allocated == 0)
        {
            Assert.Skip("The filesystem under the temporary directory does not reserve space.");
        }
    }

    /// <summary>The space the file occupies on disk, in bytes.</summary>
    private static unsafe long AllocatedBytes(SafeFileHandle file)
    {
        const long BlockUnit = 512;
        int fd = (int)file.DangerousGetHandle();

        if (OperatingSystem.IsMacOS())
        {
            DarwinStat stat = default;
            Assert.True(DarwinNative.FStat(fd, &stat) == 0, $"fstat failed: {Marshal.GetLastPInvokeError()}");
            return stat.Blocks * BlockUnit;
        }

        const uint StatxBlocks = 0x0400;
        ReadOnlySpan<byte> empty = [0];
        StatxBuffer buffer = default;
        fixed (byte* name = empty)
        {
            long result = LinuxNative.Statx(
                LinuxConstants.SYS_statx, fd, name, LinuxConstants.AT_EMPTY_PATH, StatxBlocks, &buffer);
            Assert.True(result == 0, $"statx failed: {Marshal.GetLastPInvokeError()}");
        }

        Assert.True((buffer.Mask & StatxBlocks) != 0, "statx did not report a block count");
        return (long)buffer.Blocks * BlockUnit;
    }
}
