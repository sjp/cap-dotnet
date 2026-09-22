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
/// </remarks>
public class CapIOException : IOException
{
    /// <summary>Creates the exception with a default message.</summary>
    public CapIOException()
        : base("The operation on the sandboxed directory failed.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public CapIOException(string? message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    public CapIOException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
