using Cap.Primitives;

namespace Cap.Rand.Tests;

/// <summary>
/// The integer helpers: their bounds, their rejection of out-of-range draws, and how many
/// bytes they consume doing it.
/// </summary>
/// <remarks>
/// Most of these run against a scripted source that hands out exactly the bytes a test
/// chooses, so that a rejected draw can be forced and observed rather than hoped for.
/// </remarks>
public sealed class RandomSourceExtensionsTests
{
    [Fact]
    public void An_empty_range_is_refused()
    {
        var random = new InsecureDeterministicRandom(0);

        Assert.Throws<ArgumentOutOfRangeException>(() => random.GetInt32(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => random.GetInt32(-5));
        Assert.Throws<ArgumentOutOfRangeException>(() => random.GetInt32(3, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => random.GetInt32(4, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => random.GetInt64(9, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => random.GetBytes(-1));
    }

    [Fact]
    public void A_range_of_one_value_returns_it_without_drawing()
    {
        var source = new ScriptedSource([]);

        Assert.Equal(17, source.GetInt32(17, 18));
        Assert.Equal(long.MinValue, source.GetInt64(long.MinValue, long.MinValue + 1));
        Assert.Equal(0, source.Consumed);
    }

    /// <summary>
    /// A range of ten is masked to four bits, so a draw of 12 lands outside it and must be
    /// thrown away, not reduced. Reducing it would give 2, and make 0 to 5 more likely than
    /// 6 to 9.
    /// </summary>
    [Fact]
    public void A_draw_outside_the_range_is_redrawn_not_reduced()
    {
        var source = new ScriptedSource(
        [
            12, 0, 0, 0,   // masked to 12: outside [0, 10), rejected
            15, 0, 0, 0,   // masked to 15: rejected
            0xF7, 0xFF, 0xFF, 0xFF, // masked to 7: accepted, the high bits are ignored
        ]);

        Assert.Equal(7, source.GetInt32(10));
        Assert.Equal(12, source.Consumed);
    }

    [Fact]
    public void The_lower_bound_is_added_to_the_draw()
    {
        var source = new ScriptedSource([3, 0, 0, 0]);

        Assert.Equal(-97, source.GetInt32(-100, -90));
    }

    [Fact]
    public void The_full_int32_range_takes_every_bit_of_the_draw()
    {
        var low = new ScriptedSource([0, 0, 0, 0]);
        var high = new ScriptedSource([0xFF, 0xFF, 0xFF, 0xFF]);

        Assert.Equal(int.MinValue, low.GetInt32(int.MinValue, int.MaxValue));
        Assert.Equal(int.MaxValue - 1, new ScriptedSource([0xFE, 0xFF, 0xFF, 0xFF]).GetInt32(int.MinValue, int.MaxValue));

        // 2^32 - 1 is the one value outside a width of 2^32 - 1, so it is redrawn.
        Assert.Throws<InvalidOperationException>(() => high.GetInt32(int.MinValue, int.MaxValue));
    }

    [Fact]
    public void The_full_int64_range_takes_every_bit_of_the_draw()
    {
        var source = new ScriptedSource([0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

        Assert.Equal(long.MaxValue - 1, source.GetInt64(long.MinValue, long.MaxValue));
    }

    /// <summary>
    /// A coarse check that every value in a small range turns up about equally often, run
    /// against both sources. It would not catch a subtle bias, and is not meant to; it catches
    /// the helper collapsing onto part of the range.
    /// </summary>
    [Fact]
    public void Every_value_in_a_small_range_is_reached_about_equally()
    {
        IRandomSource[] sources =
        [
            new InsecureDeterministicRandom(2024),
            CapRandom.System(AmbientAuthority.Acquire()),
        ];

        foreach (var source in sources)
        {
            int[] counts = new int[6];
            for (int i = 0; i < 60_000; i++)
            {
                counts[source.GetInt32(6)]++;
            }

            Assert.All(counts, c => Assert.InRange(c, 9_000, 11_000));
        }
    }

    [Fact]
    public void GetBytes_returns_the_count_asked_for()
    {
        var random = new InsecureDeterministicRandom(0);

        Assert.Empty(random.GetBytes(0));
        Assert.Equal(33, random.GetBytes(33).Length);
    }

    /// <summary>Hands out exactly the bytes it was given, and fails loudly past the end.</summary>
    private sealed class ScriptedSource(byte[] script) : IRandomSource
    {
        public int Consumed { get; private set; }

        public void Fill(Span<byte> destination)
        {
            if (Consumed + destination.Length > script.Length)
            {
                throw new InvalidOperationException("The script ran out.");
            }

            script.AsSpan(Consumed, destination.Length).CopyTo(destination);
            Consumed += destination.Length;
        }
    }
}
