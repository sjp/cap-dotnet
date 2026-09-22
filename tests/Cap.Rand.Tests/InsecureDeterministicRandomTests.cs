namespace Cap.Rand.Tests;

/// <summary>
/// The seeded sequence: that it is the documented algorithm, and that it will not change.
/// </summary>
/// <remarks>
/// The expected values were produced by a separate implementation of SplitMix64 and
/// xoshiro256** written from the published reference code, not by running this one. If any
/// of them changes, every seed recorded in a consumer's tests changes meaning with it, so a
/// failure here is a broken promise rather than a test to update.
/// </remarks>
public sealed class InsecureDeterministicRandomTests
{
    [Fact]
    public void Seed_zero_produces_the_reference_stream()
    {
        var random = new InsecureDeterministicRandom(0);

        Assert.Equal(
            Convert.FromHexString("b4f275cb365fec992a455649781f6ebfe0e63349"),
            random.GetBytes(20));
    }

    [Fact]
    public void Seed_forty_two_produces_the_reference_stream()
    {
        var random = new InsecureDeterministicRandom(42);

        Assert.Equal(
            Convert.FromHexString("16c72e0c2e0b78157e3a116d86d90461a199e439"),
            random.GetBytes(20));
    }

    /// <summary>
    /// The first 64-bit outputs of xoshiro256** seeded from SplitMix64 at 42, read back as
    /// little-endian words, which is how the stream is defined.
    /// </summary>
    [Fact]
    public void The_stream_is_the_generator_outputs_in_little_endian_order()
    {
        var random = new InsecureDeterministicRandom(42);
        byte[] bytes = random.GetBytes(32);

        ulong[] words = Enumerable.Range(0, 4)
            .Select(i => BitConverter.ToUInt64(bytes, i * 8))
            .ToArray();

        if (!BitConverter.IsLittleEndian)
        {
            words = words.Select(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness).ToArray();
        }

        Assert.Equal(
            [0x15780b2e0c2ec716UL, 0x6104d9866d113a7eUL, 0xae17533239e499a1UL, 0xecb8ad4703b360a1UL],
            words);
    }

    /// <summary>
    /// How the calls divide the stream does not change the stream. Every split of 40 bytes
    /// into uneven pieces, including ones that straddle the 8-byte output boundary, gives the
    /// same bytes as one call.
    /// </summary>
    [Theory]
    [InlineData(new[] { 40 })]
    [InlineData(new[] { 3, 5, 32 })]
    [InlineData(new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 31 })]
    [InlineData(new[] { 7, 9, 0, 17, 7 })]
    [InlineData(new[] { 13, 13, 13, 1 })]
    public void The_stream_does_not_depend_on_how_it_is_read(int[] pieces)
    {
        byte[] whole = new InsecureDeterministicRandom(7).GetBytes(40);

        var random = new InsecureDeterministicRandom(7);
        byte[] assembled = pieces.SelectMany(n => random.GetBytes(n)).ToArray();

        Assert.Equal(whole, assembled);
    }

    [Fact]
    public void The_same_seed_gives_the_same_bytes()
    {
        Assert.Equal(
            new InsecureDeterministicRandom(123456789).GetBytes(256),
            new InsecureDeterministicRandom(123456789).GetBytes(256));
    }

    [Fact]
    public void Different_seeds_give_different_bytes()
    {
        Assert.NotEqual(
            new InsecureDeterministicRandom(1).GetBytes(32),
            new InsecureDeterministicRandom(2).GetBytes(32));
    }

    /// <summary>
    /// The integer helpers are part of the same promise: the byte-level method is specified,
    /// so the integers a seed gives are fixed too.
    /// </summary>
    [Fact]
    public void Int32_helper_gives_the_reference_values()
    {
        var random = new InsecureDeterministicRandom(42);

        int[] drawn = Enumerable.Range(0, 8).Select(_ => random.GetInt32(0, 10)).ToArray();

        Assert.Equal([6, 6, 1, 2, 1, 7, 4, 8], drawn);
    }

    [Fact]
    public void Int64_helper_gives_the_reference_values()
    {
        var random = new InsecureDeterministicRandom(42);
        const long From = -(1L << 62);
        const long To = (1L << 62) + 12345;

        long[] drawn = Enumerable.Range(0, 4).Select(_ => random.GetInt64(From, To)).ToArray();

        Assert.Equal(
            [-3064687254024829162L, 2379265674537155198L, 750372260756293989L, 1317312123653859138L],
            drawn);
    }

    /// <summary>
    /// The reason for the interface: a component that does not need security takes an
    /// <see cref="IRandomSource"/>, and a test gets the same run every time by handing it a
    /// seed.
    /// </summary>
    [Fact]
    public void A_component_taking_the_interface_is_reproducible_under_a_seed()
    {
        int[] first = new Jitter(new InsecureDeterministicRandom(99)).Delays(10);
        int[] second = new Jitter(new InsecureDeterministicRandom(99)).Delays(10);

        Assert.Equal(first, second);
        Assert.All(first, d => Assert.InRange(d, 100, 199));
    }

    /// <summary>Written the way a consumer would write one.</summary>
    private sealed class Jitter(IRandomSource random)
    {
        public int[] Delays(int count) =>
            Enumerable.Range(0, count).Select(_ => random.GetInt32(100, 200)).ToArray();
    }
}
