namespace Cap.Std;

/// <summary>
/// A filesystem operation through a capability handle failed.
/// </summary>
/// <remarks>
/// <para>
/// Derived from <see cref="IOException"/> so that code already written to handle filesystem
/// failure keeps working when it is ported onto handle-based APIs. A caller that catches
/// <see cref="IOException"/> around a call to the framework's own file APIs catches this for
/// the same reasons and needs no second <c>catch</c> clause.
/// </para>
/// <para>
/// Failures the framework already has a well-understood type for are reported with that type
/// rather than this one: a missing directory as <see cref="DirectoryNotFoundException"/>, a
/// refusal by the filesystem's own permission check as
/// <see cref="UnauthorizedAccessException"/>. This covers what is left — the outcomes
/// peculiar to resolving a path under confinement, which have no counterpart in an API where
/// every path is resolved with the whole process's authority.
/// </para>
/// <para>
/// Which of those outcomes it was is <see cref="Kind"/>, so that a caller can act on the
/// reason (retry under another name when one is taken, say, or treat a directory that is not
/// empty differently from a name that is not a directory) without reading the message.
/// <see cref="KindOf(Exception)"/> answers the same question for the framework's types as
/// well, so one call covers every failure a filesystem member reports.
/// </para>
/// </remarks>
public class CapIOException : IOException
{
    /// <summary>Creates the exception with a default message.</summary>
    public CapIOException()
        : base("The operation on the sandboxed directory failed.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong, for a person reading the log.</param>
    public CapIOException(string? message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">What went wrong, for a person reading the log.</param>
    /// <param name="innerException">The failure that caused this one.</param>
    public CapIOException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with a reason and a message.</summary>
    /// <param name="kind">Why the operation failed, for code to act on.</param>
    /// <param name="message">What went wrong, for a person reading the log.</param>
    public CapIOException(CapErrorKind kind, string? message)
        : base(message)
    {
        Kind = kind;
    }

    /// <summary>Creates the exception with a reason, a message and an underlying cause.</summary>
    /// <param name="kind">Why the operation failed, for code to act on.</param>
    /// <param name="message">What went wrong, for a person reading the log.</param>
    /// <param name="innerException">The failure that caused this one.</param>
    public CapIOException(CapErrorKind kind, string? message, Exception? innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>Why the operation failed.</summary>
    /// <remarks>
    /// <see cref="CapErrorKind.Other"/> when the exception was created without one. Always
    /// <see cref="CapErrorKind.Escaped"/> on a <see cref="SandboxEscapeException"/>.
    /// </remarks>
    public CapErrorKind Kind { get; }

    /// <summary>
    /// Why a filesystem operation failed, whichever exception type it was reported with.
    /// </summary>
    /// <param name="exception">What an operation threw.</param>
    /// <returns>
    /// <see cref="Kind"/> for a <see cref="CapIOException"/>. For the framework's types this
    /// library reports some failures with, the reason each one stands for:
    /// <see cref="CapErrorKind.NotFound"/> for <see cref="FileNotFoundException"/> and
    /// <see cref="DirectoryNotFoundException"/>, <see cref="CapErrorKind.PermissionDenied"/>
    /// for <see cref="UnauthorizedAccessException"/>, and
    /// <see cref="CapErrorKind.NameTooLong"/> for <see cref="PathTooLongException"/>.
    /// <see cref="CapErrorKind.Other"/> for anything else.
    /// </returns>
    /// <remarks>
    /// Those framework types are kept, rather than every failure becoming a
    /// <see cref="CapIOException"/>, because code ported from path-based APIs already has
    /// <c>catch</c> clauses for them. This is what lets a caller that would rather switch on
    /// a reason do so without knowing which failures arrive as which type.
    /// </remarks>
    public static CapErrorKind KindOf(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            CapIOException cap => cap.Kind,
            FileNotFoundException or DirectoryNotFoundException => CapErrorKind.NotFound,
            UnauthorizedAccessException => CapErrorKind.PermissionDenied,
            PathTooLongException => CapErrorKind.NameTooLong,
            _ => CapErrorKind.Other,
        };
    }
}
