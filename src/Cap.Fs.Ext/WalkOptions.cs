namespace Cap.Fs.Ext;

/// <summary>
/// What a recursive walk does with what it finds.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately small. Everything here changes which entries the walk reaches, which is the
/// only thing a caller cannot do for themselves afterwards — filtering by name, by kind or by
/// anything else is a condition over the entries the walk already produced, and adding
/// options for those would put a second way of doing the same thing in front of everyone.
/// </para>
/// <para>
/// <strong>Immutable once constructed.</strong> Every property is set only by an object
/// initializer, so one instance — <see cref="Default"/> included — can be shared by any number
/// of walks on any number of threads without one of them changing what another is doing.
/// </para>
/// </remarks>
public sealed class WalkOptions
{
    /// <summary>
    /// How far down the walk will go before it refuses to go further.
    /// </summary>
    /// <remarks>
    /// The same limit resolution applies to a path's components, and for the same reason: the
    /// walk holds one open directory per level for as long as it is inside that level, so an
    /// unbounded depth is a way to exhaust the process's handles. Entries directly inside the
    /// directory the walk started at are at depth one.
    /// </remarks>
    public const int DefaultMaxDepth = 256;

    /// <summary>The ordinary settings: no links followed, nothing skipped, the default depth.</summary>
    public static WalkOptions Default { get; } = new();

    /// <summary>
    /// How many levels below the starting directory the walk will descend.
    /// </summary>
    /// <remarks>
    /// A tree deeper than this stops the walk with a failure rather than being quietly cut
    /// short. Returning part of a tree and calling it the tree is the kind of answer a caller
    /// acts on without noticing, and a walk that is used to decide what to delete, copy or
    /// publish must not silently leave things out.
    /// </remarks>
    public int MaxDepth { get; init; } = DefaultMaxDepth;

    /// <summary>
    /// Whether a symbolic link naming a directory is descended into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off, because a tree an attacker can write into is a tree in which a link is how the
    /// walk is made to visit somewhere else. With it off, a link is reported as an entry and
    /// nothing beneath it is visited; with it on, the link is opened like any other name —
    /// confined to the subtree the handle grants — and the walk carries on inside it.
    /// </para>
    /// <para>
    /// <strong>It cannot widen the handle's own policy.</strong> A handle opened to refuse
    /// symbolic links refuses them here too, and asking for them to be followed through such
    /// a handle changes nothing: the links are reported as entries. What a component was
    /// granted is decided where the handle was made, not here.
    /// </para>
    /// <para>
    /// Turning it on also turns on a check for cycles, which costs one lookup per directory
    /// entered. A link pointing at a directory above it makes an infinite tree out of a finite
    /// filesystem, and the only way to notice is to remember the identity of every directory
    /// on the way down. A directory already on that path is not entered a second time; it is
    /// still reported as an entry.
    /// </para>
    /// <para>
    /// <strong>Off holds whatever the directory read reported.</strong> Each descent is an
    /// open that refuses a link, so a link is never entered even when the read called it a
    /// directory or declined to say what it was — a directory swapped for a link between
    /// being listed and being entered, or a link on a filesystem that does not report the
    /// kinds of its entries. Such a name is yielded as an entry and nothing beneath it is
    /// visited. The directories the walk does enter are handed back under the starting
    /// handle's own policy, not a stricter one.
    /// </para>
    /// </remarks>
    public bool FollowSymlinks { get; init; }

    /// <summary>
    /// Whether entries the platform treats as hidden are left out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A name beginning with a dot on any platform, and on Windows additionally anything
    /// carrying the hidden attribute — each system's own notion, rather than one invented
    /// here that would be wrong on both. A skipped directory is not descended into either.
    /// </para>
    /// <para>
    /// On Windows this costs a lookup per entry, because the attribute is not part of what a
    /// directory read reports. A walk that leaves this off pays nothing for it.
    /// </para>
    /// <para>
    /// A symbolic link is judged as itself — by its own name and, on Windows, by the
    /// attributes of the link rather than of its target — so a visible link to a hidden
    /// directory is kept, and a hidden link to a visible one is skipped.
    /// </para>
    /// </remarks>
    public bool SkipHidden { get; init; }
}
