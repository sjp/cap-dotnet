using Cap.Std;

namespace WasiHost.Preview1;

/// <summary>
/// Turns what the library threw into the error code a guest is given.
/// </summary>
/// <remarks>
/// <para>
/// Mapped by exception type alone, because the type is all the library promises. Several
/// different failures — a name already taken, a directory that is not empty, a component
/// that is not a directory, a link the policy will not follow — all arrive as
/// <see cref="CapIOException"/>, told apart only by their message. A message is prose for a
/// person and may change in any release, so it is not read here, and those failures all
/// become <see cref="Errno.Io"/>. A guest that checks for <c>EEXIST</c> or <c>ENOTEMPTY</c>
/// therefore sees the wrong code. The sample's README lists this with the other places the
/// library does not yet give the adapter what WASI asks for.
/// </para>
/// <para>
/// The containment refusal is mapped exactly. <see cref="SandboxEscapeException"/> becomes
/// <see cref="Errno.NotCapable"/>, which is the code WASI reserves for "this descriptor does
/// not grant that".
/// </para>
/// </remarks>
internal static class ErrorMapping
{
    public static Errno ToErrno(Exception exception) => exception switch
    {
        SandboxEscapeException => Errno.NotCapable,
        FileNotFoundException or DirectoryNotFoundException => Errno.NoEnt,
        UnauthorizedAccessException => Errno.Access,
        PathTooLongException => Errno.NameTooLong,
        ObjectDisposedException => Errno.BadF,
        ArgumentException => Errno.Inval,
        NotSupportedException => Errno.NotSup,
        IOException => Errno.Io,
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
