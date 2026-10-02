using Cap.Primitives;

namespace Cap.Rand.Tests;

/// <summary>
/// The helpers: their bounds, their rejection of out-of-range draws, and how many bytes they
/// consume doing it.
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

    [Fact]
    public void GetDouble_uses_the_top_53_bits()
    {
        var zero = new ScriptedSource([0, 0, 0, 0, 0, 0, 0, 0]);
        var ones = new ScriptedSource([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

        // The low eleven bits are dropped, so only the top bit of the last byte counts.
        var lowBitsOnly = new ScriptedSource([0xFF, 0x07, 0, 0, 0, 0, 0, 0]);
        var topBitOnly = new ScriptedSource([0, 0, 0, 0, 0, 0, 0, 0x80]);

        Assert.Equal(0.0, zero.GetDouble());
        Assert.Equal(1.0 - Math.Pow(2, -53), ones.GetDouble());
        Assert.Equal(0.0, lowBitsOnly.GetDouble());
        Assert.Equal(0.5, topBitOnly.GetDouble());
        Assert.Equal(8, ones.Consumed);
    }

    [Fact]
    public void GetDouble_stays_below_one()
    {
        var random = new InsecureDeterministicRandom(2024);

        for (int i = 0; i < 10_000; i++)
        {
            Assert.InRange(random.GetDouble(), 0.0, Math.BitDecrement(1.0));
        }
    }

    [Fact]
    public void Shuffle_is_a_permutation()
    {
        int[] values = Enumerable.Range(1, 100).ToArray();

        new InsecureDeterministicRandom(7).Shuffle(values.AsSpan());

        Assert.NotEqual(Enumerable.Range(1, 100), values);
        Assert.Equal(Enumerable.Range(1, 100), values.Order());
    }

    [Fact]
    public void Shuffle_of_fewer_than_two_elements_draws_nothing()
    {
        var source = new ScriptedSource([]);

        source.Shuffle(Span<int>.Empty);
        source.Shuffle(new[] { 42 }.AsSpan());

        Assert.Equal(0, source.Consumed);
    }

    /// <summary>
    /// Index <c>i</c> is swapped with <c>GetInt32(i + 1)</c>, last index first. With three
    /// elements that is a draw over three values, then a draw over two.
    /// </summary>
    [Fact]
    public void Shuffle_swaps_each_index_from_the_last_with_a_draw_up_to_it()
    {
        var source = new ScriptedSource(
        [
            0, 0, 0, 0, // i = 2, j = 0: [c, b, a]
            1, 0, 0, 0, // i = 1, j = 1: unchanged
        ]);
        char[] values = ['a', 'b', 'c'];

        source.Shuffle(values.AsSpan());

        Assert.Equal(['c', 'b', 'a'], values);
        Assert.Equal(8, source.Consumed);
    }

    /// <summary>
    /// Each of the six orders of three elements turns up about equally often. A shuffle that
    /// draws over the whole length at every step, the usual mistake, makes some orders
    /// noticeably more likely than others and fails this.
    /// </summary>
    [Fact]
    public void Every_order_of_three_elements_is_reached_about_equally()
    {
        var random = new InsecureDeterministicRandom(2024);
        var counts = new Dictionary<string, int>();

        for (int i = 0; i < 60_000; i++)
        {
            char[] values = ['a', 'b', 'c'];
            random.Shuffle(values.AsSpan());
            string order = new(values);
            counts[order] = counts.GetValueOrDefault(order) + 1;
        }

        Assert.Equal(6, counts.Count);
        Assert.All(counts.Values, c => Assert.InRange(c, 9_000, 11_000));
    }

    [Fact]
    public void A_null_source_is_refused()
    {
        IRandomSource source = null!;

        Assert.Throws<ArgumentNullException>(() => source.GetDouble());
        Assert.Throws<ArgumentNullException>(() => source.Shuffle(new[] { 1, 2 }.AsSpan()));
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
