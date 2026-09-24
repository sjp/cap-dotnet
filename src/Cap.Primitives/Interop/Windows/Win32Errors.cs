namespace Cap.Primitives.Interop.Windows;

/// <summary>
/// The Win32 error codes this layer recognises.
/// </summary>
/// <remarks>
/// A second table rather than a duplicate of the first: these arrive from the two calls that
/// report through the last-error slot instead of returning a status, and from the system's
/// own translation of a status this library has no entry for. The two numbering schemes
/// share no values, so nothing is gained by merging them and a great deal is risked.
/// </remarks>
internal static class Win32Errors
{
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_HANDLE = 6;
    public const int ERROR_NOT_ENOUGH_MEMORY = 8;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_FILE_EXISTS = 80;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_INVALID_NAME = 123;
    public const int ERROR_DIR_NOT_EMPTY = 145;
    public const int ERROR_ALREADY_EXISTS = 183;
    public const int ERROR_FILENAME_EXCED_RANGE = 206;
    public const int ERROR_DIRECTORY = 267;
    public const int ERROR_TOO_MANY_OPEN_FILES = 4;
    public const int ERROR_WRITE_PROTECT = 19;
    public const int ERROR_NOT_A_REPARSE_POINT = 4390;
    public const int ERROR_REPARSE_TAG_INVALID = 4393;
    public const int ERROR_REPARSE_TAG_MISMATCH = 4394;
    public const int ERROR_IO_PENDING = 997;

    /// <summary>Reads a Win32 error as a portable category.</summary>
    public static CapErrorCategory Classify(int error) => error switch
    {
        ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND => CapErrorCategory.NotFound,
        ERROR_ACCESS_DENIED or ERROR_SHARING_VIOLATION => CapErrorCategory.PermissionDenied,
        ERROR_FILE_EXISTS or ERROR_ALREADY_EXISTS => CapErrorCategory.AlreadyExists,
        ERROR_DIRECTORY => CapErrorCategory.NotADirectory,
        ERROR_DIR_NOT_EMPTY => CapErrorCategory.NotEmpty,
        ERROR_FILENAME_EXCED_RANGE => CapErrorCategory.NameTooLong,
        ERROR_WRITE_PROTECT => CapErrorCategory.ReadOnlyFilesystem,
        ERROR_TOO_MANY_OPEN_FILES => CapErrorCategory.OutOfHandles,
        ERROR_INVALID_PARAMETER or ERROR_INVALID_NAME or ERROR_INVALID_HANDLE =>
            CapErrorCategory.InvalidArgument,
        ERROR_NOT_A_REPARSE_POINT or ERROR_REPARSE_TAG_INVALID or ERROR_REPARSE_TAG_MISMATCH =>
            CapErrorCategory.Reparse,
        _ => CapErrorCategory.Unknown,
    };

    /// <summary>Wraps a Win32 error as a failure, or success when it is zero.</summary>
    public static CapError ToError(int error) =>
        error == 0 ? CapError.Success : CapError.Create(Classify(error), CapErrorSource.Win32, error);
}
