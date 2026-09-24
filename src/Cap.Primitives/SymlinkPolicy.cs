using Cap.Primitives.Interop;

namespace Cap.Primitives;

/// <summary>
/// Whether resolution beneath a directory handle may follow a symbolic link at all.
/// </summary>
/// <remarks>
/// <para>
/// "Should a sandbox follow symbolic links?" has no single right answer, and choosing
/// silently is how a sandbox ends up either useless or unsafe. Refusing every link breaks
/// ordinary directory layouts, where a link inside a tree pointing elsewhere inside the same
/// tree is a normal thing for a package manager or a build system to have left behind.
/// Following every link is not containment at all. So the default follows a link whose
/// resolution stays inside the subtree and refuses one that leaves, and the caller may say
/// instead that no link is to be followed.
/// </para>
/// <para>
/// <strong>This is not the containment guarantee, and it cannot weaken it.</strong> Under
/// every value here, a link whose target would leave the subtree is refused, and so is
/// anything that is not a filesystem link in the first place: a Windows junction, which is
/// always stored as an absolute target; a reparse point whose tag means something other
/// than a link, which must never be read as though it were one; and the kernel's synthetic
/// links — the entries under the process filesystem that jump straight to an open file, a
/// process root or a namespace. None of those is a policy question. Following one lands
/// somewhere that has no relationship to the sandbox root, so there is no value of this
/// enum under which it is the right thing to do.
/// </para>
/// <para>
/// <strong>Nor does it decide whether the last component is followed.</strong> That is
/// fixed by the operation and is a separate axis entirely. Reading a link, removing a name,
/// asking what a name refers to without following it, and creating something exclusively
/// all act on the name they were given rather than on whatever it points at, whichever
/// value is in force here — otherwise removing a link would delete its target. An open that
/// may create or empty a file refuses a link at the last component under either value, even
/// one that stays inside, so that a write lands on the name it was given and not on another
/// file a planted link leads to; only an open of an existing file follows one. Conflating
/// the two axes is a well-worn source of bugs in this kind of library, so they are kept
/// apart: this one governs links met *on the way* to the thing named, and the operation
/// governs the thing named.
/// </para>
/// <para>
/// <strong>The values are ordered by strictness</strong>, least strict first, and code that
/// tightens a handle's policy relies on that order. A policy that a derived handle could
/// widen would not be a policy: whoever was handed a handle in order to work inside a
/// subtree could lift the restriction it came with simply by deriving from it, and the
/// restriction would last exactly as long as it took somebody to try.
/// </para>
/// <para>
/// <strong>There is deliberately no value that re-anchors an absolute link target at the
/// sandbox root.</strong> That reading is defensible — it is what <c>chroot</c> does — but
/// it silently changes which file a link means, and nothing in the result tells a caller
/// which reading they got. It is also not available in isolation: on the one platform that
/// can resolve a whole path in a single confined operation, asking the kernel to re-anchor
/// absolute targets also makes it clamp an upward step at the root instead of refusing it,
/// which would turn a link trying to climb out of the subtree from a reported refusal into
/// a successful open of a different file, and would leave that platform disagreeing with
/// the others about the same tree. Refusing an absolute target says what happened instead.
/// </para>
/// </remarks>
public enum SymlinkPolicy
{
    /// <summary>
    /// Follow a symbolic link whose resolution stays inside the subtree the handle was
    /// opened on, and refuse one that would leave it. The default.
    /// </summary>
    /// <remarks>
    /// A link's target is walked by the same loop, under the same root test, as a path a
    /// caller wrote out: a link cannot reach anywhere a written-out path could not. That is
    /// what makes "stays inside" mean the same thing for both.
    /// </remarks>
    FollowWithinSandbox = 0,

    /// <summary>
    /// Refuse every symbolic link met while resolving, including one whose target is inside
    /// the subtree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a caller that treats a link as suspect regardless of where it points — an
    /// archive extractor, or a service reading a directory that untrusted code can write
    /// into, where a link is not something the layout was ever supposed to contain.
    /// </para>
    /// <para>
    /// The refusal is reported as a link that resolution would not follow rather than as an
    /// attempt to escape, because it is not one: the link named something inside. A log of
    /// escape attempts is worth reading closely, and filling it with refusals that were
    /// nothing of the kind is how it stops being read.
    /// </para>
    /// </remarks>
    Deny = 1,
}

/// <summary>
/// Converts between the caller-facing policy and the flags resolution is given.
/// </summary>
/// <remarks>
/// Two representations rather than one because they answer to different constraints. The
/// enum is public, ordered by strictness and has to stay comprehensible; the flags are the
/// argument a confined resolution actually takes and carry more than links. Keeping one
/// derived from the other, rather than storing both, is what stops a handle from being able
/// to disagree with itself about its own policy.
/// </remarks>
internal static class SymlinkPolicyExtensions
{
    /// <summary>True when the value is one this enum defines.</summary>
    /// <remarks>
    /// Worth asking, because an undefined value cast in from outside would otherwise be
    /// read as the default by the mapping below — which is the loosest value, and so the
    /// one a mistake must not silently select.
    /// </remarks>
    public static bool IsDefinedValue(this SymlinkPolicy policy) =>
        policy is SymlinkPolicy.FollowWithinSandbox or SymlinkPolicy.Deny;

    /// <summary>The resolution flags that carry out this policy.</summary>
    public static ConfinedResolveOptions ToResolveOptions(this SymlinkPolicy policy) =>
        policy == SymlinkPolicy.Deny
            ? ConfinedResolveOptions.RefuseSymlinks
            : ConfinedResolveOptions.None;

    /// <summary>The policy a set of resolution flags expresses.</summary>
    public static SymlinkPolicy ToSymlinkPolicy(this ConfinedResolveOptions options) =>
        (options & ConfinedResolveOptions.RefuseSymlinks) != 0
            ? SymlinkPolicy.Deny
            : SymlinkPolicy.FollowWithinSandbox;
}
