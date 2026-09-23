using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// Reads a directory on macOS, through the C library's directory stream.
/// </summary>
/// <remarks>
/// <para>
/// Unlike Linux, this platform has no stable public call that hands back a run of raw
/// entries: the one that exists is the old one, whose records carry a 32-bit inode number,
/// and the 64-bit form of it is private. So the directory stream is used, which is the
/// supported way to read a directory here and does its own buffering underneath — the
/// buffer this type would otherwise own belongs to the C library instead.
/// </para>
/// <para>
/// The stream is built on a descriptor this library opened rather than on a path, which is
/// the whole reason it can be used at all: a path-based open would resolve a name with the
/// process's ambient authority and would have nothing to do with the directory a caller
/// holds a handle on.
/// </para>
/// <para>
/// The re-entrant read is used rather than the plain one, although it is the deprecated of
/// the two, because the plain one reports the end of the directory and a failure the same
/// way — a null reply — and separates them only through <c>errno</c>, which is not reliably
/// readable from managed code across a call that may not have set it. Reading a truncated
/// directory as a complete one is exactly the failure a capability library must not have.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed unsafe class DarwinDirectoryReader : DirectoryReader
{
    private readonly nint _stream;

    /// <summary>
    /// The descriptor the stream was built on, for looking a name up when the directory read
    /// did not say what it is.
    /// </summary>
    /// <remarks>
    /// Wrapped but not owned: the stream owns the descriptor and closing the stream closes
    /// it. The wrapper is here for what it refuses to do — once it has been disposed a lease
    /// on it fails, so a lookup arriving after the stream was closed cannot reach a
    /// descriptor number that something else has since been given.
    /// </remarks>
    private readonly SafeDirHandle _descriptor;
    private readonly bool _alwaysLookUp = UnixFileTypes.AlwaysLookUpKind;

    private DarwinDirectoryEntry _entry;
    private bool _closed;

    /// <summary>
    /// Takes ownership of <paramref name="stream"/>, and with it of the descriptor it was
    /// built on.
    /// </summary>
    public DarwinDirectoryReader(nint stream, SafeDirHandle descriptor)
    {
        _stream = stream;
        _descriptor = descriptor;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;

        // The wrapper first, so that nothing can take a lease on the descriptor after the
        // stream has closed it. It owns nothing, so disposing it closes nothing; what it
        // does is stop answering.
        _descriptor.Dispose();
        DarwinNative.CloseDir(_stream);
    }

    /// <inheritdoc/>
    protected override CapError ReadCore(out bool advanced)
    {
        advanced = false;

        if (_closed)
        {
            return HandleLease.ClosedError;
        }

        DarwinDirectoryEntry* result;
        int status;

        fixed (DarwinDirectoryEntry* entry = &_entry)
        {
            status = DarwinNative.ReadDirR(_stream, entry, &result);
        }

        if (status != 0)
        {
            return DarwinErrno.ToError(status);
        }

        if (result is null)
        {
            return CapError.Success;
        }

        fixed (byte* name = _entry.Name)
        {
            SetCurrent(
                new ReadOnlySpan<byte>(name, NameLength),
                _alwaysLookUp ? CapFileType.Unknown : UnixFileTypes.FromDirectoryEntry(_entry.Kind));
        }

        advanced = true;
        return CapError.Success;
    }

    /// <inheritdoc/>
    protected override CapFileType Classify()
    {
        if (_closed || NameLength == 0)
        {
            return CapFileType.Unknown;
        }

        using HandleLease lease = _descriptor.Lease();
        if (!lease.IsValid)
        {
            return CapFileType.Unknown;
        }

        DarwinStat result;

        // Looked up straight out of the record, which this platform terminates, so the name
        // asked about is the one the directory read produced rather than one rebuilt from
        // it.
        fixed (byte* name = _entry.Name)
        {
            int status = DarwinNative.FStatAt(
                lease.Descriptor, name, &result, DarwinConstants.AT_SYMLINK_NOFOLLOW);

            if (status != 0)
            {
                return CapFileType.Unknown;
            }
        }

        return UnixFileTypes.FromMode(result.Mode);
    }

    /// <summary>
    /// The length of the current entry's name, as the record states it and bounded by the
    /// field the name sits in.
    /// </summary>
    /// <remarks>
    /// The stated length rather than a search for a terminator, since this platform reports
    /// it. Bounded because a length larger than the field would read past the record into
    /// whatever follows it, and the length is a number that arrived from outside this
    /// process.
    /// </remarks>
    private int NameLength => Math.Min((int)_entry.NameLength, DarwinDirectoryEntry.NameCapacity);
}

/// <summary>
/// The macOS <c>struct dirent</c>, in its 64-bit-inode form.
/// </summary>
/// <remarks>
/// <para>
/// Declared in full, the name array included, because the re-entrant directory read writes a
/// whole record into a buffer the caller supplies — so the declaration is what decides how
/// much room the C library believes it has.
/// </para>
/// <para>
/// The same split applies here as to this platform's <c>struct stat</c>: the undecorated
/// symbols still mean the original 32-bit-inode layout on Intel, and only the decorated ones
/// mean this. A record filled by the wrong one would be read with every field after the
/// first at the wrong offset, which for the length of a name means reading a length that
/// belongs to something else.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DarwinDirectoryEntry
{
    /// <summary>The longest name this platform stores, and the size of the field holding it.</summary>
    public const int NameCapacity = 1024;

    /// <summary>The inode number.</summary>
    public ulong Inode;

    /// <summary>Where this entry sits in the directory, for a later seek.</summary>
    public ulong SeekOffset;

    /// <summary>The length of this record.</summary>
    public ushort RecordLength;

    /// <summary>The length of the name, not counting its terminator.</summary>
    public ushort NameLength;

    /// <summary>What the entry is, where the filesystem says.</summary>
    public byte Kind;

    /// <summary>The name, terminated, in the filesystem's own bytes.</summary>
    public fixed byte Name[NameCapacity];

    /// <summary>The size the C library expects, checked by the layout tests.</summary>
    public static int StructSize => sizeof(DarwinDirectoryEntry);
}
