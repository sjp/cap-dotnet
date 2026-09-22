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
    /// Open only while the walk is inside it. See the notes on this type.
    /// </remarks>
    /// <exception cref="InvalidOperationException">This entry came from no walk.</exception>
    public Dir Directory =>
        _directory ??
        throw new InvalidOperationException(
            "This entry did not come from a walk, so there is no handle to open it through. " +
            "A default value of this type carries no authority and names nothing.");

    /// <summary>The entry itself, as reading the directory produced it.</summary>
    public DirEntry Entry { get; }

    /// <summary>The entry's name: a single component, as the filesystem stores it.</summary>
    public string Name => Entry.Name;

    /// <summary>What the entry is, as the directory read reported it.</summary>
    /// <remarks>
    /// A snapshot rather than a promise, exactly as it is on the entry itself: a name that
    /// said it was a directory may hold something else by the time anything opens it.
    /// </remarks>
    public CapFileType Type => Entry.Type;

    /// <summary>
    /// How far below the directory the walk started at this entry is.
    /// </summary>
    /// <remarks>
    /// One for an entry directly inside it, two for an entry inside one of those, and so on.
    /// </remarks>
    public int Depth => _depth;

    /// <summary>Opens the entry as a directory.</summary>
    /// <returns>A handle on it, carrying the walk's own resolution policy.</returns>
    /// <remarks>
    /// The name is resolved again rather than reused from the walk, so what opens is whatever
    /// holds the name now.
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
    /// <exception cref="InvalidOperationException">This entry came from no walk.</exception>
    /// <exception cref="ObjectDisposedException">The walk has moved past this entry.</exception>
    public bool TryOpenDir([NotNullWhen(true)] out Dir? dir) => Entry.TryOpenDir(out dir);

    /// <summary>Opens the entry as a file to read.</summary>
    /// <returns>The open file.</returns>
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
    /// <exception cref="InvalidOperationException">This entry came from no walk.</exception>
    /// <exception cref="ObjectDisposedException">The walk has moved past this entry.</exception>
    public bool TryOpenFile([NotNullWhen(true)] out CapFile? file) => Entry.TryOpenFile(out file);

    /// <summary>Describes what the entry's name holds now.</summary>
    /// <returns>A snapshot of the entry, taken at the moment of the call.</returns>
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
    /// <exception cref="InvalidOperationException">This entry came from no walk.</exception>
    /// <exception cref="ObjectDisposedException">The walk has moved past this entry.</exception>
    public bool TryGetMetadata(out CapMetadata metadata) => Entry.TryGetMetadata(out metadata);
}
