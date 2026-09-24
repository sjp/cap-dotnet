using Cap.Std;

namespace WasiHost.Preview1;

/// <summary>
/// Turns what the library threw into the error code a guest is given.
/// </summary>
/// <remarks>
/// <para>
/// Mapped from the reason the library attaches to every filesystem failure,
/// <see cref="CapIOException.KindOf(Exception)"/>, and never from the message, which is prose
/// for a person and may change in any release. The reason is the same on every platform, so
/// a guest sees the same code whichever system the host runs on.
/// </para>
/// <para>
/// The containment refusal is mapped exactly. <see cref="CapErrorKind.Escaped"/> becomes
/// <see cref="Errno.NotCapable"/>, which is the code WASI reserves for "this descriptor does
/// not grant that". A link the handle's policy will not follow becomes
/// <see cref="Errno.Loop"/>, as a POSIX open that refuses to follow a link reports it.
/// </para>
/// </remarks>
internal static class ErrorMapping
{
    public static Errno ToErrno(Exception exception) => exception switch
    {
        ObjectDisposedException => Errno.BadF,
        ArgumentException => Errno.Inval,
        NotSupportedException => Errno.NotSup,
        IOException or UnauthorizedAccessException => CapIOException.KindOf(exception) switch
        {
            CapErrorKind.Escaped => Errno.NotCapable,
            CapErrorKind.NotFound => Errno.NoEnt,
            CapErrorKind.PermissionDenied => Errno.Access,
            CapErrorKind.AlreadyExists => Errno.Exist,
            CapErrorKind.NotADirectory => Errno.NotDir,
            CapErrorKind.IsADirectory => Errno.IsDir,
            CapErrorKind.NotEmpty => Errno.NotEmpty,
            CapErrorKind.SymbolicLink or CapErrorKind.LinkNotFollowed => Errno.Loop,
            CapErrorKind.NotALink => Errno.Inval,
            CapErrorKind.CrossDevice => Errno.XDev,
            CapErrorKind.ReadOnlyFilesystem => Errno.RoFs,
            CapErrorKind.InvalidArgument => Errno.Inval,
            CapErrorKind.NotSupported => Errno.NotSup,
            CapErrorKind.NameTooLong => Errno.NameTooLong,
            CapErrorKind.OutOfHandles => Errno.MFile,
            CapErrorKind.ConcurrentChange => Errno.Again,
            _ => Errno.Io,
        },
        _ => throw exception,
    };

    /// <summary>
    /// Runs one library call and reports how it ended. Anything that is not a filesystem
    /// outcome — a bug in the adapter, say — is not swallowed into an error code.
    /// </summary>
    public static Errno Run(Action action)
    {
        try
        {
            action();
            return Errno.Success;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or
                                  ObjectDisposedException or NotSupportedException)
        {
            return ToErrno(e);
        }
    }
}
