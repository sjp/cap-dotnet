namespace Cap.Primitives.Interop;

/// <summary>
/// The access a handle is opened for.
/// </summary>
/// <remarks>
/// <para>
/// Intentionally narrower than the platform's own vocabulary. Resolution opens intermediate
/// directories for traversal only, and opens a final target for whatever the caller asked;
/// the modes that come with creating, truncating or appending belong to the file API, which
/// applies them to a name that resolution has already confined.
/// </para>
/// <para>
/// A directory recognises only two of these. <see cref="None"/> asks for a place to resolve
/// from, <see cref="Read"/> for one whose contents can also be listed, and no operating
/// system can open a directory for writing — so a directory open asked for
/// <see cref="Write"/> is refused rather than quietly widened or narrowed into one of the
/// two that exist.
/// </para>
/// </remarks>
[Flags]
internal enum CapAccess
{
    /// <summary>
    /// No data access. Enough to query the node and to resolve names beneath it, and the
    /// least authority a step in a walk can be taken with.
    /// </summary>
    /// <remarks>
    /// On a directory this is a genuinely different kind of handle and not merely a smaller
    /// one: it can be traversed but never listed, and on Linux it is the only form that can
    /// be opened at all on a directory that grants execute permission without read
    /// permission.
    /// </remarks>
    None = 0,

    /// <summary>Read the contents.</summary>
    Read = 1,

    /// <summary>Write the contents.</summary>
    Write = 2,

    /// <summary>Read and write.</summary>
    ReadWrite = Read | Write,
}
