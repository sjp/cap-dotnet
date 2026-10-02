using System.Buffers.Binary;
using System.Numerics;

namespace Cap.Rand;

/// <summary>
/// Integers, fractions, byte arrays and permutations drawn from any
/// <see cref="IRandomSource"/>, uniformly over the range asked for.
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
/// <item>
/// <see cref="GetDouble"/> draws eight bytes, reads them as a little-endian unsigned integer,
/// keeps its top 53 bits and divides them by <c>2^53</c>.
/// </item>
/// <item>
/// <see cref="Shuffle{T}"/> is the classic Fisher–Yates shuffle: for each index <c>i</c> from
/// the last down to 1, it draws <c>j</c> with <c>GetInt32(i + 1)</c> and swaps the elements at
/// <c>i</c> and <c>j</c>.
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
    /// <remarks>
    /// Exactly as safe to call from several threads at once as <paramref name="source"/>'s
    /// <see cref="IRandomSource.Fill"/> is: safe with <see cref="CapRandom"/>, not with a
    /// shared <see cref="InsecureDeterministicRandom"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
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
    /// <remarks>
    /// Exactly as safe to call from several threads at once as <paramref name="source"/>'s
    /// <see cref="IRandomSource.Fill"/> is: safe with <see cref="CapRandom"/>, not with a
    /// shared <see cref="InsecureDeterministicRandom"/>. A value that needs a redraw makes
    /// several calls to it, and nothing keeps them together, so on a source shared between
    /// threads another caller's draws can fall between them.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
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
    /// <remarks>
    /// Exactly as safe to call from several threads at once as <paramref name="source"/>'s
    /// <see cref="IRandomSource.Fill"/> is: safe with <see cref="CapRandom"/>, not with a
    /// shared <see cref="InsecureDeterministicRandom"/>. A value that needs a redraw makes
    /// several calls to it, and nothing keeps them together, so on a source shared between
    /// threads another caller's draws can fall between them.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="fromInclusive"/> is not below <paramref name="toExclusive"/>, so the
    /// range is empty.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
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
    /// <remarks>
    /// Exactly as safe to call from several threads at once as <paramref name="source"/>'s
    /// <see cref="IRandomSource.Fill"/> is: safe with <see cref="CapRandom"/>, not with a
    /// shared <see cref="InsecureDeterministicRandom"/>. A value that needs a redraw makes
    /// several calls to it, and nothing keeps them together, so on a source shared between
    /// threads another caller's draws can fall between them.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="fromInclusive"/> is not below <paramref name="toExclusive"/>, so the
    /// range is empty.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
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

    /// <summary>
    /// A fraction from zero up to, but not including, one.
    /// </summary>
    /// <param name="source">Where the bytes come from.</param>
    /// <returns>
    /// One of the <c>2^53</c> evenly spaced values <c>k / 2^53</c>, each equally likely. That
    /// spacing is the precision of a <see cref="double"/> just below one, so every value the
    /// result can take near one is reached; values close to zero are not as finely divided as
    /// a <see cref="double"/> could divide them.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Draws exactly eight bytes, reads them as a little-endian unsigned integer, and divides
    /// its top 53 bits by <c>2^53</c>. Nothing is ever redrawn.
    /// </para>
    /// <para>
    /// Exactly as safe to call from several threads at once as <paramref name="source"/>'s
    /// <see cref="IRandomSource.Fill"/> is: safe with <see cref="CapRandom"/>, not with a
    /// shared <see cref="InsecureDeterministicRandom"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public static double GetDouble(this IRandomSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Span<byte> draw = stackalloc byte[sizeof(ulong)];
        source.Fill(draw);

        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(draw) >> 11;
        return value * (1.0 / (1UL << 53));
    }

    /// <summary>
    /// Puts <paramref name="values"/> into a random order, every order equally likely.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">Where the bytes come from.</param>
    /// <param name="values">
    /// The elements to reorder, in place. An array passes as it is; a <c>List&lt;T&gt;</c>
    /// through <c>CollectionsMarshal.AsSpan</c>.
    /// </param>
    /// <remarks>
    /// <para>
    /// The classic Fisher–Yates shuffle: for each index <c>i</c> from the last down to 1,
    /// draw <c>j</c> with <see cref="GetInt32(IRandomSource, int)"/> over <c>i + 1</c> values
    /// and swap the elements at <c>i</c> and <c>j</c>. The draws are therefore those of
    /// <see cref="GetInt32(IRandomSource, int)"/>, so a seeded shuffle can be reproduced
    /// outside .NET. It is not the same sequence of draws as <c>System.Random.Shuffle</c>.
    /// Fewer than two elements draw nothing.
    /// </para>
    /// <para>
    /// Exactly as safe to call from several threads at once as <paramref name="source"/>'s
    /// <see cref="IRandomSource.Fill"/> is: safe with <see cref="CapRandom"/>, not with a
    /// shared <see cref="InsecureDeterministicRandom"/>. The span itself must not be changed
    /// by anyone else while it is being shuffled.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    public static void Shuffle<T>(this IRandomSource source, Span<T> values)
    {
        ArgumentNullException.ThrowIfNull(source);

        for (int i = values.Length - 1; i > 0; i--)
        {
            int j = source.GetInt32(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }
}
