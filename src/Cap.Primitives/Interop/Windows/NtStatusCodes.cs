using System.Runtime.Versioning;

namespace Cap.Primitives.Interop.Windows;

/// <summary>
/// The <c>NTSTATUS</c> values this layer recognises, and how it reads them.
/// </summary>
/// <remarks>
/// <para>
/// The native API reports failures as <c>NTSTATUS</c>, not as the Win32 error codes most
/// Windows code sees; the two are different numbering schemes and the translation between
/// them is many-to-one. Keeping the <c>NTSTATUS</c> is what preserves distinctions the Win32
/// layer throws away — most importantly the several different ways an object can turn out to
/// be a reparse point, which the Win32 layer flattens into one generic failure.
/// </para>
/// <para>
/// Anything unrecognised is translated to its Win32 equivalent and classified from there,
/// rather than being given up on. That table is far larger than this one and is maintained
/// by the system, so it will often have an answer where this does not; the original
/// <c>NTSTATUS</c> is still what gets reported.
/// </para>
/// </remarks>
internal static class NtStatusCodes
{
    public const int STATUS_SUCCESS = 0;

    /// <summary>An intermediate component of the path does not exist.</summary>
    public const int STATUS_OBJECT_PATH_NOT_FOUND = unchecked((int)0xC000003A);

    /// <summary>The final component does not exist.</summary>
    public const int STATUS_OBJECT_NAME_NOT_FOUND = unchecked((int)0xC0000034);

    public const int STATUS_OBJECT_NAME_INVALID = unchecked((int)0xC0000033);
    public const int STATUS_OBJECT_NAME_COLLISION = unchecked((int)0xC0000035);
    public const int STATUS_OBJECT_PATH_SYNTAX_BAD = unchecked((int)0xC000003B);
    public const int STATUS_OBJECT_TYPE_MISMATCH = unchecked((int)0xC0000024);
    public const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);
    public const int STATUS_INVALID_HANDLE = unchecked((int)0xC0000008);
    public const int STATUS_INVALID_PARAMETER = unchecked((int)0xC000000D);
    public const int STATUS_NOT_A_DIRECTORY = unchecked((int)0xC0000103);
    public const int STATUS_FILE_IS_A_DIRECTORY = unchecked((int)0xC00000BA);
    public const int STATUS_NOT_SUPPORTED = unchecked((int)0xC00000BB);
    public const int STATUS_DIRECTORY_NOT_EMPTY = unchecked((int)0xC0000101);
    public const int STATUS_NAME_TOO_LONG = unchecked((int)0xC0000106);
    public const int STATUS_MEDIA_WRITE_PROTECTED = unchecked((int)0xC00000A2);
    public const int STATUS_TOO_MANY_OPENED_FILES = unchecked((int)0xC000011F);
    public const int STATUS_SHARING_VIOLATION = unchecked((int)0xC0000043);
    public const int STATUS_DELETE_PENDING = unchecked((int)0xC0000056);
    public const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);
    public const int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);

    /// <summary>A rename would have moved the object to a different volume.</summary>
    public const int STATUS_NOT_SAME_DEVICE = unchecked((int)0xC00000D4);

    /// <summary>
    /// The process token does not hold a privilege the operation requires. What creating a
    /// symbolic link reports on a system where that is still privileged.
    /// </summary>
    public const int STATUS_PRIVILEGE_NOT_HELD = unchecked((int)0xC0000061);

    /// <summary>The object cannot be removed: read-only, in use, or a directory that is not empty.</summary>
    public const int STATUS_CANNOT_DELETE = unchecked((int)0xC0000121);

    /// <summary>
    /// The filesystem does not implement the class of information that was asked for.
    /// </summary>
    /// <remarks>
    /// How an older system, or a filesystem that has not caught up, reports that one of the
    /// newer forms of removal or renaming is unavailable. It is the signal to use the older
    /// form of the same operation, not a failure to report.
    /// </remarks>
    public const int STATUS_INVALID_INFO_CLASS = unchecked((int)0xC0000003);

    /// <summary>The object is not a reparse point, so there is no link to read.</summary>
    public const int STATUS_NOT_A_REPARSE_POINT = unchecked((int)0xC0000275);

    /// <summary>
    /// Resolution met a reparse point and stopped. Returned when the open did not ask to
    /// consume the reparse point itself.
    /// </summary>
    public const int STATUS_REPARSE_POINT_ENCOUNTERED = unchecked((int)0xC000050B);

    /// <summary>
    /// The object is a reparse point whose tag the filesystem does not act on. It is
    /// emphatically not a symbolic link, and treating it as one would mean interpreting the
    /// contents of something whose format this library does not know.
    /// </summary>
    public const int STATUS_IO_REPARSE_TAG_NOT_HANDLED = unchecked((int)0xC0000279);

    /// <summary>
    /// A success code, not a failure: the object manager wants resolution restarted at a new
    /// name. Reaching a caller here would mean an open followed a link that nobody decided
    /// to follow.
    /// </summary>
    public const int STATUS_REPARSE = 0x00000104;

    /// <summary>True when the status indicates failure.</summary>
    public static bool IsFailure(int status) => status < 0;

    /// <summary>Reads an <c>NTSTATUS</c> as a portable category.</summary>
    public static CapErrorCategory Classify(int status)
    {
        switch (status)
        {
            case STATUS_OBJECT_NAME_NOT_FOUND:
            case STATUS_OBJECT_PATH_NOT_FOUND:
            case STATUS_DELETE_PENDING:
                return CapErrorCategory.NotFound;
            case STATUS_ACCESS_DENIED:
            case STATUS_SHARING_VIOLATION:
            case STATUS_PRIVILEGE_NOT_HELD:
            case STATUS_CANNOT_DELETE:
                return CapErrorCategory.PermissionDenied;
            case STATUS_OBJECT_NAME_COLLISION:
                return CapErrorCategory.AlreadyExists;
            case STATUS_NOT_A_DIRECTORY:
            case STATUS_OBJECT_TYPE_MISMATCH:
                return CapErrorCategory.NotADirectory;
            case STATUS_FILE_IS_A_DIRECTORY:
                return CapErrorCategory.IsADirectory;
            case STATUS_REPARSE_POINT_ENCOUNTERED:
            case STATUS_IO_REPARSE_TAG_NOT_HANDLED:
            case STATUS_REPARSE:
                return CapErrorCategory.Reparse;
            case STATUS_NOT_A_REPARSE_POINT:
                return CapErrorCategory.NotSupported;
            case STATUS_OBJECT_NAME_INVALID:
            case STATUS_OBJECT_PATH_SYNTAX_BAD:
            case STATUS_INVALID_PARAMETER:
            case STATUS_INVALID_HANDLE:
                return CapErrorCategory.InvalidArgument;
            case STATUS_NOT_SUPPORTED:
            case STATUS_INVALID_INFO_CLASS:
                return CapErrorCategory.NotSupported;
            case STATUS_NOT_SAME_DEVICE:
                return CapErrorCategory.CrossDevice;
            case STATUS_DIRECTORY_NOT_EMPTY:
                return CapErrorCategory.NotEmpty;
            case STATUS_NAME_TOO_LONG:
                return CapErrorCategory.NameTooLong;
            case STATUS_MEDIA_WRITE_PROTECTED:
                return CapErrorCategory.ReadOnlyFilesystem;
            case STATUS_TOO_MANY_OPENED_FILES:
                return CapErrorCategory.OutOfHandles;
            default:
                return CapErrorCategory.Unknown;
        }
    }

    /// <summary>Wraps an <c>NTSTATUS</c> as a failure, or success when it is not one.</summary>
    /// <remarks>
    /// An unrecognised status is handed to the system's own translation to a Win32 error and
    /// classified from that. The reported code stays the original <c>NTSTATUS</c>: the
    /// translation is a way to understand the failure, not a replacement for what the kernel
    /// actually said. That fallback is the only part of this type that needs the platform:
    /// the table above is data, and is exercised on every build agent.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static CapError ToError(int status)
    {
        if (!IsFailure(status))
        {
            return CapError.Success;
        }

        CapErrorCategory category = Classify(status);
        if (category == CapErrorCategory.Unknown)
        {
            category = Win32Errors.Classify(NtNative.RtlNtStatusToDosError(status));
        }

        return CapError.Create(category, CapErrorSource.NtStatus, status);
    }
}
