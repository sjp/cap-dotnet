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

    /// <summary>
    /// Closes a handle the native open produced.
    /// </summary>
    /// <remarks>
    /// Needed for the handles this layer opens and then decides not to hand back — an open
    /// that succeeded against a name the caller is not allowed to use still produced a
    /// handle, and dropping it on the floor would leak it. Handles that do reach a caller are
    /// closed by their wrapper instead.
    /// </remarks>
    [LibraryImport("ntdll.dll")]
    internal static partial int NtClose(nint handle);

    /// <summary>
    /// Creates or opens an object, resolving its name against a directory handle.
    /// </summary>
    /// <remarks>
    /// The creating counterpart of <see cref="NtOpenFile"/>. Everything that is being made
    /// goes through it — a directory, the stub a symbolic link is written into, a file the
    /// caller asked to create — and so does a file open whose disposition the caller chose,
    /// because this is the only call that has a disposition at all. An open that cannot
    /// create anything goes through the other call instead, which has no disposition to pass
    /// wrongly.
    /// </remarks>
    [LibraryImport("ntdll.dll")]
    internal static partial int NtCreateFile(
        nint* fileHandle,
        uint desiredAccess,
        ObjectAttributes* objectAttributes,
        IoStatusBlock* ioStatusBlock,
        long* allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        void* eaBuffer,
        uint eaLength);

    /// <summary>
    /// Writes one class of information about an open file.
    /// </summary>
    /// <remarks>
    /// How removal, renaming and hard linking are all performed here. Each takes a handle to
    /// the object and a structure describing what to do with it, which is what makes them
    /// expressible against an already-confined handle at all — the path-taking Win32
    /// equivalents would re-resolve a string, with the Win32 rewriting in front of it, and
    /// throw away everything resolution established.
    /// </remarks>
    [LibraryImport("ntdll.dll")]
    internal static partial int NtSetInformationFile(
        nint fileHandle,
        IoStatusBlock* ioStatusBlock,
        void* fileInformation,
        uint length,
        uint fileInformationClass);

    /// <summary>
    /// Reads a run of directory entries into a buffer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only enumeration call this library can use: it names the directory by its handle
    /// and takes no path at all, where the Win32 find calls take a path with a wildcard on
    /// the end and resolve it with the process's own authority.
    /// </para>
    /// <para>
    /// Called synchronously, which is what the handle allows: every directory here is opened
    /// for blocking IO, so the call returns when the buffer is filled and the event, the
    /// completion routine and its context are all unused.
    /// </para>
    /// </remarks>
    [LibraryImport("ntdll.dll")]
    internal static partial int NtQueryDirectoryFileEx(
        nint fileHandle,
        nint completionEvent,
        nint completionRoutine,
        nint completionContext,
        IoStatusBlock* ioStatusBlock,
        void* fileInformation,
        uint length,
        uint fileInformationClass,
        uint queryFlags,
        UnicodeString* fileName);

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

    /// <summary>
    /// Asks the system what kind of object an open handle refers to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Win32 call, and safely so: it takes a handle rather than a path, so there is no
    /// string for the Win32 layer to rewrite on the way down. What it reports is a property
    /// of the object that was actually opened, which is the whole reason for asking.
    /// </para>
    /// <para>
    /// It answers the exact question this library needs answered — is this a filesystem
    /// object or a device — using the system's own classification. Reading the underlying
    /// volume device type and mapping it here instead would mean maintaining a list of which
    /// device types count as a filesystem, and such a list ages the same way the reserved
    /// name list does.
    /// </para>
    /// </remarks>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetFileType(nint handle);

    /// <summary>
    /// Asks the system which path an open handle is currently reachable by.
    /// </summary>
    /// <remarks>
    /// A Win32 call taking a handle, so there is no string for the Win32 layer to rewrite on
    /// the way down; the rewriting that matters here happens on the way back, and is wanted.
    /// The reply is the object's own long name, spelled in the user-facing syntax, which is
    /// what makes it useful in a log — the native call's answer is a path from the root of
    /// the volume and names no drive.
    /// </remarks>
    /// <returns>
    /// The length written, not counting the terminator, on success; a length including the
    /// terminator when the buffer was too small; zero on failure.
    /// </returns>
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetFinalPathNameByHandle(
        nint file,
        char* path,
        uint pathLength,
        uint flags);

    /// <summary>A pseudo-handle for the current process, for the handle-duplicating call.</summary>
    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    /// <summary>
    /// Produces a second handle to the object an existing handle refers to.
    /// </summary>
    /// <remarks>
    /// A handle rather than a name, so the copy refers to the object the original refers to
    /// and nothing a concurrent rename does can change which object that is. Re-opening the
    /// name would ask the filesystem to resolve it a second time, which is the one thing a
    /// capability handle exists to avoid having to do.
    /// </remarks>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateHandle(
        nint sourceProcess,
        nint sourceHandle,
        nint targetProcess,
        nint* targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

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

    /// <summary>Required to remove an object, and to rename one.</summary>
    public const uint DELETE = 0x00010000;

    /// <summary>
    /// Write at the end of the file, wherever that is when the write happens.
    /// </summary>
    /// <remarks>
    /// Granted instead of the ordinary write right rather than alongside it. Holding both
    /// would let a write land at an explicit offset, which is exactly what a handle opened to
    /// append is not supposed to be able to do.
    /// </remarks>
    public const uint FILE_APPEND_DATA = 0x0004;

    // --- Share mode, one bit at a time --------------------------------------------------

    public const uint FILE_SHARE_READ = 0x0001;
    public const uint FILE_SHARE_WRITE = 0x0002;
    public const uint FILE_SHARE_DELETE = 0x0004;

    // --- Open options --------------------------------------------------------------------

    /// <summary>Refuse the open unless the object is a directory.</summary>
    public const uint FILE_DIRECTORY_FILE = 0x00000001;

    /// <summary>Refuse the open if the object is a directory.</summary>
    public const uint FILE_NON_DIRECTORY_FILE = 0x00000040;

    /// <summary>Make the handle usable for ordinary blocking reads and writes.</summary>
    /// <remarks>
    /// Its absence is what makes a handle capable of overlapped operations; there is no
    /// separate flag asking for those. A handle opened without it and then used for blocking
    /// reads, or opened with it and then handed to something expecting overlapped ones, does
    /// not fail cleanly — so which of the two a handle is has to be decided when it is opened
    /// and carried with it afterwards.
    /// </remarks>
    public const uint FILE_SYNCHRONOUS_IO_NONALERT = 0x00000020;

    /// <summary>Do not return from a write until the data has reached the storage device.</summary>
    public const uint FILE_WRITE_THROUGH = 0x00000002;

    /// <summary>A hint that the file will be read from beginning to end.</summary>
    public const uint FILE_SEQUENTIAL_ONLY = 0x00000004;

    /// <summary>A hint that the file will be read out of order.</summary>
    public const uint FILE_RANDOM_ACCESS = 0x00000800;

    /// <summary>Remove the object once the last handle to it is closed.</summary>
    /// <remarks>
    /// Acts on the object rather than on a name, which is why it can be offered here at all.
    /// The removal happens to the file that was opened, whatever its name has become in the
    /// meantime, so it cannot be made to remove something a rename put in its place.
    /// </remarks>
    public const uint FILE_DELETE_ON_CLOSE = 0x00001000;

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

    // --- Create disposition ---------------------------------------------------------------

    /// <summary>Open the object, and fail if nothing holds the name.</summary>
    public const uint FILE_OPEN = 1;

    /// <summary>
    /// Create the object, and fail if the name is already taken.
    /// </summary>
    /// <remarks>
    /// The only disposition anything but a file open ever passes. Every other one either
    /// opens something that exists or silently does one or the other, and a create that
    /// quietly opened an existing object would let a name planted by somebody else be
    /// mistaken for one this process had just made. A file open passes whichever disposition
    /// the caller chose, because choosing is the whole of what a file mode is.
    /// </remarks>
    public const uint FILE_CREATE = 2;

    /// <summary>Open the object, or create it if nothing holds the name.</summary>
    public const uint FILE_OPEN_IF = 3;

    // The overwriting dispositions are deliberately not declared. Combined with the flag
    // that opens a reparse point as itself, they overwrite the reparse point rather than
    // what it refers to, destroying a link before anything can notice it was one. Emptying a
    // file is done through its handle here, after the object has been opened and examined.



    // --- File attributes for a newly created object -----------------------------------------

    /// <summary>No attributes of note. Valid only on its own.</summary>
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    /// <summary>Store the file's contents encrypted at rest.</summary>
    public const uint FILE_ATTRIBUTE_ENCRYPTED = 0x00004000;

    // --- CreateFileW ----------------------------------------------------------------------

    public const uint OPEN_EXISTING = 3;

    /// <summary>Required to obtain a handle to a directory through the Win32 call.</summary>
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    public const nint INVALID_HANDLE_VALUE = -1;

    // --- GetFinalPathNameByHandleW -----------------------------------------------------------

    /// <summary>
    /// Ask for the object's own name, spelled with a drive letter: the normalised long name
    /// and the DOS volume name, which are both the zero value of their respective fields.
    /// </summary>
    public const uint FILE_NAME_NORMALIZED_VOLUME_NAME_DOS = 0x0;

    // --- Object kinds ---------------------------------------------------------------------

    /// <summary>
    /// The handle could not be classified. Also what is reported when the call itself fails,
    /// which is why nothing here treats it as a kind: it is the value a handle carries when
    /// the system has told us nothing about it.
    /// </summary>
    public const uint FILE_TYPE_UNKNOWN = 0x0000;

    /// <summary>
    /// The handle refers to a file or directory on a filesystem — the only kind of object a
    /// sandbox deals in.
    /// </summary>
    public const uint FILE_TYPE_DISK = 0x0001;

    /// <summary>
    /// The handle refers to a character device: a console, a serial or parallel port, the
    /// null device. This is what a reserved device name reaches.
    /// </summary>
    public const uint FILE_TYPE_CHAR = 0x0002;

    /// <summary>The handle refers to a named pipe or a socket.</summary>
    public const uint FILE_TYPE_PIPE = 0x0003;

    // --- File attributes --------------------------------------------------------------------

    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

    /// <summary>
    /// The flag that makes an object refuse to be written to or removed, whatever the
    /// account attempting it is otherwise entitled to do.
    /// </summary>
    /// <remarks>
    /// Not a permission: it lives on the object rather than in its security descriptor, and
    /// anybody who may change attributes may clear it and then proceed. That is why emptying
    /// a directory of files somebody else marked this way is a matter of clearing the flag
    /// rather than of acquiring rights.
    /// </remarks>
    public const uint FILE_ATTRIBUTE_READONLY = 0x00000001;

    // --- Information classes ------------------------------------------------------------------

    /// <summary>
    /// Asks for the name of an open object, as a path from the root of its volume.
    /// </summary>
    /// <remarks>
    /// The filesystem answers with the name it stores, which is the long one. That is what
    /// makes the reply worth having: a name generated as a short alias for a longer one opens
    /// the same object, and asking the object what it is called is the only way to notice
    /// that the name used to reach it was not its own.
    /// </remarks>
    public const uint FileNameInformationClass = 9;

    /// <summary>Asks for the attribute bits and the reparse tag together.</summary>
    public const uint FileAttributeTagInformationClass = 35;

    /// <summary>
    /// Reads or writes the four timestamps and the attribute bits of an open object.
    /// </summary>
    /// <remarks>
    /// Writing it is how an attribute is cleared. A timestamp field left at zero asks for
    /// that timestamp to be left alone, which is what makes it possible to change the
    /// attributes without also deciding when the file was last written.
    /// </remarks>
    public const uint FileBasicInformationClass = 4;

    /// <summary>Asks for the volume serial and the 128-bit file identifier.</summary>
    public const uint FileIdInformationClass = 59;

    /// <summary>
    /// Ask for the times, the length and the attributes in one reply. Cheaper than asking
    /// separately, and the fields then describe one instant rather than several.
    /// </summary>
    public const uint FileNetworkOpenInformationClass = 34;

    /// <summary>
    /// Asks a directory read for each entry's name, attributes and reparse tag.
    /// </summary>
    /// <remarks>
    /// The tag is the reason for this class rather than the plainer one. It arrives in the
    /// field an ordinary entry uses for the size of its extended attributes, which is how
    /// this platform has always reported it, and without it an entry that redirects could
    /// only be known to redirect and not by what mechanism.
    /// </remarks>
    public const uint FileFullDirectoryInformationClass = 2;

    // --- Directory query flags -----------------------------------------------------------

    /// <summary>
    /// Begin the scan again from the first entry.
    /// </summary>
    /// <remarks>
    /// Passed on the first read of a handle and never afterwards. The position belongs to
    /// the open object, so a handle opened for one enumeration starts wherever the
    /// enumeration left it, and asking for a restart on every read would return the first
    /// bufferful for ever.
    /// </remarks>
    public const uint SL_RESTART_SCAN = 0x00000001;

    // --- Control codes ----------------------------------------------------------------------------

    /// <summary>Reads the data stored in a reparse point.</summary>
    public const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;

    /// <summary>
    /// Writes the data of a reparse point, which is how a symbolic link is created here.
    /// </summary>
    /// <remarks>
    /// The Win32 call that makes a symbolic link takes two paths and resolves both with the
    /// process's ambient authority, so it cannot be used beneath a directory handle at all.
    /// A link is therefore made in two steps: an empty object is created as an entry of the
    /// confined directory, and the link data is written into it through its handle. The
    /// window between them is visible — the empty stub exists for an instant — and is closed
    /// by removing the stub if the second step fails.
    /// </remarks>
    public const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;

    // --- Information classes for writing --------------------------------------------------------

    /// <summary>Sets an open file's length, which is how a file is emptied through its handle.</summary>
    public const uint FileEndOfFileInformationClass = 20;

    /// <summary>
    /// Sets how much room is claimed for an open file, behind whatever length it reports.
    /// </summary>
    /// <remarks>
    /// Set to zero before the length is, when a file is being emptied: the space a file was
    /// using is not released by shortening it alone, so a file emptied without this would
    /// report no contents while still occupying the disk.
    /// </remarks>
    public const uint FileAllocationInformationClass = 19;

    /// <summary>Renames an open object. The older form, carrying a plain replace flag.</summary>
    public const uint FileRenameInformationClass = 10;

    /// <summary>Creates a second name for an open object.</summary>
    public const uint FileLinkInformationClass = 11;

    /// <summary>Marks an open object for removal. The older form, carrying a plain flag.</summary>
    public const uint FileDispositionInformationClass = 13;

    /// <summary>
    /// Marks an open object for removal, with flags — among them the one that makes the name
    /// disappear at once rather than when the last handle closes.
    /// </summary>
    public const uint FileDispositionInformationExClass = 64;

    /// <summary>Renames an open object, with flags.</summary>
    public const uint FileRenameInformationExClass = 65;

    // --- Flags for those classes ------------------------------------------------------------------

    /// <summary>Remove the object.</summary>
    public const uint FILE_DISPOSITION_DELETE = 0x00000001;

    /// <summary>
    /// Unlink the name immediately, rather than keeping it until every handle is closed.
    /// </summary>
    /// <remarks>
    /// This platform's default is the other way round: a removed name stays visible, and
    /// unusable, until the last handle to the object goes away, which is why deleting a file
    /// something else has open behaves so differently here from everywhere else. Asking for
    /// the immediate form makes removal mean the same thing on every platform this library
    /// runs on, which is worth more than matching the local convention.
    /// </remarks>
    public const uint FILE_DISPOSITION_POSIX_SEMANTICS = 0x00000002;

    /// <summary>Replace an entry already holding the destination name.</summary>
    public const uint FILE_RENAME_REPLACE_IF_EXISTS = 0x00000001;

    /// <summary>Replace it the way the other platforms do: at once, even if it is open.</summary>
    public const uint FILE_RENAME_POSIX_SEMANTICS = 0x00000002;

}
