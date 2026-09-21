namespace Cap.Primitives.Interop;

/// <summary>
/// A platform-independent reading of a failed filesystem operation.
/// </summary>
/// <remarks>
/// <para>
/// Resolution is a loop that has to make decisions from failure codes: a component that is
/// a symlink must be read and re-resolved, a resolution that raced must be retried, and a
/// resolution that tried to leave the subtree must be refused. Those are three different
/// reactions to three different codes, so the codes cannot be flattened into "the operation
/// failed" on the way out of the syscall.
/// </para>
/// <para>
/// This enum deliberately does not mirror <see cref="System.IO.IOException"/> and its
/// subclasses. That hierarchy is built for reporting to a user and folds most of what is
/// listed here into a single exception carrying a message; the distinctions that matter to
/// a resolver — <see cref="SymbolicLink"/>, <see cref="Raced"/>, <see cref="Escaped"/>,
/// <see cref="Reparse"/> — do not survive it.
/// </para>
/// <para>
/// Categories are a lossy view on purpose: the exact platform code always travels alongside
/// in <see cref="CapError.RawCode"/>, so a backend that needs the difference between two
/// errnos that share a category can still see it.
/// </para>
/// </remarks>
internal enum CapErrorCategory
{
    /// <summary>The operation succeeded.</summary>
    None = 0,

    /// <summary>
    /// The named entry does not exist. Also the category a caller-facing API is expected to
    /// collapse other failures into where reporting the difference would reveal whether
    /// something exists outside the sandbox.
    /// </summary>
    NotFound,

    /// <summary>The filesystem's own permission check refused the operation.</summary>
    PermissionDenied,

    /// <summary>The entry already exists and the operation required that it not.</summary>
    AlreadyExists,

    /// <summary>
    /// A component used as a directory is not one. During a walk this is ordinary: it means
    /// the path continues past a file.
    /// </summary>
    NotADirectory,

    /// <summary>The target is a directory and the operation required that it not be.</summary>
    IsADirectory,

    /// <summary>
    /// The entry is a symbolic link and the open refused to follow it. Every handle-relative
    /// open this layer issues refuses to follow links, so this is the walk's signal to read
    /// the link and decide for itself, not an error to report.
    /// </summary>
    SymbolicLink,

    /// <summary>
    /// A symlink chain exceeded the kernel's budget, or a loop was detected. Distinct from
    /// <see cref="SymbolicLink"/>: this one is terminal.
    /// </summary>
    SymbolicLinkLoop,

    /// <summary>
    /// The operation would have crossed a filesystem boundary — a rename between devices,
    /// or a confined resolution that met a mount point it was told not to cross.
    /// </summary>
    CrossDevice,

    /// <summary>
    /// A confined resolution tried to leave the subtree it was pinned to, and the kernel
    /// stopped it. This is the kernel reporting a containment violation, which means the
    /// caller was handed a path that tried to escape.
    /// </summary>
    Escaped,

    /// <summary>
    /// A confined resolution could not be completed atomically because the tree changed
    /// underneath it. Expected under concurrent mutation, and retried rather than reported;
    /// see the retry note on the confined-open members of <see cref="IPlatformOps"/>.
    /// </summary>
    Raced,

    /// <summary>The operation was interrupted by a signal before it did anything.</summary>
    Interrupted,

    /// <summary>
    /// The kernel rejected the arguments. On a confined open this is the signature of a
    /// mismatch between the layout this library declares for a syscall argument and the one
    /// the kernel expects, which is why the layouts are asserted by tests rather than
    /// assumed.
    /// </summary>
    InvalidArgument,

    /// <summary>
    /// The kernel or the filesystem does not implement the operation. A confined open can
    /// report this on a kernel too old for it, or one where a seccomp filter has removed it.
    /// </summary>
    NotSupported,

    /// <summary>A name, or the whole path, was longer than the filesystem accepts.</summary>
    NameTooLong,

    /// <summary>
    /// A path nested more deeply than a walk is willing to descend. Produced only by the
    /// component-at-a-time backend, which holds one open handle per level so that upward
    /// movement can be a step back through handles it already has rather than a question put
    /// to the kernel. That makes depth a consumer of the process's descriptors, and a bound
    /// on it is what stops one hostile path from exhausting them and breaking opens
    /// elsewhere in the program.
    /// </summary>
    PathTooDeep,

    /// <summary>The process or the system is out of file descriptors or handles.</summary>
    OutOfHandles,

    /// <summary>The directory is not empty and the operation required that it be.</summary>
    NotEmpty,

    /// <summary>The filesystem is mounted read-only.</summary>
    ReadOnlyFilesystem,

    /// <summary>
    /// Windows: the object opened is a reparse point and the open did not consume it. The
    /// tag says what kind — a symbolic link, a junction, or something that is not a
    /// filesystem link at all and must never be interpreted as one.
    /// </summary>
    Reparse,

    /// <summary>
    /// The platform reported a failure this layer has no portable reading of. The raw code
    /// is still carried; only the classification is missing.
    /// </summary>
    Unknown,
}
