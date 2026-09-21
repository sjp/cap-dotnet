namespace Cap.Primitives.Interop;

/// <summary>
/// Policy a confined, kernel-atomic resolution is asked to apply on top of confinement.
/// </summary>
/// <remarks>
/// <para>
/// Confinement to the subtree is not here because it is not optional: it is the reason the
/// confined open is used at all, and an unconfined resolution would be an ordinary open.
/// Nor is the refusal to traverse magic links — the <c>/proc</c> entries that jump straight
/// to an open file, a process root or a namespace. Those are not filesystem links; following
/// one lands somewhere with no relationship to the sandbox root, so there is no policy under
/// which it is the right thing to do.
/// </para>
/// <para>
/// What is left are the two genuine choices: whether symbolic links that stay inside the
/// subtree may be followed, and whether a mount point inside the subtree may be crossed.
/// Both have defensible answers in both directions, so both are asked rather than assumed.
/// </para>
/// </remarks>
[Flags]
internal enum ConfinedResolveOptions
{
    /// <summary>
    /// Follow symbolic links, and cross mount points, as long as resolution stays inside the
    /// subtree.
    /// </summary>
    None = 0,

    /// <summary>
    /// Refuse any symbolic link, including one whose target is inside the subtree.
    /// </summary>
    RefuseSymlinks = 1,

    /// <summary>
    /// Refuse to cross a mount point. A mount appearing inside the subtree is an unrelated
    /// filesystem grafted in by whoever controls the mount table, which is outside the trust
    /// boundary.
    /// </summary>
    RefuseMountCrossing = 2,
}
