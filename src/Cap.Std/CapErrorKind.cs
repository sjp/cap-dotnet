namespace Cap.Std;

/// <summary>
/// Why a filesystem operation through a capability handle failed, in a form a caller can
/// switch on.
/// </summary>
/// <remarks>
/// <para>
/// Carried by <see cref="CapIOException.Kind"/>, and answered for any exception by
/// <see cref="CapIOException.KindOf(Exception)"/>, which also reads the framework's own types
/// this library reports some failures with. The message is prose for a person and may
/// change in any release; this value is the part that stays the same.
/// </para>
/// <para>
/// The same on every platform. A kind is what the failure means, not which code the system
/// returned: several are decided by this library rather than by the kernel, and those have
/// no platform code at all. The system's own code still appears in the message, for a bug
/// report, but is not offered as something to act on.
/// </para>
/// <para>
/// New values may be added in later releases, as failures that are reported as
/// <see cref="Other"/> today are given a reading of their own. A <c>switch</c> over this
/// type should have a default arm.
/// </para>
/// </remarks>
public enum CapErrorKind
{
    /// <summary>
    /// The failure has no more specific reading. Also what
    /// <see cref="CapIOException.KindOf(Exception)"/> answers for an exception this library
    /// did not classify, such as one the framework raised while reading or writing an open
    /// file.
    /// </summary>
    Other = 0,

    /// <summary>
    /// The name does not exist. Reported as <see cref="FileNotFoundException"/> or
    /// <see cref="DirectoryNotFoundException"/>.
    /// </summary>
    NotFound,

    /// <summary>
    /// The filesystem's own permission check refused the operation. Reported as
    /// <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    PermissionDenied,

    /// <summary>The name already exists and the operation required that it not.</summary>
    AlreadyExists,

    /// <summary>
    /// Something used as a directory is not one: a component the path continues past, a
    /// name spelled with a trailing separator that holds a file, or the target of an
    /// operation that acts only on directories.
    /// </summary>
    NotADirectory,

    /// <summary>The name holds a directory and the operation does not act on one.</summary>
    IsADirectory,

    /// <summary>The directory still has entries and the operation required that it be empty.</summary>
    NotEmpty,

    /// <summary>
    /// The name holds a symbolic link, and the operation acts on what a name holds rather
    /// than on what it points at.
    /// </summary>
    SymbolicLink,

    /// <summary>
    /// Resolution met a symbolic link it would not follow: one the handle's policy does not
    /// allow following, a chain longer than resolution will follow, or a loop.
    /// </summary>
    LinkNotFollowed,

    /// <summary>A link was to be read and the name does not hold one.</summary>
    NotALink,

    /// <summary>
    /// The source and destination are on different filesystems, so the entry cannot be moved
    /// or linked between them.
    /// </summary>
    CrossDevice,

    /// <summary>The filesystem is mounted read-only.</summary>
    ReadOnlyFilesystem,

    /// <summary>
    /// The filesystem understood the request and could not carry it out as asked, such as
    /// moving a directory to a name beneath itself.
    /// </summary>
    InvalidArgument,

    /// <summary>
    /// The filesystem or the platform does not implement what the operation needs, or an
    /// entry is of a kind the operation cannot act on.
    /// </summary>
    NotSupported,

    /// <summary>
    /// A name, or the whole path, is longer than the filesystem accepts. Reported as
    /// <see cref="PathTooLongException"/>.
    /// </summary>
    NameTooLong,

    /// <summary>The path descends further than resolution will follow.</summary>
    PathTooDeep,

    /// <summary>The process or the system is out of file descriptors or handles.</summary>
    OutOfHandles,

    /// <summary>
    /// The path could not be resolved as one step because the tree kept changing underneath
    /// it. Trying again may succeed.
    /// </summary>
    ConcurrentChange,

    /// <summary>
    /// Windows: the name reached its target through an alias, such as a generated short
    /// name, rather than by the name the filesystem stores.
    /// </summary>
    AliasedName,

    /// <summary>
    /// The path named something the handle confers no authority over. Reported as
    /// <see cref="SandboxEscapeException"/>, and always this value there.
    /// </summary>
    Escaped,
}
