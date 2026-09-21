using System.Buffers;
using System.Diagnostics;

namespace Cap.Primitives;

/// <summary>
/// Converts between the UTF-16 characters a .NET caller supplies and the bytes a Unix kernel
/// actually stores, without ever losing a filename.
/// </summary>
/// <remarks>
/// <para>
/// A Unix filename is a sequence of bytes. The kernel requires only that it contain no
/// <c>/</c> and no <c>U+0000</c>; it is under no obligation to be UTF-8, and on a filesystem
/// that has been around long enough some names are not. A decoder that replaces invalid
/// bytes with <c>U+FFFD</c> makes those names unnameable — the string it hands back does not
/// encode to the bytes it came from, so the file it names cannot be opened again. One bad
/// filename then makes its whole directory impossible to enumerate usefully.
/// </para>
/// <para>
/// The fix is the scheme Python adopted for the same problem: a byte that cannot begin a
/// valid UTF-8 sequence becomes a lone low surrogate in the range <c>U+DC80</c>–<c>U+DCFF</c>,
/// carrying its value in the low eight bits. Encoding reverses this exactly. The result is a
/// round trip — <c>bytes → chars → bytes</c> returns the original bytes for every possible
/// input — at the cost of strings that are not well-formed UTF-16 and must not be written
/// anywhere that assumes they are.
/// </para>
/// <para>
/// The escape range cannot collide with real text. <c>U+DC80</c>–<c>U+DCFF</c> are surrogate
/// code points, which are not characters and cannot appear alone in well-formed UTF-16, so
/// an escaped byte is always distinguishable from content that decoded successfully.
/// </para>
/// </remarks>
internal static class PathEncoding
{
    /// <summary>Lowest lone surrogate used to carry an undecodable byte.</summary>
    private const char EscapeBase = '\udc00';

    /// <summary>
    /// Number of bytes <paramref name="chars"/> encodes to, or <c>-1</c> if it cannot be
    /// encoded — which happens only for a lone surrogate outside the escape range, meaning
    /// the string was already malformed before it reached this library.
    /// </summary>
    public static int GetByteCount(ReadOnlySpan<char> chars)
    {
        int total = 0;
        int i = 0;
        while (i < chars.Length)
        {
            if (!ClassifyNext(chars, i, out _, out int consumed, out int bytes))
            {
                return -1;
            }

            total += bytes;
            i += consumed;
        }

        return total;
    }

    /// <summary>
    /// Encodes <paramref name="chars"/> into <paramref name="destination"/>. Returns
    /// <see langword="false"/> if the buffer is too small or the input contains a lone
    /// surrogate outside the escape range.
    /// </summary>
    public static bool TryGetBytes(ReadOnlySpan<char> chars, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        int i = 0;
        while (i < chars.Length)
        {
            if (!ClassifyNext(chars, i, out int scalar, out int consumed, out int bytes))
            {
                bytesWritten = 0;
                return false;
            }

            if (destination.Length - bytesWritten < bytes)
            {
                bytesWritten = 0;
                return false;
            }

            Span<byte> at = destination[bytesWritten..];
            if (scalar < 0)
            {
                // An escaped byte: written back verbatim, never re-encoded as UTF-8.
                at[0] = (byte)(chars[i] & 0xFF);
            }
            else
            {
                WriteScalar(scalar, bytes, at);
            }

            bytesWritten += bytes;
            i += consumed;
        }

        return true;
    }

    /// <summary>Number of characters <paramref name="bytes"/> decodes to. Never fails.</summary>
    public static int GetCharCount(ReadOnlySpan<byte> bytes)
    {
        int total = 0;
        int i = 0;
        while (i < bytes.Length)
        {
            if (TryDecodeSequence(bytes[i..], out int scalar, out int consumed))
            {
                total += scalar > 0xFFFF ? 2 : 1;
                i += consumed;
            }
            else
            {
                total++;
                i++;
            }
        }

        return total;
    }

    /// <summary>
    /// Decodes <paramref name="bytes"/> into <paramref name="destination"/>, escaping
    /// anything that is not valid UTF-8. Fails only if the buffer is too small.
    /// </summary>
    public static bool TryGetChars(ReadOnlySpan<byte> bytes, Span<char> destination, out int charsWritten)
    {
        charsWritten = 0;
        int i = 0;
        while (i < bytes.Length)
        {
            if (TryDecodeSequence(bytes[i..], out int scalar, out int consumed))
            {
                int needed = scalar > 0xFFFF ? 2 : 1;
                if (destination.Length - charsWritten < needed)
                {
                    charsWritten = 0;
                    return false;
                }

                if (needed == 1)
                {
                    destination[charsWritten] = (char)scalar;
                }
                else
                {
                    int offset = scalar - 0x10000;
                    destination[charsWritten] = (char)(0xD800 + (offset >> 10));
                    destination[charsWritten + 1] = (char)(0xDC00 + (offset & 0x3FF));
                }

                charsWritten += needed;
                i += consumed;
            }
            else
            {
                if (destination.Length - charsWritten < 1)
                {
                    charsWritten = 0;
                    return false;
                }

                // Only a byte of 0x80 or above can fail to decode, so the escape always
                // lands inside U+DC80..U+DCFF and can never be mistaken for real content.
                destination[charsWritten] = (char)(EscapeBase + bytes[i]);
                charsWritten++;
                i++;
            }
        }

        return true;
    }

    /// <summary>Decodes <paramref name="bytes"/> to a string, escaping undecodable bytes.</summary>
    public static string GetString(ReadOnlySpan<byte> bytes)
    {
        int count = GetCharCount(bytes);
        if (count == 0)
        {
            return string.Empty;
        }

        // A filename is nearly always short enough to decode on the stack. The rented
        // fallback is for the rare long path, and keeps a hostile caller from turning a
        // large-allocation request into a stack overflow.
        char[]? rented = null;
        Span<char> buffer = count <= 256
            ? stackalloc char[256]
            : (rented = ArrayPool<char>.Shared.Rent(count));

        try
        {
            // The length came from the same decoder that is about to run, so the buffer
            // cannot come up short.
            bool decoded = TryGetChars(bytes, buffer, out int written);
            Debug.Assert(decoded, "char count and decode disagreed");
            return new string(buffer[..written]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// Describes the next unit of <paramref name="chars"/> at <paramref name="index"/>:
    /// how many characters it occupies, how many bytes it encodes to, and its scalar value
    /// — or <c>-1</c> for an escaped byte, which is emitted verbatim rather than encoded.
    /// </summary>
    private static bool ClassifyNext(
        ReadOnlySpan<char> chars,
        int index,
        out int scalar,
        out int charsConsumed,
        out int byteCount)
    {
        char c = chars[index];

        // A well-formed surrogate pair is a real character and takes precedence, so a low
        // surrogate that happens to sit in the escape range is still read as half of its
        // pair when it follows a high surrogate.
        if (char.IsHighSurrogate(c) && index + 1 < chars.Length && char.IsLowSurrogate(chars[index + 1]))
        {
            scalar = char.ConvertToUtf32(c, chars[index + 1]);
            charsConsumed = 2;
            byteCount = 4;
            return true;
        }

        if (c is >= '\udc80' and <= '\udcff')
        {
            scalar = -1;
            charsConsumed = 1;
            byteCount = 1;
            return true;
        }

        if (char.IsSurrogate(c))
        {
            // A lone surrogate that is not one of ours. There is no byte sequence it could
            // have come from, so encoding it would have to invent one.
            scalar = 0;
            charsConsumed = 0;
            byteCount = 0;
            return false;
        }

        scalar = c;
        charsConsumed = 1;
        byteCount = c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
        return true;
    }

    private static void WriteScalar(int scalar, int byteCount, Span<byte> destination)
    {
        switch (byteCount)
        {
            case 1:
                destination[0] = (byte)scalar;
                break;
            case 2:
                destination[0] = (byte)(0xC0 | (scalar >> 6));
                destination[1] = (byte)(0x80 | (scalar & 0x3F));
                break;
            case 3:
                destination[0] = (byte)(0xE0 | (scalar >> 12));
                destination[1] = (byte)(0x80 | ((scalar >> 6) & 0x3F));
                destination[2] = (byte)(0x80 | (scalar & 0x3F));
                break;
            default:
                destination[0] = (byte)(0xF0 | (scalar >> 18));
                destination[1] = (byte)(0x80 | ((scalar >> 12) & 0x3F));
                destination[2] = (byte)(0x80 | ((scalar >> 6) & 0x3F));
                destination[3] = (byte)(0x80 | (scalar & 0x3F));
                break;
        }
    }

    /// <summary>
    /// Reads one UTF-8 sequence from the front of <paramref name="bytes"/>.
    /// </summary>
    /// <remarks>
    /// Strict on purpose. Overlong encodings, encoded surrogates and values above
    /// <c>U+10FFFF</c> are all rejected rather than decoded generously, because two byte
    /// sequences that decode to the same string are two names for one file — the alias is
    /// precisely what a containment check can be walked past. Rejecting them here means each
    /// is escaped byte by byte instead, which keeps them distinct and still round-trips.
    /// </remarks>
    private static bool TryDecodeSequence(ReadOnlySpan<byte> bytes, out int scalar, out int consumed)
    {
        scalar = 0;
        consumed = 0;

        byte lead = bytes[0];
        if (lead < 0x80)
        {
            scalar = lead;
            consumed = 1;
            return true;
        }

        int length;
        int value;
        byte lowestSecond = 0x80;
        byte highestSecond = 0xBF;

        switch (lead)
        {
            // 0x80..0xC1 are continuation bytes or the lead of an overlong two-byte form.
            case >= 0xC2 and <= 0xDF:
                length = 2;
                value = lead & 0x1F;
                break;
            case >= 0xE0 and <= 0xEF:
                length = 3;
                value = lead & 0x0F;
                // E0 80.. would be overlong; ED A0.. would encode a surrogate.
                if (lead == 0xE0) { lowestSecond = 0xA0; }
                if (lead == 0xED) { highestSecond = 0x9F; }
                break;
            case >= 0xF0 and <= 0xF4:
                length = 4;
                value = lead & 0x07;
                // F0 80.. would be overlong; F4 90.. would exceed U+10FFFF.
                if (lead == 0xF0) { lowestSecond = 0x90; }
                if (lead == 0xF4) { highestSecond = 0x8F; }
                break;
            default:
                return false;
        }

        if (bytes.Length < length)
        {
            return false;
        }

        byte second = bytes[1];
        if (second < lowestSecond || second > highestSecond)
        {
            return false;
        }

        value = (value << 6) | (second & 0x3F);
        for (int i = 2; i < length; i++)
        {
            byte b = bytes[i];
            if ((b & 0xC0) != 0x80)
            {
                return false;
            }

            value = (value << 6) | (b & 0x3F);
        }

        scalar = value;
        consumed = length;
        return true;
    }
}
