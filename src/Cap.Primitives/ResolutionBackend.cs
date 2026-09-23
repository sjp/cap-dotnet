namespace Cap.Primitives;

/// <summary>
/// Which implementation of confined resolution is in use.
/// </summary>
/// <remarks>
/// <para>
/// The backends are not interchangeable in their security properties, so which one is
/// running is deliberately observable rather than an implementation detail. A caller that
/// believes it has kernel-atomic resolution while in fact running a walk has a weaker
/// guarantee than it thinks it has, and there is no way to notice that from the outside
/// unless the library says so.
/// </para>
/// <para>
/// Demotion is also the failure mode of a mistake in this layer: a confined open whose
/// argument struct has the wrong size is rejected by the kernel as an invalid argument,
/// which a naive capability probe reads as "not available" and caches forever. Every test
/// would still pass, and the fast path would never run again on any machine.
/// </para>
/// <para>
/// The choice is made once per process, the first time anything is resolved, and does not
/// change afterwards. The same process-wide answer is read through the static
/// <c>Dir.ResolutionBackend</c> property in <c>Cap.Std</c>, and published, together with
/// counts of the operations each backend performs, as instruments on the
/// <c>Cap.Primitives</c> meter.
/// </para>
/// </remarks>
public enum ResolutionBackend
{
    /// <summary>
    /// No backend: the operating system has no implementation here, and opening a directory
    /// fails rather than degrading to anything weaker.
    /// </summary>
    None = 0,

    /// <summary>
    /// Linux <c>openat2</c> with <c>RESOLVE_BENEATH</c>. The whole path is resolved by the
    /// kernel in one operation that cannot leave the subtree, so there is no window between
    /// components for anything to be swapped into.
    /// </summary>
    ConfinedOpen,

    /// <summary>
    /// A component-at-a-time walk, each step taken against the handle produced by the last.
    /// Used on Linux where the confined open is unavailable, and on macOS. Every step is
    /// handle-relative, so resolution cannot be redirected into a different subtree, but the
    /// sequence as a whole is not atomic.
    /// </summary>
    PortableWalk,

    /// <summary>
    /// The Windows walk, which takes each step through the native API with the previous
    /// handle as the resolution root rather than through a path string.
    /// </summary>
    WindowsRelativeOpen,
}
