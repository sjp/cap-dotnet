using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Interop.Windows;

/// <summary>
/// The Windows entry points confined resolution is built from.
/// </summary>
/// <remarks>
/// <para>
/// Almost all of these are the native API rather than the familiar Win32 one, for a reason
/// that is the whole Windows story in this library: the Win32 layer takes a path string and
/// rewrites it on the way down. It strips trailing dots and spaces, recognises reserved
/// device names wherever they appear, and re-parses prefixes. A name that has been validated
/// is therefore not necessarily the name that reaches the filesystem, and every one of the
/// published escapes from a Windows directory sandbox works through that gap. The native API
/// takes a counted string and a directory to resolve it against, and does none of the
/// rewriting.
/// </para>
/// <para>
/// The exception is acquiring the very first directory handle, which is done through the
/// Win32 call precisely because it does understand drive letters and the rest of the
/// user-facing path syntax. That step is ambient by definition; nothing is confined yet.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static unsafe partial class NtNative
{
    /// <summary>Opens an existing object, resolving its name against a directory handle.</summary>
    [LibraryImport("ntdll.dll")]
    internal static partial int NtOpenFile(
        nint* fileHandle,
        uint desiredAccess,
        ObjectAttributes* objectAttributes,
        IoStatusBlock* ioStatusBlock,
        uint shareAccess,
        uint openOptions);

    /// <summary>Reads one class of information about an open file.</summary>
    [LibraryImport("ntdll.dll")]
    internal static partial int NtQueryInformationFile(
        nint fileHandle,
        IoStatusBlock* ioStatusBlock,
        void* fileInformation,
        uint length,
        uint fileInformationClass);

    /// <summary>
    /// Translates a status into the Win32 error the system considers equivalent.
    /// </summary>
    /// <remarks>
    /// Used only to understand a status this library has no entry for. The translation loses
    /// information — it is many-to-one — so what gets reported is still the original status.
    /// </remarks>
    [LibraryImport("ntdll.dll")]
    internal static partial int RtlNtStatusToDosError(int status);

    /// <summary>Opens a path with the process's ambient authority.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    /// <summary>Issues a filesystem control code against an open handle. Used to read a reparse point.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        SafeHandle device,
        uint controlCode,
        void* inBuffer,
        uint inBufferSize,
        void* outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        nint overlapped);
}

/// <summary>Constants for the native open and query calls.</summary>
[SupportedOSPlatform("windows")]
internal static class NtConstants
{
    // --- Access mask ---------------------------------------------------------------------

    /// <summary>Read a file's contents, and list a directory's entries: the same bit.</summary>
    public const uint FILE_LIST_DIRECTORY = 0x0001;
    public const uint FILE_READ_DATA = 0x0001;
    public const uint FILE_WRITE_DATA = 0x0002;

    /// <summary>Resolve a name through a directory. Required for every intermediate step of a walk.</summary>
    public const uint FILE_TRAVERSE = 0x0020;

    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint FILE_WRITE_ATTRIBUTES = 0x0100;

    /// <summary>
    /// Required for a handle that the framework's synchronous read and write paths can use.
    /// </summary>
    public const uint SYNCHRONIZE = 0x00100000;

    // --- Share mode ----------------------------------------------------------------------

    /// <summary>
    /// Share for read, write and delete.
    /// </summary>
    /// <remarks>
    /// Permissive on purpose. A sandbox is not an exclusion mechanism, and a handle held for
    /// the length of a walk that denied sharing would make ordinary concurrent access to the
    /// sandbox fail in ways that have nothing to do with containment.
    /// </remarks>
    public const uint FILE_SHARE_ALL = 0x0001 | 0x0002 | 0x0004;

    // --- Open options --------------------------------------------------------------------

    /// <summary>Refuse the open unless the object is a directory.</summary>
    public const uint FILE_DIRECTORY_FILE = 0x00000001;

    /// <summary>Refuse the open if the object is a directory.</summary>
    public const uint FILE_NON_DIRECTORY_FILE = 0x00000040;

    /// <summary>Make the handle usable for ordinary blocking reads and writes.</summary>
    public const uint FILE_SYNCHRONOUS_IO_NONALERT = 0x00000020;

    /// <summary>
    /// Open a reparse point itself rather than what it points at.
    /// </summary>
    /// <remarks>
    /// Set on every confined open. Without it the object manager resolves the reparse point,
    /// which for a junction means resolving an absolute path to somewhere else on the volume
    /// — the decision about whether to follow a link would have been made by the system,
    /// before this library got to see that there was a link at all.
    /// </remarks>
    public const uint FILE_OPEN_REPARSE_POINT = 0x00200000;

    // --- CreateFileW ----------------------------------------------------------------------

    public const uint OPEN_EXISTING = 3;

    /// <summary>Required to obtain a handle to a directory through the Win32 call.</summary>
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    public const nint INVALID_HANDLE_VALUE = -1;

    // --- File attributes --------------------------------------------------------------------

    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

    // --- Information classes ------------------------------------------------------------------

    /// <summary>Asks for the attribute bits and the reparse tag together.</summary>
    public const uint FileAttributeTagInformationClass = 35;

    /// <summary>Asks for the volume serial and the 128-bit file identifier.</summary>
    public const uint FileIdInformationClass = 59;

    // --- Control codes ----------------------------------------------------------------------------

    /// <summary>Reads the data stored in a reparse point.</summary>
    public const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
}
