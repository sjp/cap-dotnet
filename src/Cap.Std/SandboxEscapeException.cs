namespace Cap.Std;

/// <summary>
/// A path named something the handle it was used against confers no authority over.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from every other failure because it means something different. A missing file is
/// an ordinary fact about the filesystem; this is a request that, had it been honoured, would
/// have reached outside the subtree the capability covers. It is therefore an event worth
/// recording and worth alerting on, and a separate type is what lets an application do that
/// without pattern-matching on message text.
/// </para>
/// <para>
/// Being thrown is not evidence of an attack. The commonest cause is a caller joining a
/// user-supplied fragment onto a path without checking it, and the second commonest is a
/// symbolic link inside the subtree that points outside it — planted by nobody in particular,
/// years ago, by a package that assumed a system-wide layout. What it does mean is that the
/// operation was refused on containment grounds and not for any other reason, which is the
/// distinction that makes a log of these worth reading.
/// </para>
/// <para>
/// Reported for a path that is rejected on inspection as well as for one refused mid-walk —
/// an absolute path, a drive- or root-relative one, a network location, a device-namespace
/// prefix, a <c>..</c> component, or a name reserved for a character device. None of those
/// name anything beneath a directory handle, and refusing them before touching the filesystem
/// rather than after changes nothing about what was asked for.
/// </para>
/// </remarks>
public sealed class SandboxEscapeException : CapIOException
{
    /// <summary>Creates the exception with a default message.</summary>
    public SandboxEscapeException()
        : base(CapErrorKind.Escaped, "The path named something outside the directory the handle grants authority over.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong, for a person reading the log.</param>
    public SandboxEscapeException(string? message)
        : base(CapErrorKind.Escaped, message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">What went wrong, for a person reading the log.</param>
    /// <param name="innerException">The failure that caused this one.</param>
    public SandboxEscapeException(string? message, Exception? innerException)
        : base(CapErrorKind.Escaped, message, innerException)
    {
    }
}
