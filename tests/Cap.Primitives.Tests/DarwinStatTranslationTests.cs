using System.Runtime.Versioning;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;

namespace Cap.Primitives.Tests;

/// <summary>
/// How a macOS <c>stat</c> reply becomes a caller-facing snapshot, and in particular when its
/// creation time is read as one the filesystem does not keep.
/// </summary>
/// <remarks>
/// Fed hand-built replies rather than real ones, because the interesting replies are the ones
/// a test host does not give on its own — a creation time before 1970 has to be put there on
/// purpose.
/// </remarks>
public sealed class DarwinStatTranslationTests
{
    /// <summary>A full reply carries every field through.</summary>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void Full_reply_is_translated()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("The translation is the macOS backend's.");
            return;
        }

        CapNodeStat stat = DarwinPlatformOps.Describe(Reply(new DarwinTimespec { Seconds = 400 }));

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
    /// A creation time that is zero in both halves is what a filesystem that keeps none
    /// leaves behind, and is reported absent rather than as the start of 1970.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void All_zero_creation_time_is_reported_absent()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("The translation is the macOS backend's.");
            return;
        }

        CapNodeStat stat = DarwinPlatformOps.Describe(Reply(default));

        Assert.Null(stat.CreationTime);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(200), stat.LastWriteTime);
    }

    /// <summary>
    /// A creation time before 1970 is a recorded time, not a missing one.
    /// </summary>
    /// <remarks>
    /// HFS+ dates run from 1904, and setting attributes can store any creation time at all,
    /// so a negative number of seconds is an ordinary answer on this platform.
    /// </remarks>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void Creation_time_before_the_epoch_is_reported()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("The translation is the macOS backend's.");
            return;
        }

        DateTimeOffset expected = new(1960, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DarwinTimespec birth = new() { Seconds = expected.ToUnixTimeSeconds() };

        CapNodeStat stat = DarwinPlatformOps.Describe(Reply(birth));

        Assert.Equal(expected, stat.CreationTime);
    }

    /// <summary>
    /// A creation time inside the first second of 1970 is recorded too: only both halves
    /// being zero means absent.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void Creation_time_within_the_first_second_of_the_epoch_is_reported()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("The translation is the macOS backend's.");
            return;
        }

        CapNodeStat stat = DarwinPlatformOps.Describe(Reply(new DarwinTimespec { Nanoseconds = 500 }));

        Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks(5), stat.CreationTime);
    }

    private static DarwinStat Reply(DarwinTimespec birth) => new()
    {
        Mode = 0x8000 | 0b110_100_100,
        Inode = 42,
        Size = 7,
        HardLinkCount = 2,
        UserId = 1000,
        AccessTime = new DarwinTimespec { Seconds = 100 },
        ModifyTime = new DarwinTimespec { Seconds = 200 },
        ChangeTime = new DarwinTimespec { Seconds = 300 },
        BirthTime = birth,
    };
}
