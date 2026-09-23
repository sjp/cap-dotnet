using System.Buffers.Binary;
using System.Text;

namespace Cap.Fuzz.Targets;

/// <summary>
/// Reads structured values off the front of a fuzzer's byte string.
/// </summary>
/// <remarks>
/// <para>
/// Every read succeeds. Past the end of the input a read returns zero, so every byte string
/// decodes to some input and none is thrown away. A decoder that refused short or odd inputs
/// would spend most of the fuzzer's time on inputs that test nothing.
/// </para>
/// <para>
/// Small counts and choices are read a byte at a time and reduced by modulo rather than
/// rejected when out of range, for the same reason. A single mutated byte then moves the
/// input to a neighbouring one rather than to a refused one.
/// </para>
/// </remarks>
internal ref struct FuzzInput
{
    private ReadOnlySpan<byte> _remaining;

    public FuzzInput(ReadOnlySpan<byte> data) => _remaining = data;

    /// <summary>Whether every byte has been read.</summary>
    public readonly bool IsEmpty => _remaining.IsEmpty;

    /// <summary>The next byte, or zero once the input is used up.</summary>
    public byte NextByte()
    {
        if (_remaining.IsEmpty)
        {
            return 0;
        }

        byte value = _remaining[0];
        _remaining = _remaining[1..];
        return value;
    }

    /// <summary>A choice among <paramref name="count"/> alternatives.</summary>
    public int NextChoice(int count) => NextByte() % count;

    /// <summary>
    /// The next <paramref name="length"/> bytes read as UTF-8, or fewer when the input runs
    /// out.
    /// </summary>
    /// <remarks>
    /// Invalid sequences are replaced rather than refused. The strings built this way are
    /// names in a simulated tree, and a replacement character is as good a name as any.
    /// </remarks>
    public string NextUtf8(int length)
    {
        ReadOnlySpan<byte> bytes = _remaining[..Math.Min(length, _remaining.Length)];
        _remaining = _remaining[bytes.Length..];
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Everything left, read as UTF-16 in little-endian order, with an odd final byte dropped.
    /// </summary>
    /// <remarks>
    /// UTF-16 rather than UTF-8 because a .NET string is a sequence of UTF-16 code units and
    /// need not be well-formed: a lone surrogate is a perfectly good <see cref="string"/>, and
    /// the only way to hand one to the code under test is to build the string from code
    /// units directly. Decoding UTF-8 could never produce one.
    /// </remarks>
    public string RestAsUtf16()
    {
        int length = _remaining.Length / sizeof(char);
        string text = string.Create(length, _remaining.ToArray(), static (chars, bytes) =>
        {
            for (int i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * sizeof(char)));
            }
        });

        _remaining = default;
        return text;
    }
}
