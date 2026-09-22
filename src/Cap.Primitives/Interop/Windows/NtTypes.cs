using System.Runtime.InteropServices;

namespace Cap.Primitives.Interop.Windows;

/// <summary>
/// A counted, not necessarily null-terminated, UTF-16 string as the native API takes one.
/// </summary>
/// <remarks>
/// <para>
/// The length is in <em>bytes</em>, not characters, and is a 16-bit field — so the longest
/// name expressible is 32767 characters and a length computed in characters would address
/// half the string.
/// </para>
/// <para>
/// Counted rather than terminated is the point. Every one of the Windows name manglings this
/// library has to defend against works by exploiting a layer that rewrites a string on the
/// way down: trailing dots and spaces stripped, a device name recognised before an
/// extension, a path re-parsed for a prefix. Passing a counted string to the native API
/// bypasses that rewriting entirely, so the name that is checked is the name that is opened.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct UnicodeString
{
    /// <summary>Length of the string in bytes.</summary>
    public ushort Length;

    /// <summary>Capacity of the buffer in bytes.</summary>
    public ushort MaximumLength;

    /// <summary>Pointer to the characters. Not owned by this structure.</summary>
    public nint Buffer;

    /// <summary>The size the native API expects, checked by the layout tests.</summary>
    public static unsafe int StructSize => sizeof(UnicodeString);
}

/// <summary>
/// The name, and the directory to resolve it against, for a native open.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RootDirectory"/> is what makes containment possible on Windows. With it set,
/// the name is resolved as an entry of that directory rather than as a path from a
/// filesystem root, so an open cannot be redirected to an unrelated part of the volume by
/// anything in the name itself — the same property the Unix "open relative to this
/// descriptor" calls provide.
/// </para>
/// <para>
/// The fields are laid out by the compiler at their natural alignment, which is what the
/// native declaration does too. Writing the padding by hand would bake in the shape of one
/// pointer width and silently produce a differently sized structure on the other.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct ObjectAttributes
{
    /// <summary>Size of this structure in bytes. The native API validates it.</summary>
    public uint Length;

    /// <summary>The directory the name is resolved against.</summary>
    public nint RootDirectory;

    /// <summary>Pointer to a <see cref="UnicodeString"/> holding the name.</summary>
    public nint ObjectName;

    /// <summary>Flags; see <see cref="ObjectAttributeFlags"/>.</summary>
    public uint Attributes;

    /// <summary>Unused here; opens inherit the security of what they open.</summary>
    public nint SecurityDescriptor;

    /// <summary>Unused here.</summary>
    public nint SecurityQualityOfService;

    /// <summary>The size the native API expects, checked by the layout tests.</summary>
    public static unsafe int StructSize => sizeof(ObjectAttributes);
}

/// <summary>Flags for <see cref="ObjectAttributes.Attributes"/>.</summary>
[Flags]
internal enum ObjectAttributeFlags : uint
{
    None = 0,

    /// <summary>
    /// Match the name without regard to case.
    /// </summary>
    /// <remarks>
    /// Set on every open this library issues, because it is what the ordinary Windows API
    /// does and an open that disagreed with it would reach a different file from the one any
    /// other program on the system reaches by that name. It follows that containment on
    /// Windows can never rest on comparing names as strings — two names that differ only in
    /// case are one file.
    /// </remarks>
    CaseInsensitive = 0x40,
}

/// <summary>
/// The result block every native filesystem call writes to.
/// </summary>
/// <remarks>
/// The status is repeated here as well as being returned. The returned value is the one this
/// library reads; the block matters because the call writes to it regardless, and a
/// mis-sized or unwritable one corrupts the stack.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct IoStatusBlock
{
    /// <summary>The status, overlaid with a pointer in the native declaration.</summary>
    public nint Status;

    /// <summary>Operation-specific detail, such as how the open resolved.</summary>
    public nuint Information;

    /// <summary>The size the native API expects, checked by the layout tests.</summary>
    public static unsafe int StructSize => sizeof(IoStatusBlock);
}

/// <summary>The reply to a request for a file's attributes and reparse tag.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FileAttributeTagInformation
{
    /// <summary>The file attribute bits.</summary>
    public uint FileAttributes;

    /// <summary>
    /// The reparse tag, meaningful only when the attributes say this is a reparse point.
    /// </summary>
    public uint ReparseTag;
}

/// <summary>The reply to a request for a file's identity.</summary>
/// <remarks>
/// The identifier is 128 bits because the 64-bit one it replaced is not unique on every
/// filesystem the system supports. Only the low half is carried forward, which is enough to
/// distinguish objects within one volume on the filesystems this runs on, and the volume
/// serial is carried alongside it so that two objects on different volumes never compare
/// equal.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct FileIdInformation
{
    /// <summary>Identifies the volume.</summary>
    public ulong VolumeSerialNumber;

    /// <summary>Low half of the 128-bit file identifier.</summary>
    public ulong FileIdLow;

    /// <summary>High half of the 128-bit file identifier.</summary>
    public ulong FileIdHigh;
}

/// <summary>The reply to a request for a file's times, size and attributes together.</summary>
/// <remarks>
/// <para>
/// Named for the case it was added for — answering an open over the network without actually
/// opening anything — but it is the cheapest way to get all of this in one call on any
/// filesystem, which is what it is used for here. The alternative is two requests, one for
/// the times and one for the size, and two requests are two instants.
/// </para>
/// <para>
/// The trailing field is padding the native declaration spells out, and it is declared here
/// for the same reason every other reserved field is: the structure's size is part of the
/// contract, and one declared short is one the system writes past.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct FileNetworkOpenInformation
{
    /// <summary>When the file was created, or zero where nothing recorded it.</summary>
    public long CreationTime;

    /// <summary>When the file's contents were last read.</summary>
    public long LastAccessTime;

    /// <summary>When the file's contents were last written.</summary>
    public long LastWriteTime;

    /// <summary>When the file's metadata last changed.</summary>
    public long ChangeTime;

    /// <summary>The space reserved for the file, which is not its length.</summary>
    public long AllocationSize;

    /// <summary>The file's length in bytes.</summary>
    public long EndOfFile;

    /// <summary>The file attribute bits.</summary>
    public uint FileAttributes;

    /// <summary>Padding the native declaration carries. Never read.</summary>
    public uint Reserved;

    /// <summary>The size the native API expects, checked by the layout tests.</summary>
    public static unsafe int StructSize => sizeof(FileNetworkOpenInformation);
}
