namespace Cap.Primitives.Interop;

/// <summary>
/// A directory handle and the single name an operation is about to use against it.
/// </summary>
/// <remarks>
/// <para>
/// What <see cref="ResolutionTarget.Parent"/> produces. The handle is the confined result of
/// the walk and the name is one component that has been validated but never looked up, so
/// the operation that receives this pair performs exactly one further call and that call is
/// relative to a directory the walk has already proved is inside the sandbox.
/// </para>
/// <para>
/// The handle carries no authority beyond traversal, which is all the name-based calls need:
/// they are permitted by the rights checked on the object being created, removed or renamed,
/// not by a right to read the directory holding it. The one exception is a path of a single
/// component, where the parent is the caller's own root and the pair carries a copy of it —
/// a copy of something the caller already holds grants nothing it did not have.
/// </para>
/// <para>
/// Owning the handle, this must be disposed. The name is a copy rather than a slice of the
/// caller's string, because it may have come from a symbolic link target that the walk read
/// and will not keep.
/// </para>
/// </remarks>
internal sealed class ResolvedParent : IDisposable
{
    /// <summary>Creates a resolved parent.</summary>
    public ResolvedParent(SafeDirHandle directory, string name)
    {
        Directory = directory;
        Name = name;
    }

    /// <summary>The directory the name is to be used against.</summary>
    public SafeDirHandle Directory { get; }

    /// <summary>
    /// The final component, exactly as it was written. Never <c>.</c>, <c>..</c>, empty, or
    /// anything containing a separator.
    /// </summary>
    public string Name { get; }

    /// <summary>Closes the directory handle.</summary>
    public void Dispose() => Directory.Dispose();
}
