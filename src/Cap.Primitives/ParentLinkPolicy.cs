namespace Cap.Primitives;

/// <summary>
/// What a parse should do when it meets a <c>..</c> component.
/// </summary>
/// <remarks>
/// <para>
/// Collapsing <c>..</c> in the string before touching the disk — turning <c>a/../b</c> into
/// <c>b</c> — is the single most common way a string-based sandbox is defeated. If <c>a</c>
/// is a symlink to <c>/etc</c>, the kernel resolves <c>a/../b</c> to <c>/b</c>; a checker
/// that collapsed it first has approved a different path from the one that will be opened.
/// So no policy here collapses anything. The choice is only between refusing the component
/// and carrying it through to a resolver that can walk up for real and re-check the result
/// against the root.
/// </para>
/// <para>
/// <see cref="Reject"/> is the default because it is the answer that is correct without any
/// help: a caller who gets a parsed path back and forgets to ask about <c>..</c> has still
/// not been handed an escape.
/// </para>
/// </remarks>
public enum ParentLinkPolicy
{
    /// <summary>
    /// Refuse the path with <see cref="CapPathError.ParentLink"/>. The default, and the
    /// behaviour at any boundary where caller-supplied strings first arrive.
    /// </summary>
    Reject = 0,

    /// <summary>
    /// Parse the path, preserve each <c>..</c> as a component of its own, and record the
    /// fact in <see cref="CapPath.ContainsParentLink"/>. For resolvers that implement
    /// upward movement as an actual step against a real handle, bounded by the sandbox root.
    /// Choosing this without implementing that check is an escape.
    /// </summary>
    Preserve,
}
