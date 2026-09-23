using System.Buffers.Binary;
using System.Text;
using Cap.Primitives.Interop.Windows;
using static Cap.Fuzz.Targets.InvariantViolation;

namespace Cap.Fuzz.Targets;

/// <summary>
/// The reader for the structure Windows stores in a reparse point, fed arbitrary bytes.
/// </summary>
/// <remarks>
/// <para>
/// This data comes off the disk, and anything that can create a file inside a sandbox can
/// create a reparse point with whatever header it likes. The reader has to find a name at an
/// offset and a length that the data states about itself, so a buffer that lies about either
/// must be refused rather than read past.
/// </para>
/// <para>
/// A read past the end of the buffer would surface here as an exception, which is itself a
/// finding. That alone is not enough, because a reader can stay inside the bytes it was
/// handed and still read past the data the structure declares, picking up whatever the
/// buffer held beyond it. So the verdict is also compared with an independent reading of
/// the documented layout, and the bytes beyond the declared data are changed to show that
/// nothing there was consulted.
/// </para>
/// <para>
/// The input is the buffer, exactly as the filesystem would have returned it.
/// </para>
/// </remarks>
internal static class ReparseDataTarget
{
    public const string Name = "reparse-data";

    private const int HeaderSize = 8;

    public static void Run(ReadOnlySpan<byte> data) => Check(data);

    public static void Check(ReadOnlySpan<byte> buffer)
    {
        bool hasTag = ReparseData.TryReadTag(buffer, out uint tag);
        Require(hasTag == buffer.Length >= HeaderSize, $"A buffer of {buffer.Length} bytes was misjudged as {(hasTag ? "holding" : "lacking")} a header.");
        if (hasTag)
        {
            Require(tag == BinaryPrimitives.ReadUInt32LittleEndian(buffer), "The tag was not read from the start of the header.");
        }

        (bool read, string target, bool isRelative) = Read(buffer);
        (bool expectedRead, string expectedTarget, bool expectedRelative) = Expected(buffer);

        Require(
            read == expectedRead,
            $"A buffer of {buffer.Length} bytes was {(read ? "read" : "refused")}, but its layout says it should be {(expectedRead ? "read" : "refused")}.");

        if (!read)
        {
            Require(target.Length == 0 && !isRelative, "A refused buffer still produced a target.");
            return;
        }

        Require(target == expectedTarget, $"The target read was {Show(target)}, but the layout places {Show(expectedTarget)} there.");
        Require(isRelative == expectedRelative, "The target's relativity was not read from its flag.");

        // Nothing beyond the data the structure declares may be consulted. Change every byte
        // there, and extend the buffer with more, and the answer must not move.
        int declaredEnd = HeaderSize + BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..]);
        byte[] altered = new byte[buffer.Length + 64];
        buffer.CopyTo(altered);
        for (int i = declaredEnd; i < altered.Length; i++)
        {
            altered[i] = (byte)~altered[i];
        }

        foreach (int length in new[] { buffer.Length, altered.Length })
        {
            (bool readAgain, string targetAgain, bool relativeAgain) = Read(altered.AsSpan(0, length));
            Require(
                readAgain && targetAgain == target && relativeAgain == isRelative,
                "The bytes after the declared data changed what was read.");
        }
    }

    private static (bool Read, string Target, bool IsRelative) Read(ReadOnlySpan<byte> buffer)
    {
        bool read = ReparseData.TryReadTarget(buffer, out string target, out bool isRelative);
        return (read, target, isRelative);
    }

    /// <summary>
    /// Reads the buffer from its documented layout, as plainly as it can be written.
    /// </summary>
    /// <remarks>
    /// The header is a four-byte tag, a two-byte length of the data after the header, and two
    /// reserved bytes. For both link kinds the data opens with four two-byte fields — the
    /// substitute name's offset and length, then the print name's — measured in bytes from the
    /// start of the character area. A symbolic link has a four-byte flags word before that
    /// area and a junction does not. Only those two tags describe a link at all.
    /// </remarks>
    private static (bool Read, string Target, bool IsRelative) Expected(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize)
        {
            return (false, string.Empty, false);
        }

        uint tag = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        bool symbolicLink = tag == 0xA000000C;
        bool junction = tag == 0xA0000003;
        if (!symbolicLink && !junction)
        {
            return (false, string.Empty, false);
        }

        int dataLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..]);
        if (HeaderSize + dataLength > buffer.Length)
        {
            return (false, string.Empty, false);
        }

        byte[] data = buffer.Slice(HeaderSize, dataLength).ToArray();
        int characterArea = symbolicLink ? 12 : 8;
        if (data.Length < characterArea)
        {
            return (false, string.Empty, false);
        }

        int offset = data[0] | (data[1] << 8);
        int length = data[2] | (data[3] << 8);
        if (length % 2 != 0 || characterArea + offset + length > data.Length)
        {
            return (false, string.Empty, false);
        }

        bool relative = symbolicLink && (data[8] & 1) != 0;
        return (true, Encoding.Unicode.GetString(data, characterArea + offset, length), relative);
    }
}
