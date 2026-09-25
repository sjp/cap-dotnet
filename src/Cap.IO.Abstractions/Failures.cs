using Cap.Std;

namespace Cap.IO.Abstractions;

/// <summary>What an operation expected the name it was given to hold.</summary>
internal enum Expected
{
    /// <summary>A file: a directory there is an access failure, as in <c>System.IO</c>.</summary>
    File,

    /// <summary>A directory: nothing there is a missing directory, not a missing file.</summary>
    Directory,

    /// <summary>Either.</summary>
    Any,
}

/// <summary>
/// Turns what a <see cref="Dir"/> throws into what <c>System.IO</c> throws for the same
/// condition, naming the virtual path the caller passed.
/// </summary>
/// <remarks>
/// <para>
/// Code written against <c>IFileSystem</c> catches <see cref="FileNotFoundException"/>,
/// <see cref="DirectoryNotFoundException"/>, <see cref="UnauthorizedAccessException"/> and
/// <see cref="IOException"/>. <see cref="Dir"/> already reports most failures with those
/// types; this settles the cases where the framework draws the line differently, such as
/// opening a directory as a file, which it reports as an access failure.
/// </para>
/// <para>
/// A <see cref="SandboxEscapeException"/> is never translated. It is an
/// <see cref="IOException"/>, so a generic handler still catches it, and a handler that
/// wants to tell a refused escape from an ordinary failure can.
/// </para>
/// </remarks>
internal static class Failures
{
    /// <summary>
    /// The exception <c>System.IO</c> would throw in place of <paramref name="exception"/>, or
    /// null to let it propagate unchanged.
    /// </summary>
    public static Exception? Translate(Exception exception, string path, Expected expected)
    {
        if (exception is SandboxEscapeException)
        {
            return null;
        }

        switch (exception)
        {
            case FileNotFoundException when expected == Expected.Directory:
            case DirectoryNotFoundException:
                return PartNotFound(path, exception);
            case FileNotFoundException:
                return FileNotFound(path, exception);
            case UnauthorizedAccessException:
                return Denied(path, exception);
        }

        return CapIOException.KindOf(exception) switch
        {
            CapErrorKind.IsADirectory when expected == Expected.File => Denied(path, exception),
            CapErrorKind.NotADirectory when expected != Expected.File => PartNotFound(path, exception),
            _ => null,
        };
    }

    /// <summary>What <c>System.IO</c> throws for a file that is not there.</summary>
    public static FileNotFoundException FileNotFound(string path, Exception? inner = null) =>
        new($"Could not find file '{path}'.", path, inner);

    /// <summary>What <c>System.IO</c> throws for a directory that is not there.</summary>
    public static DirectoryNotFoundException PartNotFound(string path, Exception? inner = null) =>
        new($"Could not find a part of the path '{path}'.", inner);

    /// <summary>What <c>System.IO</c> throws for an access refused, a directory opened as a file included.</summary>
    public static UnauthorizedAccessException Denied(string path, Exception? inner = null) =>
        new($"Access to the path '{path}' is denied.", inner);

    /// <summary>The root of the virtual namespace cannot be removed, renamed or replaced.</summary>
    public static IOException RootIsFixed(string path) =>
        new CapIOException(
            CapErrorKind.InvalidArgument,
            $"'{path}' is the root of this file system. It is the directory handle the file system was given, and has no name beneath it that could be removed, moved or replaced.");
}

/// <summary>The members with no capability meaning, and why each has none.</summary>
/// <remarks>
/// The adapter implements the whole of <c>IFileSystem</c>, and a member it cannot honour throws
/// <see cref="NotSupportedException"/> with its reason rather than being missing, so that
/// the failure is found where the call is made and says what to do instead.
/// </remarks>
internal static class Unsupported
{
    public static NotSupportedException Drives() =>
        new("Drives are not supported by DirFileSystem. It presents one directory handle as its whole namespace, and there are no other volumes to describe.");

    public static NotSupportedException Watcher() =>
        new("File system watchers are not supported by DirFileSystem. Watching is done by path against the whole filesystem, and a change notification cannot be confined to what is beneath a directory handle.");

    public static NotSupportedException VersionInfo() =>
        new("File version information is not supported by DirFileSystem. Reading it opens the file by host path, outside the directory handle.");

    public static NotSupportedException AccessControl() =>
        new("Access control lists are not supported by DirFileSystem. Cap.Std has no operation that reads or changes them beneath a directory handle.");

    public static NotSupportedException Handle() =>
        new("Members that take a SafeFileHandle are not supported by DirFileSystem. The handle was not opened beneath this file system's directory, so nothing here can say what it reaches.");

    public static NotSupportedException Wrap(string type) =>
        new($"Wrapping a {type} is not supported by DirFileSystem. A {type} names a host path, and wrapping it would reach the host filesystem around the directory handle.");

    public static NotSupportedException Permissions() =>
        new("Changing permissions or attributes is not supported by DirFileSystem. Cap.Std has no operation that changes them beneath a directory handle.");

    public static NotSupportedException CreateMode() =>
        new("Creating with a Unix mode is not supported by DirFileSystem. Cap.Std creates with the permissions every other program asks for, and ignoring the mode would create something more permissive than was asked for.");

    public static NotSupportedException CreationTime() =>
        new("Setting a creation time is not supported by DirFileSystem. Cap.Std sets access and write times only.");

    public static NotSupportedException Encryption() =>
        new("Encryption is not supported by DirFileSystem. Cap.Std has no operation that encrypts or decrypts a file beneath a directory handle.");

    public static NotSupportedException RandomFileName() =>
        new("GetRandomFileName is not supported by DirFileSystem. It needs entropy that the adapter is not given. Use GetTempFileName, or Directory.CreateTempSubdirectory, which draw their names from Cap.Std.");
}
