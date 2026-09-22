using System.Runtime.CompilerServices;
using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Std;

namespace Cap.Fs.Ext;

/// <summary>
/// Walking a tree by descending through handles.
/// </summary>
/// <remarks>
/// <para>
/// A walk is a sequence of enumerations, one per directory, each performed through a handle
/// opened from the one above it. Written that way it inherits the containment of every step
/// it is made of: no name is ever joined to another, every open is confined to the subtree
/// the starting handle grants, and a directory replaced by a link between being listed and
/// being entered is refused at the open rather than followed somewhere else.
/// </para>
/// <para>
/// The implementation keeps its own stack rather than calling itself. The depth of a tree is
/// decided by whoever created it, which in the cases that matter is not this program, and a
/// recursive walk of a tree built to be deep ends as a stack overflow — a failure that cannot
/// be caught and takes the process with it. A limit that is enforced is worth more than one
/// that is merely documented, and enforcing it needs the levels to be in a list rather than in
/// stack frames.
/// </para>
/// </remarks>
public static partial class DirExtensions
{
    /// <summary>
    /// Walks everything beneath this handle, parents before their children.
    /// </summary>
    /// <param name="dir">The directory to walk.</param>
    /// <param name="options">What the walk does with what it finds, or null for the defaults.</param>
    /// <returns>
    /// The entries, each with the handle of the directory it was found in and how deep it is.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Lazy, and re-walked from the start each time it is enumerated. A caller that stops
    /// early stops the walk, and the handles it was holding are closed at that point — which
    /// is what makes it safe to walk a large tree looking for one thing.
    /// </para>
    /// <para>
    /// <strong>Each entry is only usable while it is the current one.</strong> The handle it
    /// carries is the walk's own, closed as soon as the walk leaves that directory. See
    /// <see cref="WalkEntry"/>.
    /// </para>
    /// <para>
    /// <strong>This is not a snapshot.</strong> Each directory is read when the walk reaches
    /// it, so a tree being modified while the walk runs is reported partly as it was and
    /// partly as it became, and nothing here can do better.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="WalkOptions.MaxDepth"/> is less than one.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">A directory could not be read.</exception>
    /// <exception cref="CapIOException">
    /// The tree descends past <see cref="WalkOptions.MaxDepth"/>, or a directory could not be
    /// read.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static IEnumerable<WalkEntry> Walk(this Dir dir, WalkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dir);
        WalkOptions settings = Demand(options);

        return Walking(dir, settings);
    }

    /// <summary>
    /// Walks everything beneath this handle without holding the calling thread.
    /// </summary>
    /// <param name="dir">The directory to walk.</param>
    /// <param name="options">What the walk does with what it finds, or null for the defaults.</param>
    /// <param name="cancellationToken">Stops the walk between batches of entries.</param>
    /// <returns>The entries, as <see cref="Walk"/> produces them.</returns>
    /// <remarks>
    /// The reading is not asynchronous and nothing here claims it is: no operating system this
    /// runs on offers a directory read that completes by itself, so what this does is have a
    /// thread-pool thread do the waiting. The opens between levels happen on whichever thread
    /// the enumeration resumes on, for the same reason.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="WalkOptions.MaxDepth"/> is less than one.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="UnauthorizedAccessException">A directory could not be read.</exception>
    /// <exception cref="CapIOException">
    /// The tree descends past <see cref="WalkOptions.MaxDepth"/>, or a directory could not be
    /// read.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static IAsyncEnumerable<WalkEntry> WalkAsync(
        this Dir dir,
        WalkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dir);
        WalkOptions settings = Demand(options);

        return WalkingAsync(dir, settings, cancellationToken);
    }

    /// <summary>Checks the settings a walk was given, or supplies the defaults.</summary>
    private static WalkOptions Demand(WalkOptions? options)
    {
        WalkOptions settings = options ?? WalkOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxDepth, 1, nameof(options));
        return settings;
    }

    private static IEnumerable<WalkEntry> Walking(Dir root, WalkOptions options)
    {
        Descent descent = new(root, options, asynchronous: false);

        try
        {
            while (descent.Level is { } level)
            {
                if (!level.Entries!.MoveNext())
                {
                    descent.Leave();
                    continue;
                }

                DirEntry entry = level.Entries.Current;
                if (descent.Skips(entry))
                {
                    continue;
                }

                yield return new WalkEntry(level.Directory, entry, descent.Depth);

                descent.Enter(entry);
            }
        }
        finally
        {
            descent.Dispose();
        }
    }

    private static async IAsyncEnumerable<WalkEntry> WalkingAsync(
        Dir root,
        WalkOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Descent descent = new(root, options, asynchronous: true, cancellationToken);

        try
        {
            while (descent.Level is { } level)
            {
                if (!await level.AsyncEntries!.MoveNextAsync().ConfigureAwait(false))
                {
                    await descent.LeaveAsync().ConfigureAwait(false);
                    continue;
                }

                DirEntry entry = level.AsyncEntries.Current;
                if (descent.Skips(entry))
                {
                    continue;
                }

                yield return new WalkEntry(level.Directory, entry, descent.Depth);

                descent.Enter(entry);
            }
        }
        finally
        {
            await descent.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The levels a walk currently has open, and the rules for moving between them.
    /// </summary>
    /// <remarks>
    /// Shared by both forms of the walk, which differ only in how a directory's entries are
    /// read. Everything that decides where the walk goes — what is skipped, what is entered,
    /// how deep it may go, which directories it has already been inside — is here, so the two
    /// cannot drift apart about any of it.
    /// </remarks>
    private sealed class Descent
    {
        private readonly WalkOptions _options;
        private readonly CancellationToken _cancellationToken;
        private readonly bool _asynchronous;
        private readonly List<WalkLevel> _levels = [];

        /// <summary>
        /// The directories the walk is currently inside, by identity.
        /// </summary>
        /// <remarks>
        /// Kept only when links are followed, because only then can the walk arrive somewhere
        /// it has already been: a tree of real directories has no cycles on any filesystem
        /// this runs on, and paying a lookup per directory to prove it would be paying for
        /// nothing.
        /// </remarks>
        private readonly HashSet<CapFileId>? _entered;

        public Descent(
            Dir root,
            WalkOptions options,
            bool asynchronous,
            CancellationToken cancellationToken = default)
        {
            _options = options;
            _asynchronous = asynchronous;
            _cancellationToken = cancellationToken;

            if (options.FollowSymlinks)
            {
                _entered = [root.GetMetadata().FileId];
            }

            Push(root, owned: false, states: null);
        }

        /// <summary>The level the walk is reading now, or null when it has finished.</summary>
        public WalkLevel? Level => _levels.Count == 0 ? null : _levels[^1];

        /// <summary>How deep the entries of the current level are.</summary>
        public int Depth => _levels.Count;

        /// <summary>Whether an entry is one the caller asked not to see.</summary>
        public bool Skips(DirEntry entry) => _options.SkipHidden && IsHidden(entry);

        /// <summary>
        /// Descends into an entry, if it is something to descend into and the walk may go
        /// deeper.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The open decides what the entry is, rather than the kind the directory read
        /// reported. That kind is a snapshot taken earlier and several filesystems decline to
        /// give one at all, so it is used only to avoid opening the names that plainly are not
        /// directories; a name that claims to be one and is not fails the open, which is the
        /// same answer arrived at without trusting the claim.
        /// </para>
        /// <para>
        /// The depth is checked after the open rather than before it, so a tree that is deep
        /// in entries that turn out not to be directories is not refused for a descent that
        /// was never going to happen.
        /// </para>
        /// </remarks>
        public void Enter(DirEntry entry, int[]? states = null)
        {
            if (!MayDescend(entry.Type) || !entry.TryOpenDir(out Dir? child))
            {
                return;
            }

            bool kept = false;
            try
            {
                if (_levels.Count >= _options.MaxDepth)
                {
                    throw FailureTranslation.ToException(
                        CapError.FromCategory(CapErrorCategory.PathTooDeep),
                        entry.Name,
                        ExpectedTarget.Directory);
                }

                if (_entered is not null && !_entered.Add(child.GetMetadata().FileId))
                {
                    // Already on the way down to here, so entering it again is a loop rather
                    // than a subtree. Reported as an entry like any other and not descended
                    // into; the alternative is a walk that never ends.
                    return;
                }

                Push(child, owned: true, states);
                kept = true;
            }
            finally
            {
                if (!kept)
                {
                    child.Dispose();
                }
            }
        }

        /// <summary>Closes the current level and returns to the one above it.</summary>
        public void Leave()
        {
            WalkLevel level = _levels[^1];
            _levels.RemoveAt(_levels.Count - 1);
            _ = _entered?.Remove(level.Id);
            level.Dispose();
        }

        /// <summary>Closes the current level, for the form that reads asynchronously.</summary>
        public async ValueTask LeaveAsync()
        {
            WalkLevel level = _levels[^1];
            _levels.RemoveAt(_levels.Count - 1);
            _ = _entered?.Remove(level.Id);
            await level.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>Closes every level still open.</summary>
        public void Dispose()
        {
            for (int i = _levels.Count - 1; i >= 0; i--)
            {
                _levels[i].Dispose();
            }

            _levels.Clear();
        }

        /// <summary>Closes every level still open, for the form that reads asynchronously.</summary>
        public async ValueTask DisposeAsync()
        {
            for (int i = _levels.Count - 1; i >= 0; i--)
            {
                await _levels[i].DisposeAsync().ConfigureAwait(false);
            }

            _levels.Clear();
        }

        /// <summary>Whether the kind a directory read reported is worth trying to open.</summary>
        /// <remarks>
        /// A link is a candidate only when the caller asked for links to be followed, and even
        /// then the open is subject to the handle's own policy, which may refuse it. An entry
        /// the filesystem declined to classify is a candidate because it might be a directory,
        /// and the cost of being wrong is one refused open.
        /// </remarks>
        private bool MayDescend(CapFileType type) => type switch
        {
            CapFileType.Directory or CapFileType.Unknown => true,
            CapFileType.Symlink => _options.FollowSymlinks,
            _ => false,
        };

        /// <summary>Opens a directory's entries and makes it the level the walk is reading.</summary>
        private void Push(Dir directory, bool owned, int[]? states)
        {
            CapFileId id = _entered is null ? default : directory.GetMetadata().FileId;

            WalkLevel level = _asynchronous
                ? new WalkLevel(
                    directory,
                    owned,
                    id,
                    directory.EnumerateEntriesAsync(_cancellationToken).GetAsyncEnumerator(_cancellationToken))
                : new WalkLevel(directory, owned, id, directory.EnumerateEntries().GetEnumerator());

            level.States = states;
            _levels.Add(level);
        }

        /// <summary>
        /// Whether the platform considers an entry hidden.
        /// </summary>
        /// <remarks>
        /// The leading dot is checked first and on every platform, because it costs nothing
        /// and is the convention a tree is most likely to have been built with. The attribute
        /// is asked for only where a platform records one, and only for the names the dot did
        /// not already answer — an entry that has gone since the directory was read is not
        /// hidden, it is absent, and the walk reports it rather than inventing a reason to
        /// leave it out.
        /// </remarks>
        private static bool IsHidden(DirEntry entry)
        {
            if (entry.Name.StartsWith('.'))
            {
                return true;
            }

            if (!OperatingSystem.IsWindows() || !entry.TryGetMetadata(out CapMetadata metadata))
            {
                return false;
            }

            return metadata.Permissions.TryGetWindowsAttributes(out FileAttributes attributes) &&
                   (attributes & FileAttributes.Hidden) != 0;
        }
    }

    /// <summary>One open directory the walk is inside, and its unfinished reading of it.</summary>
    private sealed class WalkLevel
    {
        private readonly bool _owned;

        public WalkLevel(Dir directory, bool owned, CapFileId id, IEnumerator<DirEntry> entries)
        {
            Directory = directory;
            _owned = owned;
            Id = id;
            Entries = entries;
        }

        public WalkLevel(Dir directory, bool owned, CapFileId id, IAsyncEnumerator<DirEntry> entries)
        {
            Directory = directory;
            _owned = owned;
            Id = id;
            AsyncEntries = entries;
        }

        /// <summary>The directory, open for as long as the walk is inside it.</summary>
        public Dir Directory { get; }

        /// <summary>Its identity, kept only when the walk is watching for cycles.</summary>
        public CapFileId Id { get; }

        /// <summary>
        /// Which pieces of a pattern are still live in this directory, for a walk a pattern is
        /// driving.
        /// </summary>
        /// <remarks>
        /// Carried on the level rather than worked out again, because it is the whole of what
        /// a pattern-driven walk knows that an ordinary one does not: it says which names here
        /// are matches and, more usefully, which directories here are worth entering at all. A
        /// plain walk leaves it empty.
        /// </remarks>
        public int[]? States { get; set; }

        /// <summary>The reading in progress, for a walk that reads on the calling thread.</summary>
        public IEnumerator<DirEntry>? Entries { get; }

        /// <summary>The reading in progress, for a walk that does not.</summary>
        public IAsyncEnumerator<DirEntry>? AsyncEntries { get; }

        /// <summary>Stops the reading and closes the directory, if this level opened it.</summary>
        /// <remarks>
        /// Only ever called on a level a walk that reads on the calling thread created. A walk
        /// is one kind or the other for its whole life, so there is no reading here that would
        /// have to be waited for.
        /// </remarks>
        public void Dispose()
        {
            Entries?.Dispose();
            Close();
        }

        /// <summary>Stops the reading and closes the directory, waiting for the reading to stop.</summary>
        public async ValueTask DisposeAsync()
        {
            Entries?.Dispose();
            if (AsyncEntries is not null)
            {
                await AsyncEntries.DisposeAsync().ConfigureAwait(false);
            }

            Close();
        }

        private void Close()
        {
            if (_owned)
            {
                Directory.Dispose();
            }
        }
    }
}
