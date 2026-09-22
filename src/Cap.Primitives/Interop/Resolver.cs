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
}
