using System.Buffers.Binary;

namespace Cap.Primitives.Interop.Windows;

/// <summary>
/// The reparse tags this library is prepared to interpret.
/// </summary>
/// <remarks>
/// A reparse point is a general extension mechanism, not a link type: the tag says which
/// component owns the data, and only two of them describe something a path walk could
/// meaningfully follow. Everything else — an application execution alias, a container image
/// link, a cloud-storage placeholder, a tag introduced after this was written — is a
/// structure this library does not know the shape of, and reading it as if it held a path
/// would mean interpreting arbitrary bytes as a destination.
/// </remarks>
internal static class ReparseTags
{
    /// <summary>A directory junction. Its target is always absolute, so it can never stay inside a sandbox.</summary>
    public const uint MountPoint = 0xA0000003;

    /// <summary>A symbolic link, which may be relative or absolute.</summary>
    public const uint SymbolicLink = 0xA000000C;

    /// <summary>True when the tag describes something that can be read as a path.</summary>
    public static bool IsFilesystemLink(uint tag) => tag is MountPoint or SymbolicLink;
}

/// <summary>
/// Reads the variable-length structure the filesystem returns for a reparse point.
/// </summary>
/// <remarks>
/// <para>
/// Parsed by hand from the returned bytes rather than through a declared structure, because
/// it does not have a fixed shape: the header is followed by a union whose layout depends on
/// the tag, and the names inside it are at offsets carried in the data itself.
/// </para>
/// <para>
/// Those offsets are filesystem data, which on the inside of a sandbox is attacker-controlled
/// data — anything that can create a file there can create a reparse point with whatever
/// header it likes. So every offset and length is checked against the buffer actually
/// returned rather than against the length the structure claims for itself.
/// </para>
/// </remarks>
internal static class ReparseData
{
    /// <summary>The largest a reparse point's data may be, as the filesystem defines it.</summary>
    public const int MaximumBufferSize = 16 * 1024;

    private const int TagOffset = 0;
    private const int DataLengthOffset = 4;
    private const int HeaderSize = 8;

    /// <summary>Offset of the substitute name, relative to the start of the path buffer.</summary>
    private const int SubstituteNameOffsetOffset = 8;

    private const int SubstituteNameLengthOffset = 10;

    /// <summary>Where the characters start for a symbolic link: after four offsets and a flags word.</summary>
    private const int SymbolicLinkPathOffset = 20;

    /// <summary>Where the characters start for a junction: after four offsets, with no flags word.</summary>
    private const int MountPointPathOffset = 16;

    /// <summary>Reads the tag from a returned buffer.</summary>
    public static bool TryReadTag(ReadOnlySpan<byte> buffer, out uint tag)
    {
        if (buffer.Length < HeaderSize)
        {
            tag = 0;
            return false;
        }

        tag = BinaryPrimitives.ReadUInt32LittleEndian(buffer[TagOffset..]);
        return true;
    }

    /// <summary>
    /// Reads the stored target of a link.
    /// </summary>
    /// <remarks>
    /// The substitute name is returned, not the print name. The print name is a display
    /// convenience the filesystem does not use, and the two need not agree — a link whose
    /// print name says one thing and whose substitute name says another resolves to the
    /// substitute name, so that is the only one worth checking.
    /// </remarks>
    public static bool TryReadTarget(ReadOnlySpan<byte> buffer, out uint tag, out string target)
    {
        target = string.Empty;

        if (!TryReadTag(buffer, out tag) || !ReparseTags.IsFilesystemLink(tag))
        {
            return false;
        }

        int declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer[DataLengthOffset..]);
        if (HeaderSize + declaredLength > buffer.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> data = buffer.Slice(HeaderSize, declaredLength);
        int pathOffset = tag == ReparseTags.SymbolicLink
            ? SymbolicLinkPathOffset - HeaderSize
            : MountPointPathOffset - HeaderSize;

        if (data.Length < pathOffset)
        {
            return false;
        }

        int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(data[(SubstituteNameOffsetOffset - HeaderSize)..]);
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(SubstituteNameLengthOffset - HeaderSize)..]);

        // Lengths are in bytes, and an odd one cannot be a whole number of UTF-16 characters.
        if ((nameLength & 1) != 0)
        {
            return false;
        }

        long start = (long)pathOffset + nameOffset;
        if (start < 0 || start + nameLength > data.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> nameBytes = data.Slice((int)start, nameLength);
        target = System.Text.Encoding.Unicode.GetString(nameBytes);
        return true;
    }
}
