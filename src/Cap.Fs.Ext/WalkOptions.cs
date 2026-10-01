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
    /// <para>
    /// Entries at depth <see cref="MaxDepth"/> are reported; an entry at depth
    /// <see cref="MaxDepth"/> + 1 fails the walk. A directory at the limit is therefore fine
    /// while it is empty, and one with anything in it is refused when that first entry is
    /// read, after the directory itself has been yielded. One, for instance, lists what is
    /// directly inside the start and fails on the first non-empty directory among it.
    /// </para>
    /// <para>
    /// A tree deeper than this stops the walk with a failure rather than being quietly cut
    /// short. Returning part of a tree and calling it the tree is the kind of answer a caller
    /// acts on without noticing, and a walk that is used to decide what to delete, copy or
    /// publish must not silently leave things out.
    /// </para>
    /// </remarks>
    public int MaxDepth { get; init; } = DefaultMaxDepth;

    /// <summary>
    /// How deep an entry must be before the walk yields it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zero, the default, yields everything, as does one, since the shallowest entries are at
    /// depth one. Entries above this depth are not yielded and are still descended into, so
    /// two, for instance, leaves out what is directly inside the start and yields everything
    /// beneath it. walkdir's <c>min_depth</c>.
    /// </para>
    /// <para>
    /// It changes only what is yielded, never where the walk goes: <see cref="MaxDepth"/>,
    /// <see cref="SkipHidden"/> and <see cref="OnError"/> apply to the entries it leaves out
    /// exactly as to the ones it yields, and <see cref="OnError"/> may be handed an entry that
    /// is never yielded. It may not exceed <see cref="MaxDepth"/>, which would leave nothing
    /// to yield.
    /// </para>
    /// </remarks>
    public int MinDepth { get; init; }

    /// <summary>
    /// Whether a directory is yielded after everything inside it rather than before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off, the walk is pre-order: a directory is yielded, and then what is inside it. On, it is
    /// post-order: a directory the walk enters is yielded once everything inside it has been,
    /// which is the order removing a tree needs, or setting each directory's times after its
    /// contents have been written. walkdir's <c>contents_first</c>.
    /// </para>
    /// <para>
    /// <strong>The directory is yielded after it is entered, not before.</strong> Its entry
    /// carries the handle of the directory it was found in, which the walk holds until it has
    /// yielded it, so the entry is as usable as any other; the directory's own handle, opened
    /// for reading what was inside it, has been closed by then, and what the entry names is
    /// opened again if anything opens it. A directory the walk does not enter — a link it is
    /// not following, one that leads out, one already on the way down, one
    /// <see cref="OnError"/> said to go on without — is yielded where it was read, as any other
    /// entry is. <see cref="OnError"/> is therefore called before the entry it is given has
    /// been yielded, rather than after.
    /// </para>
    /// <para>
    /// A tree deeper than <see cref="MaxDepth"/> fails the walk at the first entry past the
    /// limit, before the directory holding it has been yielded.
    /// </para>
    /// </remarks>
    public bool ContentsFirst { get; init; }

    /// <summary>
    /// The order each directory's entries are yielded in, by name, or null for the order the
    /// directory is read in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null, the default, yields entries in whatever order the filesystem lists them, which no
    /// platform promises anything about and which differs between filesystems on the same
    /// machine. Given, each directory's entries are read in full when the walk enters it and
    /// yielded, and descended into, in the order this puts their names in —
    /// <c>string.CompareOrdinal</c>, for instance, for a walk that comes out the same on every
    /// machine. It orders siblings only: what is inside a directory still comes straight after
    /// it, or straight before it under <see cref="ContentsFirst"/>. Two names it calls equal
    /// come out in either order.
    /// </para>
    /// <para>
    /// <strong>It costs memory.</strong> Without it the walk holds one entry per level at a
    /// time; with it, the whole of every directory on the way down from the start to the entry
    /// being yielded. A directory of a million names is a million entries held until the walk
    /// leaves it. A failure reading a directory fails the walk when the directory is entered,
    /// before any of its entries are yielded.
    /// </para>
    /// <para>
    /// It is called while the walk is reading and must not throw; anything it throws fails the
    /// walk.
    /// </para>
    /// </remarks>
    public Comparison<string>? Sort { get; init; }

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
    /// <para>
    /// Whether the hidden attribute is looked at is decided by the handle rather than the
    /// machine, since a filesystem held in memory can follow Windows rules anywhere. A walk
    /// over an <see cref="Cap.Std.IDir"/> that is not a <see cref="Cap.Std.Dir"/> has no way
    /// to ask, and looks at it when the machine is Windows.
    /// </para>
    /// </remarks>
    public bool SkipHidden { get; init; }

    /// <summary>
    /// What happens when a directory the walk means to enter is there and cannot be opened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null, the default, fails the walk with the exception the open produced — an
    /// <see cref="UnauthorizedAccessException"/> for a directory the filesystem will not let
    /// this process read, a <see cref="Cap.Std.CapIOException"/> for anything else — for the
    /// reason <see cref="MaxDepth"/> gives: a tree with a subtree missing is not the tree.
    /// </para>
    /// <para>
    /// Given, it is called with the entry that could not be entered and the exception, while
    /// that entry is still current. Returning true leaves that directory out and carries on
    /// with the rest; returning false fails the walk with the exception as though no handler
    /// were there. The entry has already been yielded either way, so a caller that goes on can
    /// still act on the directory itself — unless <see cref="ContentsFirst"/> is on, when it is
    /// yielded straight after the handler returns true, or <see cref="MinDepth"/> leaves it
    /// out, when it is not yielded at all.
    /// </para>
    /// <para>
    /// A name that is not something to enter is not an error and never reaches it: one that
    /// has gone, is not a directory, is a link the walk is not following, is a chain of links
    /// that never arrives, leads out of the subtree, or changed while it was being opened. It
    /// covers only the open of a directory. A directory that opened and then failed part-way
    /// through being read, and a tree deeper than <see cref="MaxDepth"/>, fail the walk
    /// whatever this says.
    /// </para>
    /// </remarks>
    public Func<WalkEntry, Exception, bool>? OnError { get; init; }
}
