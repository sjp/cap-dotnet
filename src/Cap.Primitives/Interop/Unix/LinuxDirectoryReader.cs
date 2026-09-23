using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// Reads a directory on Linux, a bufferful of entries at a time.
/// </summary>
/// <remarks>
/// <para>
/// The raw kernel call rather than the C library's directory stream. The stream would mean a
/// second buffer, a second copy of every name and an object whose lifetime has to be tied to
/// a descriptor this library already owns; the call it is built on is the one used here, and
/// its result is a run of variable-length records that can be read in place.
/// </para>
/// <para>
/// By syscall number, for the same reason the confined open is: the C library's wrapper for
/// it is a recent addition and whether it exists depends on which library the process was
/// linked against, while the syscall itself has been there throughout.
/// </para>
/// <para>
/// Most entries cost no lookup at all — the kernel reports what each one is as part of the
/// read. Some filesystems decline to, and those entries are looked up individually, which is
/// why enumerating such a filesystem is markedly slower and not merely different.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed unsafe class LinuxDirectoryReader : DirectoryReader
{
    /// <summary>
    /// How much the kernel is asked to fill at a time.
    /// </summary>
    /// <remarks>
    /// One syscall per bufferful, so this is the ratio of kernel transitions to entries. At
    /// this size a directory of a hundred thousand ordinary names costs on the order of a
    /// hundred calls rather than one per name, and the buffer is rented rather than held, so
    /// nothing keeps it between enumerations.
    /// </remarks>
    private const int BufferBytes = 32 * 1024;

    // The record the kernel writes: an inode number, an offset, the record's own length, the
    // kind byte, and then the name, terminated and padded out to the stated length. Laid out
    // by hand rather than declared as a structure because the name has no fixed size, so a
    // structure would describe only the part before it and the offsets would still be needed.
    private const int RecordLengthOffset = 16;
    private const int KindOffset = 18;
    private const int NameOffset = 19;

    private readonly SafeDirHandle _handle;
    private readonly bool _alwaysLookUp = UnixFileTypes.AlwaysLookUpKind;
    private byte[]? _buffer;
    private int _filled;
    private int _offset;
    private int _nameOffset;
    private int _nameLength;

    /// <summary>
    /// Takes ownership of <paramref name="handle"/>, which must be a descriptor opened for
    /// this enumeration alone.
    /// </summary>
    /// <remarks>
    /// Its own descriptor and not a copy of the caller's, because the position a directory
    /// read advances belongs to the open file description rather than to the descriptor. A
    /// duplicate shares that position, so two enumerations through duplicates of one handle
    /// would consume each other's entries.
    /// </remarks>
    public LinuxDirectoryReader(SafeDirHandle handle)
    {
        _handle = handle;
        _buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        byte[]? buffer = _buffer;
        _buffer = null;
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        _handle.Dispose();
    }

    /// <inheritdoc/>
    protected override CapError ReadCore(out bool advanced)
    {
        advanced = false;

        if (_buffer is null)
        {
            return HandleLease.ClosedError;
        }

        if (_offset >= _filled)
        {
            CapError error = Fill();
            if (error.IsFailure || _filled == 0)
            {
                return error;
            }
        }

        ReadOnlySpan<byte> remaining = _buffer.AsSpan(_offset, _filled - _offset);

        // Read as the kernel wrote it, in this machine's own byte order, and bounds-checked
        // against what is actually in the buffer. The kernel does not produce a record that
        // overruns what it said it filled; reading the length without checking it would make
        // that promise load-bearing, which is not a thing to rest a memory read on.
        if (remaining.Length <= NameOffset)
        {
            return CapError.FromCategory(CapErrorCategory.Unknown);
        }

        int recordLength = MemoryMarshal.Read<ushort>(remaining[RecordLengthOffset..]);
        if (recordLength <= NameOffset || recordLength > remaining.Length)
        {
            return CapError.FromCategory(CapErrorCategory.Unknown);
        }

        ReadOnlySpan<byte> name = remaining[NameOffset..recordLength];
        int end = name.IndexOf((byte)0);
        if (end >= 0)
        {
            name = name[..end];
        }

        _nameOffset = _offset + NameOffset;
        _nameLength = name.Length;
        _offset += recordLength;

        SetCurrent(
            name,
            _alwaysLookUp
                ? CapFileType.Unknown
                : UnixFileTypes.FromDirectoryEntry(remaining[KindOffset]));
        advanced = true;
        return CapError.Success;
    }

    /// <inheritdoc/>
    protected override CapFileType Classify()
    {
        if (_buffer is null || _nameLength == 0)
        {
            return CapFileType.Unknown;
        }

        using HandleLease lease = _handle.Lease();
        if (!lease.IsValid)
        {
            return CapFileType.Unknown;
        }

        StatxBuffer result;

        // The name is looked up straight out of the record the kernel wrote, which is still
        // in the buffer and is already terminated. Decoding it and encoding it again would
        // ask about a name reconstructed from the one the kernel gave, which is the same
        // bytes right up until it is not.
        fixed (byte* name = &_buffer[_nameOffset])
        {
            long status = LinuxNative.Statx(
                LinuxConstants.SYS_statx,
                lease.Descriptor,
                name,
                LinuxConstants.AT_SYMLINK_NOFOLLOW | LinuxConstants.AT_NO_AUTOMOUNT,
                LinuxConstants.STATX_TYPE | LinuxConstants.STATX_MODE,
                &result);

            if (status != 0)
            {
                return CapFileType.Unknown;
            }
        }

        // The kernel fills in what it can and says which fields those were. A reply that
        // left out the type bits has not answered the question, and reading the mode anyway
        // would read a zero and report it as an unrecognised kind.
        return (result.Mask & LinuxConstants.STATX_TYPE) == 0
            ? CapFileType.Unknown
            : UnixFileTypes.FromMode(result.Mode);
    }

    private CapError Fill()
    {
        _offset = 0;
        _filled = 0;

        using HandleLease lease = _handle.Lease();
        if (!lease.IsValid)
        {
            return HandleLease.ClosedError;
        }

        fixed (byte* buffer = _buffer)
        {
            long read = LinuxNative.GetDents64(
                LinuxConstants.SYS_getdents64, lease.Descriptor, buffer, (nuint)_buffer!.Length);

            if (read < 0)
            {
                return LinuxErrno.ToError(Marshal.GetLastPInvokeError());
            }

            _filled = (int)read;
        }

        return CapError.Success;
    }
}
