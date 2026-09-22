namespace Cap.Primitives.Interop.Unix;

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
}
