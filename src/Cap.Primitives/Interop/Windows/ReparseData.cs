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

    /// <summary>
    /// An application execution alias: the zero-length stub the package manager puts on the
    /// search path so that typing a package's name launches it.
    /// </summary>
    /// <remarks>
    /// Named here although it is refused, because refusing it for the right reason matters.
    /// Its data is a sequence of counted strings — a package family name, an application
    /// identifier, a target executable — and the offsets a link's data carries are not where
    /// this structure keeps anything. A reader that assumed every reparse point held a link
    /// would take whichever of those strings happened to land at the offset it expected and
    /// use it as a path.
    /// </remarks>
    public const uint AppExecLink = 0x8000001B;

    /// <summary>
    /// A Windows Container Isolation link, which redirects a file inside a container to a
    /// copy held on the host.
    /// </summary>
    /// <remarks>
    /// Acted on by a filter driver rather than by the filesystem, and meaningful only to
    /// that driver. Whatever it points at is outside anything a directory handle in this
    /// process confers authority over, so there is nothing a sandbox could usefully do with
    /// it but refuse it.
    /// </remarks>
    public const uint WciLink = 0x80000018;

    /// <summary>
    /// True when the tag describes something that can be read as a path.
    /// </summary>
    /// <remarks>
    /// An allowlist of two, and deliberately not a blocklist. Reparse tags are an extension
    /// mechanism: new ones appear with new Windows features, and a reader that refused the
    /// ones it knew to be dangerous would treat every tag invented after it was written as a
    /// link. The failure mode of getting this wrong is reading a structure of unknown shape
    /// as a destination, so the only safe default is to refuse what is not recognised.
    /// </remarks>
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

    /// <summary>Offset of the print name's position, relative to the start of the structure.</summary>
    private const int PrintNameOffsetOffset = 12;

    private const int PrintNameLengthOffset = 14;

    /// <summary>
    /// The terminator written after each stored name, which no declared length counts.
    /// </summary>
    private const int TerminatorBytes = sizeof(char);

    /// <summary>Where the characters start for a symbolic link: after four offsets and a flags word.</summary>
    private const int SymbolicLinkPathOffset = 20;

    /// <summary>Where the characters start for a junction: after four offsets, with no flags word.</summary>
    private const int MountPointPathOffset = 16;

    /// <summary>Offset of a symbolic link's flags word, relative to the start of the path buffer.</summary>
    private const int SymbolicLinkFlagsOffset = 16;

    /// <summary>
    /// The one flag defined for a symbolic link: its target is to be resolved from the
    /// directory holding the link rather than from a filesystem root.
    /// </summary>
    private const uint SymbolicLinkFlagRelative = 0x00000001;

    /// <summary>
    /// The prefix that makes a rooted path a name the object manager resolves.
    /// </summary>
    /// <remarks>
    /// What the system's own link-creating call stores for a target that is not relative.
    /// The displayed name keeps the caller's spelling; this is what the filesystem acts on.
    /// </remarks>
    public const string ObjectManagerPrefix = @"\??\";

    /// <summary>
    /// How many bytes a symbolic link's data occupies for a given target.
    /// </summary>
    public static int SymbolicLinkSize(ReadOnlySpan<char> target, bool rooted) =>
        SymbolicLinkPathOffset +
        (SubstituteChars(target, rooted) * sizeof(char)) + TerminatorBytes +
        (target.Length * sizeof(char)) + TerminatorBytes;

    /// <summary>
    /// Builds the structure that creates a symbolic link with the given stored target.
    /// </summary>
    /// <param name="target">The target as the caller wrote it.</param>
    /// <param name="rooted">
    /// Whether the target names a location from a root rather than from the directory
    /// holding the link. The filesystem acts on this rather than on the spelling, which is
    /// why it is decided by the same path parser resolution uses and passed in rather than
    /// guessed from the characters here.
    /// </param>
    /// <param name="destination">
    /// Where to write, at least <see cref="SymbolicLinkSize"/> bytes long.
    /// </param>
    /// <param name="written">How much of <paramref name="destination"/> was used.</param>
    /// <remarks>
    /// <para>
    /// Written here, beside the reader, so that the two cannot drift apart about where the
    /// names live. The offsets in this structure are the part a mistake hides in: everything
    /// keeps working with a wrong one until something reads the link back, and on the
    /// platform this runs on there is no way to find out except by trying it.
    /// </para>
    /// <para>
    /// The substitute name is the one the filesystem resolves and the print name the one a
    /// reader is shown. They are the same characters for a relative target; for a rooted one
    /// the substitute name is spelled in the object manager's syntax and the print name keeps
    /// the caller's spelling, which is what the system's own call stores. Each is followed by
    /// a terminator that its declared length does not count, again to match.
    /// </para>
    /// </remarks>
    public static bool TryBuildSymbolicLink(
        ReadOnlySpan<char> target,
        bool rooted,
        Span<byte> destination,
        out int written)
    {
        written = 0;

        int substituteBytes = SubstituteChars(target, rooted) * sizeof(char);
        int printBytes = target.Length * sizeof(char);
        int printOffset = substituteBytes + TerminatorBytes;
        int total = SymbolicLinkPathOffset + printOffset + printBytes + TerminatorBytes;

        if (total > MaximumBufferSize || total > destination.Length ||
            substituteBytes > ushort.MaxValue || printOffset > ushort.MaxValue)
        {
            return false;
        }

        Span<byte> structure = destination[..total];
        structure.Clear();

        BinaryPrimitives.WriteUInt32LittleEndian(structure, ReparseTags.SymbolicLink);
        BinaryPrimitives.WriteUInt16LittleEndian(structure[DataLengthOffset..], (ushort)(total - HeaderSize));
        BinaryPrimitives.WriteUInt16LittleEndian(structure[SubstituteNameOffsetOffset..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(structure[SubstituteNameLengthOffset..], (ushort)substituteBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(structure[PrintNameOffsetOffset..], (ushort)printOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(structure[PrintNameLengthOffset..], (ushort)printBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(
            structure[SymbolicLinkFlagsOffset..], rooted ? 0 : SymbolicLinkFlagRelative);

        Span<byte> substitute = structure.Slice(SymbolicLinkPathOffset, substituteBytes);
        if (rooted)
        {
            System.Text.Encoding.Unicode.GetBytes(ObjectManagerPrefix, substitute);
            System.Text.Encoding.Unicode.GetBytes(
                target, substitute[(ObjectManagerPrefix.Length * sizeof(char))..]);
        }
        else
        {
            System.Text.Encoding.Unicode.GetBytes(target, substitute);
        }

        System.Text.Encoding.Unicode.GetBytes(
            target, structure[(SymbolicLinkPathOffset + printOffset)..]);

        written = total;
        return true;
    }

    private static int SubstituteChars(ReadOnlySpan<char> target, bool rooted) =>
        rooted ? ObjectManagerPrefix.Length + target.Length : target.Length;

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
    /// Reads the stored target of a link, and whether the filesystem will resolve it from the
    /// directory holding the link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The substitute name is returned, not the print name. The print name is a display
    /// convenience the filesystem does not use, and the two need not agree — a link whose
    /// print name says one thing and whose substitute name says another resolves to the
    /// substitute name, so that is the only one worth checking.
    /// </para>
    /// <para>
    /// <paramref name="isRelative"/> comes from the structure's own flag and not from how the
    /// stored name is spelled, because the flag is what the filesystem acts on. A junction
    /// has no such flag and is never relative: its target is recorded as a path from a volume
    /// root, which is the reason a junction cannot be followed while staying beneath a
    /// directory handle.
    /// </para>
    /// </remarks>
    public static bool TryReadTarget(ReadOnlySpan<byte> buffer, out string target, out bool isRelative)
    {
        target = string.Empty;
        isRelative = false;

        if (!TryReadTag(buffer, out uint tag) || !ReparseTags.IsFilesystemLink(tag))
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

        if (tag == ReparseTags.SymbolicLink)
        {
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[(SymbolicLinkFlagsOffset - HeaderSize)..]);
            isRelative = (flags & SymbolicLinkFlagRelative) != 0;
        }

        ReadOnlySpan<byte> nameBytes = data.Slice((int)start, nameLength);
        target = System.Text.Encoding.Unicode.GetString(nameBytes);
        return true;
    }
}
