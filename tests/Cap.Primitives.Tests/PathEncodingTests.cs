using System.Text;

namespace Cap.Primitives.Tests;

/// <summary>
/// The byte/character conversion, whose one job is never to lose a filename.
/// </summary>
/// <remarks>
/// A Unix filename is bytes, and nothing obliges those bytes to be UTF-8. A decoder that
/// substitutes a replacement character for the ones that are not produces a string that no
/// longer encodes back to the name it came from, which means the file cannot be reopened
/// and its whole directory cannot be usefully enumerated. The round-trip property below is
/// the entire point of the type; everything else is detail.
/// </remarks>
public sealed class PathEncodingTests
{
    [Theory]
    [InlineData("")]
    [InlineData("foo.txt")]
    [InlineData("café")]
    [InlineData("café")]
    [InlineData("日本語")]
    [InlineData("😀")]
    [InlineData("a\u0001b")]
    public void Valid_text_matches_ordinary_utf8(string text)
    {
        byte[] expected = Encoding.UTF8.GetBytes(text);

        Assert.Equal(expected, Encode(text));
        Assert.Equal(text, PathEncoding.GetString(expected));
    }

    /// <summary>
    /// The property that matters: every byte sequence, valid UTF-8 or not, survives a trip
    /// through a string unchanged.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0x80 })]
    [InlineData(new byte[] { 0xC3 })]
    [InlineData(new byte[] { 0x66, 0x6F, 0x6F, 0xFF, 0x2E, 0x74, 0x78, 0x74 })]
    [InlineData(new byte[] { 0xFE, 0xFF, 0xFE, 0xFF })]
    [InlineData(new byte[] { 0xC3, 0xA9, 0xFF, 0xC3, 0xA9 })]
    public void Undecodable_bytes_round_trip(byte[] bytes)
    {
        string decoded = PathEncoding.GetString(bytes);

        Assert.Equal(bytes, Encode(decoded));
    }

    /// <summary>
    /// Every single byte, and every two-byte sequence beginning with a byte that cannot
    /// start a valid sequence. Exhaustive because it is cheap to be, and because the
    /// boundaries between "lead byte", "continuation" and "neither" are exactly where a
    /// hand-written decoder goes wrong.
    /// </summary>
    [Fact]
    public void Every_byte_value_round_trips()
    {
        for (int first = 0; first <= 0xFF; first++)
        {
            byte[] single = [(byte)first];
            Assert.Equal(single, Encode(PathEncoding.GetString(single)));

            for (int second = 0; second <= 0xFF; second++)
            {
                byte[] pair = [(byte)first, (byte)second];
                Assert.Equal(pair, Encode(PathEncoding.GetString(pair)));
            }
        }
    }

    /// <summary>
    /// Sequences that decode to a value they have no business decoding to. Each is refused
    /// and escaped byte by byte instead, which keeps it distinct from the well-formed
    /// spelling of the same character -- two byte sequences that decoded to one string
    /// would be two names for one file, and an alias is what a containment check gets
    /// walked past on.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0xC0, 0xAF })]                     // overlong '/'
    [InlineData(new byte[] { 0xE0, 0x80, 0xAF })]               // overlong again
    [InlineData(new byte[] { 0xF0, 0x80, 0x80, 0xAF })]         // and again
    [InlineData(new byte[] { 0xC1, 0xBF })]                     // overlong U+007F
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]               // encoded surrogate U+D800
    [InlineData(new byte[] { 0xED, 0xBF, 0xBF })]               // encoded surrogate U+DFFF
    [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 })]         // above U+10FFFF
    [InlineData(new byte[] { 0xF5, 0x80, 0x80, 0x80 })]         // lead byte out of range
    public void Ill_formed_sequences_are_escaped_not_decoded(byte[] bytes)
    {
        string decoded = PathEncoding.GetString(bytes);

        Assert.Equal(bytes.Length, decoded.Length);
        Assert.All(decoded.ToCharArray(), c => Assert.InRange(c, '\udc80', '\udcff'));
        Assert.Equal(bytes, Encode(decoded));
    }

    /// <summary>
    /// A truncated sequence at the end of the input is ill-formed like any other, and must
    /// not be treated as a decode that simply ran out of room.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0xC3 })]
    [InlineData(new byte[] { 0xE6, 0x97 })]
    [InlineData(new byte[] { 0xF0, 0x9F, 0x98 })]
    public void Truncated_sequences_are_escaped(byte[] bytes)
    {
        Assert.Equal(bytes, Encode(PathEncoding.GetString(bytes)));
    }

    /// <summary>
    /// A well-formed surrogate pair wins over the escape range, so a low surrogate that
    /// happens to sit in <c>U+DC80</c>–<c>U+DCFF</c> is still read as half of its pair.
    /// Reading it as an escaped byte instead would silently mangle every character from
    /// <c>U+10080</c> to <c>U+100FF</c>.
    /// </summary>
    [Fact]
    public void A_surrogate_pair_is_not_mistaken_for_an_escape()
    {
        string text = char.ConvertFromUtf32(0x10080);

        Assert.Equal('\udc80', text[1]);
        Assert.Equal(Encoding.UTF8.GetBytes(text), Encode(text));
    }

    /// <summary>
    /// A lone surrogate outside the escape range came from no byte sequence, so there is
    /// none to give back. Inventing one would be the lossy behaviour this type exists to
    /// avoid, in the other direction.
    /// </summary>
    [Fact]
    public void A_lone_surrogate_outside_the_escape_range_is_refused()
    {
        // Built here rather than supplied as theory data. A lone surrogate does not survive
        // being written out and read back again, which is the same round-trip failure this
        // type exists to prevent -- passing one in as a parameter silently turns it into
        // replacement characters before the test ever sees it.
        string[] malformed = ["\ud800", "a\udfffb", "\udc00", "\udc7f"];

        foreach (string text in malformed)
        {
            Assert.Equal(-1, PathEncoding.GetByteCount(text));
            Assert.False(PathEncoding.TryGetBytes(text, new byte[64], out _));
        }
    }

    /// <summary>A buffer that is too small fails rather than writing a truncated name.</summary>
    [Fact]
    public void A_short_buffer_is_refused()
    {
        const string Text = "café";

        Assert.Equal(5, PathEncoding.GetByteCount(Text));
        Assert.False(PathEncoding.TryGetBytes(Text, new byte[4], out int written));
        Assert.Equal(0, written);
        Assert.True(PathEncoding.TryGetBytes(Text, new byte[5], out written));
        Assert.Equal(5, written);

        Assert.False(PathEncoding.TryGetChars(Encoding.UTF8.GetBytes(Text), new char[3], out _));
    }

    /// <summary>
    /// Long names take a different path through the decoder than short ones, and that seam
    /// is worth crossing on purpose.
    /// </summary>
    [Fact]
    public void Long_input_round_trips()
    {
        byte[] bytes = new byte[4096];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 256);
        }

        Assert.Equal(bytes, Encode(PathEncoding.GetString(bytes)));
    }

    private static byte[] Encode(string text)
    {
        int count = PathEncoding.GetByteCount(text);
        Assert.InRange(count, 0, int.MaxValue);

        byte[] buffer = new byte[count];
        Assert.True(PathEncoding.TryGetBytes(text, buffer, out int written));
        Assert.Equal(count, written);

        return buffer;
    }
}
