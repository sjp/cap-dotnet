namespace Cap.Primitives.Interop;

/// <summary>
/// The access a handle is opened for.
/// </summary>
/// <remarks>
/// Intentionally narrower than the platform's own vocabulary. Resolution opens intermediate
/// directories for traversal only, and opens a final target for whatever the caller asked;
/// the modes that come with creating, truncating or appending belong to the file API, which
/// applies them to a name that resolution has already confined.
/// </remarks>
[Flags]
internal enum CapAccess
{
    /// <summary>
    /// No data access. Enough to query the node and to resolve names beneath it, and the
    /// least authority a step in a walk can be taken with.
    /// </summary>
    None = 0,

    /// <summary>Read the contents.</summary>
    Read = 1,

    /// <summary>Write the contents.</summary>
    Write = 2,

    /// <summary>Read and write.</summary>
    ReadWrite = Read | Write,
}
