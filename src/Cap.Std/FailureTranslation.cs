using Cap.Primitives;
using Cap.Primitives.Interop;

namespace Cap.Std;

/// <summary>
/// Turns the outcome of a resolution into the exception a caller sees.
/// </summary>
/// <remarks>
/// <para>
/// One place, called only where a result crosses out of the library. Resolution itself
/// reports failures as values because several of them — a component that turns out to be a
/// link, a confined open that lost a race — are ordinary steps in a walk rather than errors,
/// and are handled without anybody outside ever learning they happened. Only what survives
/// to the boundary becomes an exception.
/// </para>
/// <para>
/// The mapping prefers the framework's own types wherever one means the same thing, because
/// a caller porting from path-based APIs already has <c>catch</c> clauses for them and a
/// bespoke hierarchy would silently stop them matching. What gets a type of this library's
/// own is what the framework has no word for: a refusal on containment grounds.
/// </para>
/// </remarks>
/// <summary>
/// What an operation was looking for, for the sole purpose of reporting that it was not
/// there.
/// </summary>
/// <remarks>
/// Not a resolution option and not visible to a caller. It exists because the framework
/// reports a missing directory and a missing file as different exception types, and code
/// ported onto these APIs has <c>catch</c> clauses written for whichever one it used to get.
/// </remarks>
internal enum ExpectedTarget
{
    /// <summary>A directory: a missing one is reported as such.</summary>
    Directory,

    /// <summary>A name of any kind: a missing one is reported as a missing file.</summary>
    Name,
}

internal static class FailureTranslation
{
    /// <summary>
    /// Builds the exception for a failure the platform or the resolver reported.
    /// </summary>
    /// <param name="error">The failure.</param>
    /// <param name="path">The path the caller supplied, quoted back in the message.</param>
    /// <param name="expected">
    /// What the caller was after, which decides only how a missing thing is reported. An
    /// operation that wanted a directory and did not find one is a different fact from an
    /// operation that wanted a name and found none, and the framework has a separate type
    /// for each — so a caller porting a <c>catch</c> clause keeps matching what it matched
    /// before.
    /// </param>
    public static Exception ToException(
        CapError error,
        string path,
        ExpectedTarget expected = ExpectedTarget.Directory) => error.Category switch
        {
            // Another thread disposed the handle while this call was using it. The same answer a
            // call on an already-disposed handle gets, since that is what it was by the time the
            // call reached it.
            CapErrorCategory.Closed => DisposedDuringCall(),

            CapErrorCategory.NotFound => expected == ExpectedTarget.Directory
                ? new DirectoryNotFoundException($"'{path}' does not name a directory that exists. ({error})")
                : new FileNotFoundException($"'{path}' does not name anything that exists. ({error})"),

            CapErrorCategory.AlreadyExists =>
                new CapIOException(KindOf(error.Category), $"'{path}' names something that already exists. ({error})"),

            CapErrorCategory.IsADirectory =>
                new CapIOException(KindOf(error.Category), 
                    $"'{path}' names a directory, or is spelled so that it has to be one, and " +
                    $"this operation does not act on a directory. ({error})"),

            CapErrorCategory.NotEmpty =>
                new CapIOException(KindOf(error.Category), 
                    $"'{path}' names a directory that still has entries in it. Removing what is " +
                    $"inside is a walk over handles, which the caller performs rather than this " +
                    $"call. ({error})"),

            CapErrorCategory.CrossDevice =>
                new CapIOException(KindOf(error.Category), 
                    $"'{path}' and the destination are on different filesystems, so the entry " +
                    $"cannot be moved between them. It is not copied instead: a copy has " +
                    $"different timing, different failure modes and a different result for a " +
                    $"hard link, and doing one under the name of a move would quietly stop the " +
                    $"operation being atomic. ({error})"),

            // Not a containment refusal and not a missing thing: the filesystem understood the
            // request and it was not one it could carry out as written. Moving a directory to a
            // name inside itself is the case a caller is most likely to meet; a path ending in
            // `..`, which names a directory by where it sits and leaves no name in its parent to
            // create, remove or rename, is the other.
            CapErrorCategory.InvalidArgument =>
                new CapIOException(KindOf(error.Category), 
                    $"'{path}' was not a request the filesystem could carry out as asked. " +
                    $"Moving a directory to a name beneath itself is the usual cause, and a " +
                    $"path ending in '..' is another: it leaves no name for the operation to act " +
                    $"on. ({error})"),

            CapErrorCategory.ReadOnlyFilesystem =>
                new CapIOException(KindOf(error.Category), $"'{path}' is on a filesystem mounted read-only. ({error})"),

            // Reported rather than worked around. Either the volume cannot hold what was asked
            // for -- a symbolic link or a second name on a FAT volume -- or the platform cannot
            // make a refusal part of the operation, and the alternative -- looking first and
            // acting if the answer was favourable -- has a window in which the answer changes,
            // which is the whole class of bug this library exists to remove.
            CapErrorCategory.NotSupported =>
                new CapIOException(KindOf(error.Category), 
                    $"'{path}' could not be operated on: the filesystem does not implement what " +
                    $"the operation needs, such as a kind of entry it cannot hold or a way of " +
                    $"performing the operation as one step. ({error})"),

            // The name turned out to be a symbolic link where the operation needed the thing
            // itself. Inside the subtree, so not an escape -- only a step the operation will not
            // take on the caller's behalf.
            CapErrorCategory.SymbolicLink =>
                new CapIOException(KindOf(error.Category), 
                    $"'{path}' is a symbolic link, and this operation acts on what a name holds " +
                    $"rather than on what it points at. ({error})"),

            CapErrorCategory.PermissionDenied =>
                new UnauthorizedAccessException($"Access to '{path}' was denied by the filesystem. ({error})"),

            // Every one of these means the same thing to a caller: what was asked for lies
            // outside what the handle covers. They differ only in which layer noticed.
            CapErrorCategory.Escaped =>
                new SandboxEscapeException(
                    $"'{path}' resolved outside the directory the handle grants authority over. ({error})"),

            CapErrorCategory.DeviceObject =>
                new SandboxEscapeException(
                    $"'{path}' reached a device rather than a file beneath the handle. ({error})"),

            CapErrorCategory.Reparse =>
                new SandboxEscapeException(
                    $"'{path}' passes through a reparse point that is not a filesystem link, so " +
                    $"following it would leave the subtree entirely. ({error})"),

            // Not an escape. The chain may be a genuine loop, an honestly long one, or a link
            // the policy in force declines to follow -- and in every case it named something
            // inside. Reporting these alongside the refusals that were attempts to leave would
            // put noise into the one log that is worth reading closely.
            CapErrorCategory.SymbolicLinkLoop =>
                new CapIOException(KindOf(error.Category), 
                    $"'{path}' passes through a symbolic link that resolution would not follow. ({error})"),

            // Nor is this one: the alias reaches the same object beneath the same handle. It is
            // refused because a rule stated about one spelling of a name can be walked past
            // using the other, which is a problem about names and not about containment.
            CapErrorCategory.AliasedName =>
                new CapIOException(KindOf(error.Category), 
                    $"'{path}' reached its target through an alias rather than by the name the " +
                    $"filesystem stores. ({error})"),

            CapErrorCategory.NotADirectory =>
                new CapIOException(KindOf(error.Category), $"A component of '{path}' is not a directory. ({error})"),

            CapErrorCategory.NotALink =>
                new CapIOException(KindOf(error.Category), $"'{path}' is not a symbolic link. ({error})"),

            CapErrorCategory.NameTooLong =>
                new PathTooLongException($"'{path}' is longer than the filesystem accepts. ({error})"),

            CapErrorCategory.PathTooDeep =>
                new CapIOException(KindOf(error.Category), 
                    $"'{path}' descends further than resolution will follow. Each level costs a " +
                    $"handle that is held until the walk finishes, so the depth is bounded. ({error})"),

            CapErrorCategory.OutOfHandles =>
                new CapIOException(KindOf(error.Category), 
                    $"'{path}' could not be opened: the process or the system is out of handles. ({error})"),

            CapErrorCategory.Raced =>
                new CapIOException(KindOf(error.Category), 
                    $"'{path}' could not be resolved atomically because the tree kept changing " +
                    $"underneath it. ({error})"),

            _ => new CapIOException(KindOf(error.Category), $"'{path}' could not be opened. ({error})"),
        };

    /// <summary>
    /// The reason a caller is given for a failure the resolver or the platform reported.
    /// </summary>
    /// <remarks>
    /// Not a one-to-one copy. The categories the resolver acts on and never reports (a walk's
    /// signal that the tree changed, an interrupted call) and the ones that become another
    /// exception type entirely (a closed handle) have no reason of their own, and the three
    /// ways of meeting something outside the handle are one reason, since a caller acts on
    /// them identically.
    /// </remarks>
    public static CapErrorKind KindOf(CapErrorCategory category) => category switch
    {
        CapErrorCategory.NotFound => CapErrorKind.NotFound,
        CapErrorCategory.PermissionDenied => CapErrorKind.PermissionDenied,
        CapErrorCategory.AlreadyExists => CapErrorKind.AlreadyExists,
        CapErrorCategory.NotADirectory => CapErrorKind.NotADirectory,
        CapErrorCategory.IsADirectory => CapErrorKind.IsADirectory,
        CapErrorCategory.NotEmpty => CapErrorKind.NotEmpty,
        CapErrorCategory.SymbolicLink => CapErrorKind.SymbolicLink,
        CapErrorCategory.SymbolicLinkLoop => CapErrorKind.LinkNotFollowed,
        CapErrorCategory.NotALink => CapErrorKind.NotALink,
        CapErrorCategory.CrossDevice => CapErrorKind.CrossDevice,
        CapErrorCategory.ReadOnlyFilesystem => CapErrorKind.ReadOnlyFilesystem,
        CapErrorCategory.InvalidArgument => CapErrorKind.InvalidArgument,
        CapErrorCategory.NotSupported => CapErrorKind.NotSupported,
        CapErrorCategory.NameTooLong => CapErrorKind.NameTooLong,
        CapErrorCategory.PathTooDeep => CapErrorKind.PathTooDeep,
        CapErrorCategory.OutOfHandles => CapErrorKind.OutOfHandles,
        CapErrorCategory.Raced => CapErrorKind.ConcurrentChange,
        CapErrorCategory.AliasedName => CapErrorKind.AliasedName,
        CapErrorCategory.Escaped or CapErrorCategory.DeviceObject or CapErrorCategory.Reparse => CapErrorKind.Escaped,
        _ => CapErrorKind.Other,
    };

    /// <summary>
    /// Throws the disposal a failure stands for, when it stands for one.
    /// </summary>
    /// <remarks>
    /// For the few members that build their own exception rather than going through one of the
    /// translations here, so that a handle disposed on another thread part-way through their
    /// call is reported the same way it is everywhere else.
    /// </remarks>
    public static void ThrowIfClosed(CapError error)
    {
        if (error.Category == CapErrorCategory.Closed)
        {
            throw DisposedDuringCall();
        }
    }

    /// <summary>
    /// The exception for a handle another thread disposed while a call was using it.
    /// </summary>
    /// <remarks>
    /// The same type a call on an already-disposed handle gets, since that is what the handle
    /// was by the time the call reached it. Which of the two a caller sees depends only on
    /// where the disposal landed, and a caller should not have to catch both to find out.
    /// </remarks>
    private static ObjectDisposedException DisposedDuringCall() =>
        new(null, "The handle was disposed on another thread while this operation was using it.");

    /// <summary>
    /// Builds the exception for a failure to read a directory.
    /// </summary>
    /// <remarks>
    /// Separate from the path-taking form because there is no path. The subject is the
    /// directory the handle already refers to, and a message quoting one would have to
    /// invent it — from a name the handle does not keep, or from asking the system what the
    /// object is currently called, which is authority this operation was never granted and
    /// an answer that may have changed by the time it is read.
    /// </remarks>
    public static Exception ToEnumerationException(CapError error) => error.Category switch
    {
        CapErrorCategory.Closed => DisposedDuringCall(),

        // The handle still refers to the directory, so this is not a stale handle: the
        // directory has been removed, and a directory with no name left cannot be read even
        // by something holding it open.
        CapErrorCategory.NotFound =>
            new DirectoryNotFoundException(
                $"The directory this handle refers to has been removed, so its contents can " +
                $"no longer be read. ({error})"),

        CapErrorCategory.PermissionDenied =>
            new UnauthorizedAccessException(
                $"The contents of this directory could not be read. A handle opened only in " +
                $"order to resolve names beneath a directory carries no authority to list " +
                $"it, and the filesystem's own permissions are checked as well. ({error})"),

        CapErrorCategory.NotADirectory =>
            new CapIOException(KindOf(error.Category), $"This handle does not refer to a directory. ({error})"),

        CapErrorCategory.OutOfHandles =>
            new CapIOException(KindOf(error.Category), 
                $"The directory could not be opened for reading: the process or the system " +
                $"is out of handles. ({error})"),

        _ => new CapIOException(KindOf(error.Category), $"The contents of this directory could not be read. ({error})"),
    };

    /// <summary>
    /// Builds the exception for a failure to describe what an open handle refers to.
    /// </summary>
    /// <remarks>
    /// Separate from the path-taking form for the reason the enumeration one is: there is no
    /// path to quote. The question was asked of an object, not of a name, so a message
    /// naming one would have to invent it — and the object may well have no name left, a
    /// handle to an unlinked file being perfectly answerable.
    /// </remarks>
    public static Exception ToHandleException(CapError error) => error.Category switch
    {
        CapErrorCategory.Closed => DisposedDuringCall(),

        CapErrorCategory.PermissionDenied =>
            new UnauthorizedAccessException(
                $"The filesystem would not describe what this handle refers to. ({error})"),

        // The object is readable and the platform will not answer the question. Reported
        // rather than papered over with whatever partial answer could be assembled, because
        // a snapshot with some fields quietly left at zero is worse than none.
        CapErrorCategory.NotSupported =>
            new CapIOException(KindOf(error.Category), 
                $"The filesystem does not report what this handle refers to in the form this " +
                $"library reads. ({error})"),

        _ => new CapIOException(KindOf(error.Category), $"What this handle refers to could not be described. ({error})"),
    };

    /// <summary>
    /// Builds the exception for a path the parser refused, before anything was opened.
    /// </summary>
    /// <param name="error">Why the path was refused.</param>
    /// <param name="path">The path the caller supplied.</param>
    /// <param name="parameterName">The parameter it arrived through.</param>
    /// <remarks>
    /// Two outcomes, and the line between them is what the path was asking for rather than
    /// how badly it was written. A path that names a location a directory handle confers no
    /// authority over — an absolute path, one relative to a drive or to the current volume, a
    /// network location, the device namespace, a <c>..</c> component refused by a parse that
    /// does not walk upward, or a name the system routes to a character device — is refused
    /// as an escape, because that is what it is,
    /// and the refusal deserves to be logged alongside the ones the filesystem produces.
    /// Anything else is a malformed name, which is a mistake in the calling code and is
    /// reported as one.
    /// </remarks>
    public static Exception ToException(CapPathError error, string path, string parameterName) => error switch
    {
        CapPathError.Absolute or
        CapPathError.RootRelative or
        CapPathError.DriveRelative or
        CapPathError.Unc or
        CapPathError.DeviceNamespace or
        CapPathError.ParentLink or
        CapPathError.ReservedName =>
            new SandboxEscapeException(Describe(error, path)),

        _ => new ArgumentException(Describe(error, path), parameterName),
    };

    private static string Describe(CapPathError error, string path) => error switch
    {
        CapPathError.Empty =>
            $"'{path}' names nothing beneath the handle. A path that resolves to the " +
            "directory itself cannot be opened through it; the caller already holds it.",

        CapPathError.Absolute =>
            $"'{path}' is absolute. It names a location from a filesystem root the handle " +
            "confers no authority over, so it is never interpreted relative to one.",

        CapPathError.RootRelative =>
            $"'{path}' is relative to the current drive, which is process-wide ambient state " +
            "rather than anything this handle covers.",

        CapPathError.DriveRelative =>
            $"'{path}' is relative to a per-drive working directory the process happens to be " +
            "carrying. That is ambient authority wearing the syntax of a relative path.",

        CapPathError.Unc =>
            $"'{path}' names a host and a share rather than a location beneath this handle.",

        CapPathError.DeviceNamespace =>
            $"'{path}' addresses the object manager directly, bypassing the path rules " +
            "everything else is checked against.",

        CapPathError.ReservedName =>
            $"'{path}' contains a name reserved for a character device. Such a name reaches " +
            "the device wherever it appears and never names a file beneath this handle.",

        CapPathError.InvalidCharacter =>
            $"'{path}' contains a character that cannot appear in a filename here, or that " +
            "would change meaning on the way to the kernel.",

        CapPathError.TrailingDotOrSpace =>
            $"'{path}' has a component ending in a dot or a space. The platform strips those " +
            "below its own API, so the name that would be checked is not the name that would " +
            "be opened.",

        CapPathError.ParentLink =>
            $"'{path}' contains a '..' component, and was parsed by rules that refuse one " +
            "rather than walking it upward. It is not collapsed as text, because that is only " +
            "correct when nothing in the path is a symbolic link.",

        CapPathError.TooLong =>
            $"The supplied path, or one of its components, is longer than the parser will " +
            $"consider ({CapPath.MaxLength} characters, {CapPath.MaxComponentLength} per component).",

        _ => $"'{path}' is not a usable relative path.",
    };
}
