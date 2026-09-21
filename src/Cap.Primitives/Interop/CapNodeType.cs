namespace Cap.Primitives.Interop;

/// <summary>
/// What a directory entry is, as far as resolution needs to care.
/// </summary>
/// <remarks>
/// The list is short because resolution asks only two questions of a node: can the walk
/// continue through it, and does it redirect. Everything else a caller might want to know
/// about a file — its size, its timestamps, its permission bits — is reported by the
/// metadata layer, which reads a handle the walk has already produced.
/// </remarks>
internal enum CapNodeType
{
    /// <summary>The type could not be determined.</summary>
    Unknown = 0,

    /// <summary>An ordinary file.</summary>
    File,

    /// <summary>A directory. The only type a walk can continue through.</summary>
    Directory,

    /// <summary>
    /// A symbolic link. On Windows this also covers a mount point (junction), which is
    /// always absolute and so can never be followed while staying inside a sandbox.
    /// </summary>
    SymbolicLink,

    /// <summary>
    /// Windows only: a reparse point whose tag is not a filesystem link — an app execution
    /// alias, a container image link, or a tag this library has never heard of. These are
    /// not links, must not be read as if they were, and are never traversed.
    /// </summary>
    UnknownReparsePoint,

    /// <summary>
    /// Something else: a socket, a FIFO, a block or character device. Reachable inside a
    /// sandbox, and nameable, but never a step in a path.
    /// </summary>
    Other,
}
