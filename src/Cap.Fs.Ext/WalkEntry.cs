using System.Diagnostics.CodeAnalysis;
using Cap.Primitives;
using Cap.Std;

namespace Cap.Fs.Ext;

/// <summary>
/// One entry a walk reached, and the authority to open it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no path here either.</strong> A walk is the operation most tempted to
/// hand back a path, because a flattened list of paths is what every path-based library
/// returns and what callers expect. It would also be the end of the guarantee: a path is a
/// string, and a string reaches the filesystem through whatever resolves it, with the
/// process's own privileges and none of the confinement the walk was performed under. So what
/// comes back is a name, the handle on the directory it was found in, and how far down it is.
/// </para>
/// <para>
/// <strong>The handle belongs to the walk.</strong> It is open for exactly as long as the
/// walk is inside that directory, and the walk closes it when it moves on — so an entry kept
/// past the iteration step that produced it refers to a handle that has been disposed. Acting
/// on an entry means acting on it now: open what it names, or describe it, or take a copy of
/// the directory handle with <see cref="Cap.Std.Dir.Clone"/>, while the entry is the one the
/// walk has just yielded. Collecting the entries into a list and using them afterwards does
/// not work, and that is the type saying what a walk over handles actually is.
/// </para>
/// <para>
/// <strong>Immutable.</strong> An entry is a read-only value; nothing about it changes after
/// the walk yields it. What changes is whether the handle it carries is still open.
/// </para>
/// </remarks>
public readonly struct WalkEntry
{
    private readonly Dir? _directory;
    private readonly int _depth;

    internal WalkEntry(Dir directory, DirEntry entry, int depth)
    {
        _directory = directory;
        Entry = entry;
        _depth = depth;
    }

    /// <summary>
    /// The directory this entry was found in, which is where its authority comes from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Open only while the walk is inside it. See the notes on this type.
    /// </para>
    /// <para>
    /// Safe to read from any thread. The handle is safe for concurrent use while it is open,
    /// and a disposal by the walk racing a call on another thread ends as that call throwing
    /// <see cref="ObjectDisposedException"/>.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> When the walk follows links, this may be a directory
    /// the walk reached through one, carrying the policy of the handle the walk started from.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This entry came from no walk.</exception>
    public Dir Directory =>
        _directory ??
        throw new InvalidOperationException(
            "This entry did not come from a walk, so there is no handle to open it through. " +
            "A default value of this type carries no authority and names nothing.");

    /// <summary>The entry itself, as reading the directory produced it.</summary>
    /// <remarks>
    /// Safe to read from any thread. It opens through <see cref="Directory"/>, so it is
    /// usable only while that handle is.
    /// </remarks>
    public DirEntry Entry { get; }

    /// <summary>The entry's name: a single component, as the filesystem stores it.</summary>
    /// <remarks>
    /// <para>Safe to read from any thread, and still readable after the walk has moved on.</para>
    /// <para>
    /// <strong>Symbolic links.</strong> For a link this is the link's own name, never its
    /// target's.
    /// </para>
    /// </remarks>
    public string Name => Entry.Name;

    /// <summary>What the entry is, as the directory read reported it.</summary>
    /// <remarks>
    /// <para>
    /// A snapshot rather than a promise, exactly as it is on the entry itself: a name that
    /// said it was a directory may hold something else by the time anything opens it.
    /// </para>
    /// <para>Safe to read from any thread, and still readable after the walk has moved on.</para>
    /// <para>
    /// <strong>Symbolic links.</strong> A link reports <see cref="CapFileType.Symlink"/>
    /// whatever it leads to, a directory included, and whether or not the walk went through
    /// it.
    /// </para>
    /// </remarks>
    public CapFileType Type => Entry.Type;

    /// <summary>
    /// How far below the directory the walk started at this entry is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One for an entry directly inside it, two for an entry inside one of those, and so on.
    /// </para>
    /// <para>Safe to read from any thread, and still readable after the walk has moved on.</para>
    /// <para>
    /// <strong>Symbolic links.</strong> Counted in levels the walk descended, so a directory
    /// entered through a link adds one like any other; it is not the depth of the object on
    /// the filesystem, which a followed link can make larger or smaller.
    /// </para>
    /// </remarks>
    public int Depth => _depth;

    /// <summary>Opens the entry as a directory.</summary>
    /// <returns>A handle on it, carrying the walk's own resolution policy.</returns>
    /// <remarks>
    /// <para>
    /// The name is resolved again rather than reused from the walk, so what opens is whatever
    /// holds the name now.
    /// </para>
    /// <para>
    /// Safe to call from any thread, while the entry is the one the walk has just yielded; the
    /// handle it goes through is safe for concurrent use, and once the walk has moved on the
    /// call throws <see cref="ObjectDisposedException"/> rather than reaching anything else.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> A link at this name is followed as the policy of the
    /// walk's handle allows — only while it resolves inside the subtree, and not at all under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> — whether or not the walk itself
    /// follows links.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This entry came from no walk.</exception>
    /// <exception cref="ArgumentException">The name is not one this platform will open.</exception>
    /// <exception cref="DirectoryNotFoundException">The entry is gone, or was never a directory.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the open.</exception>
    /// <exception cref="ObjectDisposedException">The walk has moved past this entry.</exception>
    public Dir OpenDir() => Entry.OpenDir();

    /// <summary>Opens the entry as a directory, reporting failure rather than throwing.</summary>
    /// <param name="dir">The open directory, when this returns true.</param>
    /// <returns>True when it was opened.</returns>
    /// <remarks>
    /// <para>
    /// Safe to call from any thread, while the entry is the one the walk has just yielded; the
    /// handle it goes through is safe for concurrent use, and once the walk has moved on the
    /// call throws <see cref="ObjectDisposedException"/> rather than reaching anything else.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> A link at this name is followed as the policy of the
    /// walk's handle allows — only while it resolves inside the subtree, and not at all under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> — whether or not the walk itself
    /// follows links.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This entry came from no walk.</exception>
    /// <exception cref="ObjectDisposedException">The walk has moved past this entry.</exception>
    public bool TryOpenDir([NotNullWhen(true)] out Dir? dir) => Entry.TryOpenDir(out dir);

    /// <summary>Opens the entry as a file to read.</summary>
    /// <returns>The open file.</returns>
    /// <remarks>
    /// <para>
    /// Safe to call from any thread, while the entry is the one the walk has just yielded; the
    /// handle it goes through is safe for concurrent use, and once the walk has moved on the
    /// call throws <see cref="ObjectDisposedException"/> rather than reaching anything else.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> A link at this name is followed as the policy of the
    /// walk's handle allows — only while it resolves inside the subtree, and not at all under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> — whether or not the walk itself
    /// follows links.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This entry came from no walk.</exception>
    /// <exception cref="ArgumentException">The name is not one this platform will open.</exception>
    /// <exception cref="FileNotFoundException">The entry is gone.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the open.</exception>
    /// <exception cref="CapIOException">The name holds a directory, or the open failed otherwise.</exception>
    /// <exception cref="ObjectDisposedException">The walk has moved past this entry.</exception>
    public CapFile OpenFile() => Entry.OpenFile();

    /// <summary>Opens the entry as a file to read, reporting failure rather than throwing.</summary>
    /// <param name="file">The open file, when this returns true.</param>
    /// <returns>True when it was opened.</returns>
    /// <remarks>
    /// <para>
    /// Safe to call from any thread, while the entry is the one the walk has just yielded; the
    /// handle it goes through is safe for concurrent use, and once the walk has moved on the
    /// call throws <see cref="ObjectDisposedException"/> rather than reaching anything else.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> A link at this name is followed as the policy of the
    /// walk's handle allows — only while it resolves inside the subtree, and not at all under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> — whether or not the walk itself
    /// follows links.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This entry came from no walk.</exception>
    /// <exception cref="ObjectDisposedException">The walk has moved past this entry.</exception>
    public bool TryOpenFile([NotNullWhen(true)] out CapFile? file) => Entry.TryOpenFile(out file);

    /// <summary>Describes what the entry's name holds now.</summary>
    /// <returns>A snapshot of the entry, taken at the moment of the call.</returns>
    /// <remarks>
    /// <para>
    /// Safe to call from any thread, while the entry is the one the walk has just yielded; the
    /// handle it goes through is safe for concurrent use, and once the walk has moved on the
    /// call throws <see cref="ObjectDisposedException"/> rather than reaching anything else.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> A link at this name is described as the link, not as
    /// what it leads to, whatever the handle's policy.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This entry came from no walk.</exception>
    /// <exception cref="ArgumentException">The name is not one this platform will open.</exception>
    /// <exception cref="FileNotFoundException">The entry is gone.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the question.</exception>
    /// <exception cref="CapIOException">The question could not be answered.</exception>
    /// <exception cref="ObjectDisposedException">The walk has moved past this entry.</exception>
    public CapMetadata GetMetadata() => Entry.GetMetadata();

    /// <summary>Describes what the entry's name holds now, reporting failure rather than throwing.</summary>
    /// <param name="metadata">The snapshot, when this returns true.</param>
    /// <returns>True when the entry was described.</returns>
    /// <remarks>
    /// <para>
    /// Safe to call from any thread, while the entry is the one the walk has just yielded; the
    /// handle it goes through is safe for concurrent use, and once the walk has moved on the
    /// call throws <see cref="ObjectDisposedException"/> rather than reaching anything else.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> A link at this name is described as the link, not as
    /// what it leads to, whatever the handle's policy.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This entry came from no walk.</exception>
    /// <exception cref="ObjectDisposedException">The walk has moved past this entry.</exception>
    public bool TryGetMetadata(out CapMetadata metadata) => Entry.TryGetMetadata(out metadata);
}
