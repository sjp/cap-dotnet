namespace Cap.Primitives.Interop.Windows;

/// <summary>
/// Turns the hundred-nanosecond count this platform stores timestamps as into a value the
/// framework understands.
/// </summary>
/// <remarks>
/// <para>
/// The count runs from the start of 1601, which is earlier than the framework's own zero, so
/// the conversion is an addition to a fixed origin rather than a reinterpretation.
/// </para>
/// <para>
/// <strong>Out-of-range values are clamped rather than rejected.</strong> A timestamp is
/// attacker-influenced data — anything that can write a file can set one — so a value the
/// framework cannot represent is an ordinary hostile input and not a reason for a metadata
/// read to fail. Clamping reports a time that is wrong by being at the end of the range; the
/// alternative reports nothing about a file whose other fields were perfectly readable,
/// which is both less useful and a cheap way to stop a caller's walk.
/// </para>
/// </remarks>
internal static class FileTimes
{
    /// <summary>The instant the platform counts from.</summary>
    private static readonly DateTimeOffset Origin = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The largest count that still lands inside the representable range.</summary>
    private static readonly long MaximumTicks = (DateTimeOffset.MaxValue - Origin).Ticks;

    /// <summary>Builds an instant from a stored count.</summary>
    public static DateTimeOffset ToDateTimeOffset(long ticks) =>
        Origin.AddTicks(Math.Clamp(ticks, 0, MaximumTicks));
}
