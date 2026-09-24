using Cap.Primitives.Interop.Unix;
using Cap.Primitives.Interop.Windows;

namespace Cap.Primitives.Tests;

/// <summary>
/// What a request to set a time says, and how each platform's encoding of it is built.
/// </summary>
/// <remarks>
/// The encodings are checked here without a filesystem, because the cases that go wrong are
/// ones a disk rarely produces: an instant before 1970, whose seconds are negative while the
/// kernel insists the fraction is not, and an instant before the start of the Windows
/// calendar, which that platform has no way to write.
/// </remarks>
public sealed class CapFileTimeTests
{
    private const long LinuxNow = (1L << 30) - 1;
    private const long LinuxOmit = (1L << 30) - 2;

    /// <summary>The default value leaves a time alone, so a time left out is never changed.</summary>
    [Fact]
    public void The_default_leaves_a_time_alone()
    {
        CapFileTime time = default;

        Assert.True(time.IsUnchanged);
        Assert.False(time.IsNow);
        Assert.False(time.TryGetValue(out _));
        Assert.Equal(CapFileTime.Unchanged, time);
    }

    /// <summary>"Now" carries no instant; the system supplies one.</summary>
    [Fact]
    public void Now_carries_no_instant()
    {
        Assert.True(CapFileTime.Now.IsNow);
        Assert.False(CapFileTime.Now.IsUnchanged);
        Assert.False(CapFileTime.Now.TryGetValue(out _));
        Assert.NotEqual(CapFileTime.Unchanged, CapFileTime.Now);
    }

    /// <summary>
    /// An instant is held as an instant: two spellings of the same moment in different
    /// offsets ask for the same thing.
    /// </summary>
    [Fact]
    public void An_instant_is_the_same_request_whatever_its_offset()
    {
        DateTimeOffset utc = new(2010, 1, 2, 3, 4, 5, TimeSpan.Zero);
        CapFileTime inUtc = CapFileTime.At(utc);
        CapFileTime elsewhere = CapFileTime.At(utc.ToOffset(TimeSpan.FromHours(5.5)));

        Assert.Equal(inUtc, elsewhere);
        Assert.Equal(inUtc.GetHashCode(), elsewhere.GetHashCode());
        Assert.True(elsewhere.TryGetValue(out DateTimeOffset value));
        Assert.Equal(TimeSpan.Zero, value.Offset);
        Assert.Equal(utc, value);
    }

    /// <summary>The text form says what is asked for, for a log line.</summary>
    [Fact]
    public void The_text_form_says_what_is_asked_for()
    {
        Assert.Equal("Unchanged", CapFileTime.Unchanged.ToString());
        Assert.Equal("Now", CapFileTime.Now.ToString());
        Assert.Equal(
            "2010-01-02T03:04:05.0000000+00:00",
            CapFileTime.At(new DateTimeOffset(2010, 1, 2, 3, 4, 5, TimeSpan.Zero)).ToString());
    }

    /// <summary>The two requests that carry no instant use the values the kernel reserves.</summary>
    [Fact]
    public void Unix_encoding_uses_the_reserved_values_for_now_and_unchanged()
    {
        UnixTimespec now = UnixTimestamps.ToTimespec(CapFileTime.Now, LinuxNow, LinuxOmit);
        UnixTimespec unchanged = UnixTimestamps.ToTimespec(CapFileTime.Unchanged, LinuxNow, LinuxOmit);

        Assert.Equal(LinuxNow, now.Nanoseconds);
        Assert.Equal(LinuxOmit, unchanged.Nanoseconds);
    }

    /// <summary>An instant after 1970 is whole seconds and the remainder in nanoseconds.</summary>
    [Fact]
    public void Unix_encoding_splits_an_instant_into_seconds_and_nanoseconds()
    {
        DateTimeOffset instant = DateTimeOffset.FromUnixTimeSeconds(1_000_000_000).AddTicks(1234567);

        UnixTimespec encoded = UnixTimestamps.ToTimespec(CapFileTime.At(instant), LinuxNow, LinuxOmit);

        Assert.Equal(1_000_000_000, encoded.Seconds);
        Assert.Equal(123_456_700, encoded.Nanoseconds);
    }

    /// <summary>
    /// Before 1970 the seconds are rounded towards the past, so the fraction stays positive,
    /// which is the only form the kernel accepts.
    /// </summary>
    [Fact]
    public void Unix_encoding_keeps_the_fraction_positive_before_the_epoch()
    {
        DateTimeOffset instant = DateTimeOffset.UnixEpoch.AddTicks(-2_500_000);

        UnixTimespec encoded = UnixTimestamps.ToTimespec(CapFileTime.At(instant), LinuxNow, LinuxOmit);

        Assert.Equal(-1, encoded.Seconds);
        Assert.Equal(750_000_000, encoded.Nanoseconds);
        Assert.Equal(instant, UnixTimestamps.FromParts(encoded.Seconds, encoded.Nanoseconds));
    }

    /// <summary>The Windows encoding counts from 1601 and round-trips.</summary>
    [Fact]
    public void Windows_encoding_counts_from_1601()
    {
        DateTimeOffset instant = new DateTimeOffset(2003, 4, 5, 6, 7, 8, TimeSpan.Zero).AddTicks(1234567);

        Assert.True(FileTimes.TryToTicks(instant, out long ticks));
        Assert.Equal(instant.UtcDateTime.ToFileTimeUtc(), ticks);
        Assert.Equal(instant, FileTimes.ToDateTimeOffset(ticks));
    }

    /// <summary>
    /// An instant at or before the start of 1601 cannot be written on Windows: zero and the
    /// negative counts are orders to the filesystem, not instants.
    /// </summary>
    [Fact]
    public void Windows_encoding_refuses_an_instant_it_cannot_write()
    {
        Assert.False(FileTimes.TryToTicks(new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero), out _));
        Assert.False(FileTimes.TryToTicks(new DateTimeOffset(1500, 1, 1, 0, 0, 0, TimeSpan.Zero), out _));
        Assert.True(FileTimes.TryToTicks(new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1), out long ticks));
        Assert.Equal(1, ticks);
    }
}
