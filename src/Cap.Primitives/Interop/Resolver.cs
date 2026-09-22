namespace Cap.Primitives.Interop;

/// <summary>
/// Chooses which confined-resolution strategy a path is resolved by.
/// </summary>
/// <remarks>
/// <para>
/// There are two, and they are not equivalent. Where the kernel can resolve a whole path in
/// one operation that cannot leave the subtree, it does, and the result is the object the
/// path named at the instant of the call. Everywhere else the path is walked a component at
/// a time against handles already held, which cannot be redirected into another subtree but
/// is not a single instant. Both keep the containment guarantee; only the first also
/// guarantees that nothing was substituted along the way.
/// </para>
/// <para>
/// The choice is made from the capability the platform reported, which is settled once at
/// first use. It is not remade per call, and a failure from the kernel-atomic path is never
/// answered by quietly walking instead. That is the point worth being explicit about: a
/// demotion nobody notices leaves callers believing they have the stronger property while
/// they have the weaker one, and there is no way to tell from the outside. So a platform
/// that reports the confined open either performs it or reports why it could not, and a
/// disagreement between the probe and the call surfaces as an error rather than as a
/// silently different security posture.
/// </para>
/// <para>
/// The path must already have been parsed for use against a handle — relative, with no
/// component the parser refuses. Both strategies apply the same policy to what they find on
/// the way, so the same path resolves the same way whichever one runs.
/// </para>
/// </remarks>
internal static class Resolver
{
    /// <summary>
    /// Opens the directory that <paramref name="path"/> names beneath
    /// <paramref name="root"/>, by whichever strategy this platform provides.
    /// </summary>
    public static CapResult<SafeDirHandle> OpenDirectory(
        SafeDirHandle root,
        scoped in CapPath path,
        CapAccess access,
        ConfinedResolveOptions options)
    {
        IPlatformOps ops = PlatformOps.Current;

        return ops.Capabilities.SupportsConfinedOpen
            ? ops.OpenConfinedDirectory(root, path.Raw, access, options)
            : PortableResolver.OpenDirectory(root, in path, access, options);
    }

    /// <summary>
    /// Resolves everything ahead of <paramref name="path"/>'s last component, and hands back
    /// the directory that component would be looked up in together with the component
    /// itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What every operation that acts on a name uses: creating, removing, renaming, linking,
    /// reading a link, and asking whether a name is taken. None of those can be expressed as
    /// an open — a create must fail when the name exists, a removal must unlink the link and
    /// not its target — so each performs its own single call against the directory this
    /// produces, and that call is the only one that touches the last component.
    /// </para>
    /// <para>
    /// The kernel-atomic backend has no way to be asked to stop a component short, so the
    /// path is divided first and only the part ahead of the last component goes to it. That
    /// keeps the strength of the guarantee exactly where it was: the whole prefix is still
    /// resolved in one operation that cannot leave the subtree, and what is left is one name
    /// looked up in a directory the kernel has already confined. Walking the prefix here
    /// instead would be a quiet demotion to the weaker backend on the one platform that
    /// offers the stronger one.
    /// </para>
    /// <para>
    /// A single-component path has no prefix to resolve. The directory is then the caller's
    /// own root, and what is returned is a copy of it — a copy of something the caller
    /// already holds, which grants nothing it did not have.
    /// </para>
    /// </remarks>
    public static CapResult<ResolvedParent> ResolveParent(
        SafeDirHandle root,
        scoped in CapPath path,
        ConfinedResolveOptions options)
    {
        IPlatformOps ops = PlatformOps.Current;

        if (!ops.Capabilities.SupportsConfinedOpen)
        {
            return PortableResolver.ResolveParent(root, in path, options);
        }

        // `..` is not a name an operation can act on: there is nothing there to create,
        // remove or rename, only a directory the caller could have asked for directly. The
        // walk reaches the same conclusion by standing on the directory and finding no final
        // name, and the two backends have to agree about it or the same path would be
        // actionable on one platform and not on another.
        if (!path.TrySplitLastComponent(out ReadOnlySpan<char> prefix, out ReadOnlySpan<char> name) ||
            name.SequenceEqual(".."))
        {
            return CapResult<ResolvedParent>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        CapResult<SafeDirHandle> directory = prefix.IsEmpty
            ? ops.DuplicateDirectory(root)
            : ops.OpenConfinedDirectory(root, prefix, CapAccess.None, options);

        return directory.IsSuccess
            ? CapResult<ResolvedParent>.Ok(new ResolvedParent(directory.Value, name.ToString()))
            : CapResult<ResolvedParent>.Fail(directory.Error);
    }
}
