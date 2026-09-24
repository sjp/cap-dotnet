using System.Runtime.InteropServices;

namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// A <c>struct timespec</c> as both Unix platforms lay it out on a 64-bit process: whole
/// seconds, then nanoseconds, each a machine word.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UnixTimespec
{
    public long Seconds;
    public long Nanoseconds;
}

/// <summary>
/// Turns the seconds-and-nanoseconds pair every Unix filesystem timestamp arrives as into a
/// value the framework understands.
/// </summary>
/// <remarks>
/// <para>
/// Shared by both Unix backends for the same reason the file type bits are: the encoding is
/// fixed by history rather than by either kernel, and two implementations of it would be two
/// chances to get the sign of a pre-1970 timestamp wrong in only one of them.
/// </para>
/// <para>
/// <strong>Out-of-range values are clamped rather than rejected.</strong> A timestamp is
/// attacker-influenced data — anything that can write a file can set one, and a filesystem
/// image can be crafted with any value at all — so a number outside what a
/// <see cref="DateTimeOffset"/> can hold is an ordinary hostile input and not a reason for a
/// metadata read to throw. Clamping reports a time that is wrong by being at the end of the
/// range; the alternative reports nothing at all about a file whose other fields were
/// perfectly readable, which is worse and is also a cheap way to make a caller's directory
/// walk fall over.
/// </para>
/// </remarks>
internal static class UnixTimestamps
{
    /// <summary>The earliest instant representable, in seconds since the Unix epoch.</summary>
    private static readonly long MinimumSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();

    /// <summary>The latest instant representable, in seconds since the Unix epoch.</summary>
    private static readonly long MaximumSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds() - 1;

    /// <summary>Builds an instant from the two halves the kernel reports.</summary>
    /// <param name="seconds">Whole seconds since the Unix epoch, which may be negative.</param>
    /// <param name="nanoseconds">
    /// The sub-second part, which the kernel always reports as a non-negative addition even
    /// when the seconds are negative. Truncated to the hundred-nanosecond tick the framework
    /// counts in, because a timestamp cannot be reported more precisely than it is stored.
    /// </param>
    public static DateTimeOffset FromParts(long seconds, long nanoseconds)
    {
        if (seconds <= MinimumSeconds)
        {
            return DateTimeOffset.MinValue;
        }

        if (seconds >= MaximumSeconds)
        {
            return DateTimeOffset.MaxValue;
        }

        long ticks = Math.Clamp(nanoseconds, 0, 999_999_999) / TimeSpan.NanosecondsPerTick;
        return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(ticks);
    }

    /// <summary>
    /// Builds the entry a call that sets times is given for one of them.
    /// </summary>
    /// <param name="time">What the caller asked for.</param>
    /// <param name="now">
    /// The value this platform reserves in the nanoseconds field for "the time the change is
    /// recorded". It differs between the two Unix platforms, which is why it is passed in.
    /// </param>
    /// <param name="omit">The value it reserves for "leave this time alone".</param>
    /// <remarks>
    /// An instant is split into seconds rounded towards the past and a non-negative remainder,
    /// which is the form the kernel requires even before 1970. Every instant a
    /// <see cref="DateTimeOffset"/> can hold fits, so nothing here is refused; a filesystem
    /// that stores a narrower range clamps what it is given, which is its own documented
    /// behaviour and not something a caller can ask for otherwise.
    /// </remarks>
    public static UnixTimespec ToTimespec(CapFileTime time, long now, long omit)
    {
        if (time.IsNow)
        {
            return new UnixTimespec { Seconds = 0, Nanoseconds = now };
        }

        if (!time.TryGetValue(out DateTimeOffset value))
        {
            return new UnixTimespec { Seconds = 0, Nanoseconds = omit };
        }

        long ticks = value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        long seconds = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out long remainder);
        if (remainder < 0)
        {
            seconds--;
            remainder += TimeSpan.TicksPerSecond;
        }

        return new UnixTimespec { Seconds = seconds, Nanoseconds = remainder * TimeSpan.NanosecondsPerTick };
    }
}
