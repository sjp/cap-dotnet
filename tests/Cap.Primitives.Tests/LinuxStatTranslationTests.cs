using System.Runtime.Versioning;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;

namespace Cap.Primitives.Tests;

/// <summary>
/// How a <c>statx</c> reply becomes a caller-facing snapshot: which fields the kernel may leave
/// out and still be answered, and which leave nothing worth answering.
/// </summary>
/// <remarks>
/// Fed hand-built replies rather than real ones, because the interesting replies are the ones
/// no filesystem on a test host gives — a kernel withholds the access time only from a
/// filesystem whose superblock says it keeps none.
/// </remarks>
public sealed class LinuxStatTranslationTests
{
    private const uint Required =
        LinuxConstants.STATX_TYPE | LinuxConstants.STATX_MODE | LinuxConstants.STATX_INO |
        LinuxConstants.STATX_SIZE | LinuxConstants.STATX_MTIME | LinuxConstants.STATX_UID |
        LinuxConstants.STATX_NLINK;

    private const uint Optional =
        LinuxConstants.STATX_ATIME | LinuxConstants.STATX_CTIME | LinuxConstants.STATX_BTIME;

    /// <summary>The bits that, missing one at a time, must refuse the whole reply.</summary>
    public static TheoryData<uint> RequiredBits =>
    [
        LinuxConstants.STATX_TYPE,
        LinuxConstants.STATX_MODE,
        LinuxConstants.STATX_INO,
        LinuxConstants.STATX_SIZE,
        LinuxConstants.STATX_MTIME,
        LinuxConstants.STATX_UID,
        LinuxConstants.STATX_NLINK,
    ];

    /// <summary>A full reply carries every field through.</summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    public void Full_reply_is_translated()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The translation is the Linux backend's.");
            return;
        }

        StatxBuffer buffer = Reply(Required | Optional);

        Assert.True(LinuxPlatformOps.TranslateStatx(buffer, out CapNodeStat stat).IsSuccess);

        Assert.Equal(CapFileType.File, stat.Type);
        Assert.Equal((UInt128)42, stat.NodeId);
        Assert.Equal(7, stat.Length);
        Assert.Equal(2, stat.LinkCount);
        Assert.Equal(1000u, stat.UnixOwnerId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(100), stat.LastAccessTime);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(200), stat.LastWriteTime);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(300), stat.ChangeTime);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(400), stat.CreationTime);
    }

    /// <summary>
    /// A reply without the access time is still an answer, with the access time absent rather
    /// than the placeholder the kernel left in the field.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    public void Reply_without_access_time_reports_it_absent()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The translation is the Linux backend's.");
            return;
        }

        StatxBuffer buffer = Reply(Required);

        Assert.True(LinuxPlatformOps.TranslateStatx(buffer, out CapNodeStat stat).IsSuccess);

        Assert.Null(stat.LastAccessTime);
        Assert.Null(stat.ChangeTime);
        Assert.Null(stat.CreationTime);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(200), stat.LastWriteTime);
        Assert.Equal(7, stat.Length);
    }

    /// <summary>
    /// A reply missing any field a zero would pass for is refused whole, rather than answered
    /// with a plausible-looking zero.
    /// </summary>
    [Theory]
    [MemberData(nameof(RequiredBits))]
    [SupportedOSPlatform("linux")]
    public void Reply_without_a_required_field_is_not_supported(uint missing)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The translation is the Linux backend's.");
            return;
        }

        StatxBuffer buffer = Reply((Required | Optional) & ~missing);

        CapError error = LinuxPlatformOps.TranslateStatx(buffer, out _);

        Assert.Equal(CapErrorCategory.NotSupported, error.Category);
    }

    private static StatxBuffer Reply(uint mask) => new()
    {
        Mask = mask,
        Mode = 0x8000 | 0b110_100_100,
        Inode = 42,
        Size = 7,
        HardLinkCount = 2,
        UserId = 1000,
        AccessTime = new StatxTimestamp { Seconds = 100 },
        ModifyTime = new StatxTimestamp { Seconds = 200 },
        ChangeTime = new StatxTimestamp { Seconds = 300 },
        BirthTime = new StatxTimestamp { Seconds = 400 },
    };
}
