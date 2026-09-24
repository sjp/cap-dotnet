using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cap.Primitives.Interop.Windows;

/// <summary>
/// Reads a directory on Windows, a bufferful of entries at a time.
/// </summary>
/// <remarks>
/// <para>
/// The native query rather than the Win32 find calls, for the reason every other operation
/// here avoids Win32: those take a path with a wildcard in it, resolved with the process's
/// own authority, and a sandbox has no path to give them. This one takes the directory
/// handle itself and a buffer, and names nothing.
/// </para>
/// <para>
/// <strong>The reply carries the attributes, so the kind of each entry costs nothing.</strong>
/// That is the one place this platform is easier than the others: there is no equivalent of
/// a filesystem declining to say what an entry is, and so no lookup to fall back to.
/// </para>
/// <para>
/// The information class asked for carries a reparse tag alongside the attributes. A class
/// without one would say only that an entry redirects, not by what mechanism — and the
/// difference between a symbolic link and a reparse point of some other kind is exactly what
/// must not be guessed at, because only one of the two holds something that can be read as a
/// path.
/// </para>
/// <para>
/// It carries each entry's file identifier too, so that an enumeration can say which object
/// an entry refers to without opening it. The class with the full 128-bit identifier is asked
/// for first, because that is the width a description of the object reports and the two must
/// compare equal. A filesystem that does not offer it is asked for the older class with a
/// 64-bit identifier instead, which on every such filesystem is the whole identifier: the
/// wider form exists for the filesystems whose identifiers do not fit, and those all offer the
/// wider class.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsDirectoryReader : DirectoryReader
{
    /// <summary>
    /// How much the filesystem is asked to fill at a time.
    /// </summary>
    /// <remarks>
    /// One call per bufferful, and this platform is the one where that ratio matters most:
    /// each call is a round trip through the IO manager and, on a network filesystem, over
    /// the wire. Large enough that a directory of a hundred thousand names costs hundreds of
    /// calls rather than tens of thousands, and rented rather than held.
    /// </remarks>
    private const int BufferBytes = 32 * 1024;

    // The records the filesystem writes, laid out by hand because the name at the end of
    // each has no fixed size. Everything before the name is at a fixed offset; the name
    // follows immediately and its length is stated in bytes. The two classes agree up to the
    // name's length and differ after it: the wider one has a field of its own for the reparse
    // tag and a 128-bit identifier, the older one reports the tag in the field an ordinary
    // file uses for the size of its extended attributes and has a 64-bit identifier.
    private const int NextEntryOffsetField = 0;
    private const int FileAttributesField = 56;
    private const int FileNameLengthField = 60;

    private const int ExtdReparseTagField = 68;
    private const int ExtdFileIdField = 72;
    private const int ExtdNameField = 88;

    private const int FullReparseTagField = 64;
    private const int FullFileIdField = 72;
    private const int FullNameField = 80;

    private readonly SafeDirHandle _handle;
    private bool _wideIdentifiers = true;
    private byte[]? _buffer;
    private int _filled;
    private int _offset;
    private bool _buffered;
    private bool _drained;
    private bool _restart = true;

    /// <summary>
    /// Takes ownership of <paramref name="handle"/>, which must have been opened for this
    /// enumeration alone.
    /// </summary>
    /// <remarks>
    /// Its own handle and not a copy of the caller's, because the position a directory read
    /// advances belongs to the open object rather than to the handle. Two enumerations
    /// sharing one object would consume each other's entries.
    /// </remarks>
    public WindowsDirectoryReader(SafeDirHandle handle)
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

        if (!_buffered)
        {
            if (_drained)
            {
                return CapError.Success;
            }

            CapError error = Fill();
            if (error.IsFailure || _drained)
            {
                return error;
            }
        }

        ReadOnlySpan<byte> record = _buffer.AsSpan(_offset, _filled - _offset);
        int nameField = _wideIdentifiers ? ExtdNameField : FullNameField;

        // Bounds-checked against what the call said it wrote. The filesystem does not
        // produce a record that runs past that, and reading the stated name length without
        // checking it would make that promise the thing standing between a buffer and a read
        // past its end.
        if (record.Length < nameField)
        {
            return CapError.Create(
                CapErrorCategory.Unknown, CapErrorSource.NtStatus, NtStatusCodes.STATUS_INVALID_PARAMETER);
        }

        int nameBytes = MemoryMarshal.Read<int>(record[FileNameLengthField..]);
        if (nameBytes < 0 || nameBytes % sizeof(char) != 0 || nameField + nameBytes > record.Length)
        {
            return CapError.Create(
                CapErrorCategory.Unknown, CapErrorSource.NtStatus, NtStatusCodes.STATUS_INVALID_PARAMETER);
        }

        uint attributes = MemoryMarshal.Read<uint>(record[FileAttributesField..]);
        uint tag;
        UInt128 fileId;
        if (_wideIdentifiers)
        {
            tag = MemoryMarshal.Read<uint>(record[ExtdReparseTagField..]);

            // Low half first, as the identifier is laid out in memory and as a description
            // of the object reads it.
            fileId = new UInt128(
                MemoryMarshal.Read<ulong>(record[(ExtdFileIdField + sizeof(ulong))..]),
                MemoryMarshal.Read<ulong>(record[ExtdFileIdField..]));
        }
        else
        {
            tag = MemoryMarshal.Read<uint>(record[FullReparseTagField..]);
            fileId = MemoryMarshal.Read<ulong>(record[FullFileIdField..]);
        }

        int next = MemoryMarshal.Read<int>(record[NextEntryOffsetField..]);
        if (next <= 0 || next + nameField > record.Length)
        {
            // A last record in this bufferful. The next read either refills or reports the
            // end; an offset with no room behind it for another record is treated the same
            // way rather than followed, which covers both a zero and a value that points
            // past what the call said it wrote.
            _buffered = false;
        }
        else
        {
            _offset += next;
        }

        SetCurrent(
            MemoryMarshal.Cast<byte, char>(record.Slice(nameField, nameBytes)),
            Classify(attributes, tag),
            fileId);

        advanced = true;
        return CapError.Success;
    }

    /// <summary>Reads what an entry is from the attributes the query returned.</summary>
    /// <remarks>
    /// A tag this library does not recognise is reported as a redirection of an unknown kind
    /// and never as a link. Reparse tags are an extension mechanism — new ones arrive with
    /// new features — so treating an unrecognised one as a link would mean reading a
    /// structure of unknown shape as if it held a path.
    /// </remarks>
    private static CapFileType Classify(uint attributes, uint tag)
    {
        if ((attributes & NtConstants.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            return ReparseTags.IsFilesystemLink(tag) ? CapFileType.Symlink : CapFileType.ReparsePoint;
        }

        return (attributes & NtConstants.FILE_ATTRIBUTE_DIRECTORY) != 0
            ? CapFileType.Directory
            : CapFileType.File;
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

        IoStatusBlock status = default;
        int result = Query(lease.Raw, &status);

        // A filesystem that does not offer the wider class says so on the first call, before
        // anything has been read, so falling back to the older one there loses nothing and
        // the scan simply starts again in the other shape.
        if (_restart && _wideIdentifiers && IsUnsupportedClass(result))
        {
            _wideIdentifiers = false;
            status = default;
            result = Query(lease.Raw, &status);
        }

        _restart = false;

        if (result is NtStatusCodes.STATUS_NO_MORE_FILES or NtStatusCodes.STATUS_NO_SUCH_FILE)
        {
            _drained = true;
            return CapError.Success;
        }

        if (NtStatusCodes.IsFailure(result))
        {
            return CapError.Create(NtStatusCodes.Classify(result), CapErrorSource.NtStatus, result);
        }

        _filled = (int)status.Information;
        _buffered = _filled > 0;
        _drained = !_buffered;
        return CapError.Success;
    }

    private int Query(nint handle, IoStatusBlock* status)
    {
        fixed (byte* buffer = _buffer)
        {
            // The scan is restarted only on the first call. Asking for a restart every time
            // would read the first bufferful over and over; leaving it off the first would
            // continue a scan the previous holder of this object started, and this handle was
            // opened for this enumeration alone precisely so there is no such thing.
            return NtNative.NtQueryDirectoryFileEx(
                handle,
                0,
                0,
                0,
                status,
                buffer,
                (uint)_buffer!.Length,
                _wideIdentifiers
                    ? NtConstants.FileIdExtdDirectoryInformationClass
                    : NtConstants.FileIdFullDirectoryInformationClass,
                _restart ? NtConstants.SL_RESTART_SCAN : 0,
                null);
        }
    }

    /// <summary>
    /// Whether a status is a filesystem declining the information class rather than failing
    /// the read.
    /// </summary>
    /// <remarks>
    /// More than one status, because filesystems and redirectors do not agree on how to say
    /// it: the one defined for the purpose, a general refusal, and the invalid-parameter
    /// reply some older drivers give for any class they were not written to know.
    /// </remarks>
    private static bool IsUnsupportedClass(int status) =>
        status is NtStatusCodes.STATUS_INVALID_INFO_CLASS
            or NtStatusCodes.STATUS_NOT_SUPPORTED
            or NtStatusCodes.STATUS_INVALID_PARAMETER;
}
