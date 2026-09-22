using System.Buffers.Binary;
using System.Numerics;

namespace Cap.Rand;

/// <summary>
/// Integers and byte arrays drawn from any <see cref="IRandomSource"/>, uniformly over the
/// range asked for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No remainder bias.</strong> The obvious way to get a number below <c>n</c>, taking a
/// random integer modulo <c>n</c>, makes the small values slightly more likely whenever
/// <c>n</c> does not divide the integer's range evenly. These helpers mask each draw down to
/// the smallest power of two that covers the range and draw again when the result lands
/// outside it. That is always uniform, and fewer than half of all draws are thrown away even
/// in the worst case.
/// </para>
/// <para>
/// <strong>The byte-level method is part of the contract</strong>, because it is what makes a
/// seeded <see cref="InsecureDeterministicRandom"/> give the same integers from one release to
/// the next. For a range of width <c>w</c>:
/// </para>
/// <list type="bullet">
/// <item>
/// When <c>w</c> is 1, the only possible value is returned and nothing is drawn.
/// </item>
/// <item>
/// Otherwise each attempt draws four bytes (for the 32-bit helpers) or eight (for the 64-bit
/// one), reads them as a little-endian unsigned integer, and keeps the low bits that the mask
/// <c>2^k - 1</c> covers, where <c>2^k</c> is the smallest power of two not below <c>w</c>. The
/// first attempt whose value is below <c>w</c> is used, and the result is the lower bound plus
/// that value.
/// </item>
/// </list>
/// </remarks>
public static class RandomSourceExtensions
{
    /// <summary>
    /// A new array of <paramref name="count"/> bytes drawn from <paramref name="source"/>.
    /// </summary>
    /// <param name="source">Where the bytes come from.</param>
    /// <param name="count">How many bytes. Zero gives an empty array.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static byte[] GetBytes(this IRandomSource source, int count)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        byte[] bytes = new byte[count];
        source.Fill(bytes);
        return bytes;
    }

    /// <summary>
    /// An integer from zero up to, but not including, <paramref name="toExclusive"/>.
    /// </summary>
    /// <param name="source">Where the bytes come from.</param>
    /// <param name="toExclusive">One more than the largest value that may be returned.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="toExclusive"/> is zero or negative, so the range is empty.
    /// </exception>
    public static int GetInt32(this IRandomSource source, int toExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toExclusive);
        return GetInt32(source, 0, toExclusive);
    }

    /// <summary>
    /// An integer from <paramref name="fromInclusive"/> up to, but not including,
    /// <paramref name="toExclusive"/>.
    /// </summary>
    /// <param name="source">Where the bytes come from.</param>
    /// <param name="fromInclusive">The smallest value that may be returned.</param>
    /// <param name="toExclusive">One more than the largest value that may be returned.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="fromInclusive"/> is not below <paramref name="toExclusive"/>, so the
    /// range is empty.
    /// </exception>
    public static int GetInt32(this IRandomSource source, int fromInclusive, int toExclusive)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fromInclusive, toExclusive);

        // The width of any int range fits in a uint, even from int.MinValue to int.MaxValue.
        uint width = unchecked((uint)(toExclusive - fromInclusive));
        if (width == 1)
        {
            return fromInclusive;
        }

        uint mask = uint.MaxValue >> BitOperations.LeadingZeroCount(width - 1);

        Span<byte> draw = stackalloc byte[sizeof(uint)];
        uint value;
        do
        {
            source.Fill(draw);
            value = BinaryPrimitives.ReadUInt32LittleEndian(draw) & mask;
        }
        while (value >= width);

        return unchecked((int)((uint)fromInclusive + value));
    }

    /// <summary>
    /// An integer from <paramref name="fromInclusive"/> up to, but not including,
    /// <paramref name="toExclusive"/>.
    /// </summary>
    /// <param name="source">Where the bytes come from.</param>
    /// <param name="fromInclusive">The smallest value that may be returned.</param>
    /// <param name="toExclusive">One more than the largest value that may be returned.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="fromInclusive"/> is not below <paramref name="toExclusive"/>, so the
    /// range is empty.
    /// </exception>
    public static long GetInt64(this IRandomSource source, long fromInclusive, long toExclusive)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fromInclusive, toExclusive);

        ulong width = unchecked((ulong)(toExclusive - fromInclusive));
        if (width == 1)
        {
            return fromInclusive;
        }

        ulong mask = ulong.MaxValue >> BitOperations.LeadingZeroCount(width - 1);

        Span<byte> draw = stackalloc byte[sizeof(ulong)];
        ulong value;
        do
        {
            source.Fill(draw);
            value = BinaryPrimitives.ReadUInt64LittleEndian(draw) & mask;
        }
        while (value >= width);

        return unchecked((long)((ulong)fromInclusive + value));
    }
}
