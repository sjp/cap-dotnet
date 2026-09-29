using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
    /// <para>
    /// The sequence may be enumerated any number of times, from any number of threads at
    /// once: each enumeration opens its own handles and shares nothing with another but the
    /// starting handle, which is safe for concurrent use. A single enumerator is not, and is
    /// for one consumer at a time, as any enumerator is.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> A link inside the tree is yielded as an entry of its
    /// own, reporting <see cref="CapFileType.Symlink"/>, and is not descended into unless
    /// <see cref="WalkOptions.FollowSymlinks"/> asks for that, the starting handle's policy
    /// allows it and the link resolves inside the subtree; a link leading out is yielded and
    /// never entered. Following turns on the cycle check described there. When links are not
    /// followed, every descent is an open that refuses a link, so a link is never entered
    /// however the directory read reported it: a directory swapped for one after it was
    /// listed, or a link on a filesystem that does not say what its entries are, is yielded
    /// and not descended into.
    /// </para>
    /// <para>
    /// <strong>Nothing is left out silently.</strong> A name that is not a directory to enter
    /// — gone since it was listed, a link not being followed, one leading out of the subtree —
    /// is yielded and not descended into. A directory that is there and cannot be opened, for
    /// want of permission, of handles or of a working device, fails the walk, unless
    /// <see cref="WalkOptions.OnError"/> says to go on without it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="WalkOptions.MaxDepth"/> is less than one.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// A directory could not be opened or read for want of permission, and
    /// <see cref="WalkOptions.OnError"/> did not say to go on without it.
    /// </exception>
    /// <exception cref="CapIOException">
    /// The tree descends past <see cref="WalkOptions.MaxDepth"/>, or a directory could not be
    /// opened or read, and <see cref="WalkOptions.OnError"/> did not say to go on without it.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static IEnumerable<WalkEntry> Walk(this IDir dir, WalkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dir);
        WalkOptions settings = Demand(options);

        return Walking(dir, pattern: null, settings);
    }

    /// <summary>
    /// Walks everything beneath this handle without holding the calling thread.
    /// </summary>
    /// <param name="dir">The directory to walk.</param>
    /// <param name="options">What the walk does with what it finds, or null for the defaults.</param>
    /// <param name="cancellationToken">Stops the walk between batches of entries.</param>
    /// <returns>The entries, as <see cref="Walk"/> produces them.</returns>
    /// <remarks>
    /// <para>
    /// The reading is not asynchronous and nothing here claims it is: no operating system this
    /// runs on offers a directory read that completes by itself, so what this does is have a
    /// thread-pool thread do the waiting. The opens between levels happen on whichever thread
    /// the enumeration resumes on, for the same reason.
    /// </para>
    /// <para>
    /// As for <see cref="Walk"/>, the sequence may be enumerated any number of times,
    /// concurrently included, and each enumerator is for one consumer at a time: a second
    /// <c>MoveNextAsync</c> must not be started before the previous one has completed.
    /// </para>
    /// <para>
    /// <strong>Symbolic links</strong>, and directories that cannot be opened, are treated
    /// exactly as <see cref="Walk"/> treats them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="WalkOptions.MaxDepth"/> is less than one.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// A directory could not be opened or read for want of permission, and
    /// <see cref="WalkOptions.OnError"/> did not say to go on without it.
    /// </exception>
    /// <exception cref="CapIOException">
    /// The tree descends past <see cref="WalkOptions.MaxDepth"/>, or a directory could not be
    /// opened or read, and <see cref="WalkOptions.OnError"/> did not say to go on without it.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static IAsyncEnumerable<WalkEntry> WalkAsync(
        this IDir dir,
        WalkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dir);
        WalkOptions settings = Demand(options);

        return WalkingAsync(dir, pattern: null, settings, cancellationToken);
    }

    /// <summary>Checks the settings a walk was given, or supplies the defaults.</summary>
    private static WalkOptions Demand(WalkOptions? options)
    {
        WalkOptions settings = options ?? WalkOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxDepth, 1, nameof(options));
        return settings;
    }

    /// <summary>
    /// The walk, and the search a pattern drives, in the form that reads on the calling thread.
    /// </summary>
    /// <remarks>
    /// One iterator for both, and one more for both in the form that reads asynchronously, so
    /// that what a search does differently from a walk is written once and the two forms of
    /// each cannot drift apart. Without a pattern every entry is yielded and every directory
    /// is a candidate to enter; with one, only the entries it matches are yielded and only the
    /// directories it could still match through are entered.
    /// </remarks>
    private static IEnumerable<WalkEntry> Walking(IDir root, GlobPattern? pattern, WalkOptions options)
    {
        Descent descent = new(root, options, asynchronous: false);

        try
        {
            descent.Level!.States = pattern?.Start();

            while (descent.Level is { } level)
            {
                if (!level.Reader.TryNext(out ListedEntry entry))
                {
                    descent.Leave();
                    continue;
                }

                if (descent.Skips(in entry))
                {
                    continue;
                }

                descent.Admit();

                if (pattern is null)
                {
                    yield return new WalkEntry(level.Directory, entry, descent.Depth);

                    descent.Enter(in entry);
                    continue;
                }

                int[]? beneath = pattern.Step(level.States, entry.Name, out bool matched);

                if (matched)
                {
                    yield return new WalkEntry(level.Directory, entry, descent.Depth);
                }

                if (beneath is not null)
                {
                    // Nothing else is entered. A directory no remaining piece of the pattern
                    // could match through is not read at all, which is the whole difference
                    // between this and walking the tree and filtering afterwards.
                    descent.Enter(in entry, beneath);
                }
            }
        }
        finally
        {
            descent.Dispose();
        }
    }

    /// <summary>
    /// The walk, and the search a pattern drives, in the form that reads asynchronously.
    /// </summary>
    /// <remarks>See <see cref="Walking"/>, which this mirrors step for step.</remarks>
    private static async IAsyncEnumerable<WalkEntry> WalkingAsync(
        IDir root,
        GlobPattern? pattern,
        WalkOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Descent descent = new(root, options, asynchronous: true, cancellationToken);

        try
        {
            descent.Level!.States = pattern?.Start();

            while (descent.Level is { } level)
            {
                if (!await level.Reader.MoveNextAsync().ConfigureAwait(false))
                {
                    await descent.LeaveAsync().ConfigureAwait(false);
                    continue;
                }

                ListedEntry entry = level.Reader.Current;
                if (descent.Skips(in entry))
                {
                    continue;
                }

                descent.Admit();

                if (pattern is null)
                {
                    yield return new WalkEntry(level.Directory, entry, descent.Depth);

                    descent.Enter(in entry);
                    continue;
                }

                int[]? beneath = pattern.Step(level.States, entry.Name, out bool matched);

                if (matched)
                {
                    yield return new WalkEntry(level.Directory, entry, descent.Depth);
                }

                if (beneath is not null)
                {
                    descent.Enter(in entry, beneath);
                }
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
            IDir root,
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

            Push(root, name: null, owned: false, states: null);
        }

        /// <summary>The level the walk is reading now, or null when it has finished.</summary>
        public WalkLevel? Level => _levels.Count == 0 ? null : _levels[^1];

        /// <summary>How deep the entries of the current level are.</summary>
        public int Depth => _levels.Count;

        /// <summary>Whether an entry is one the caller asked not to see.</summary>
        public bool Skips(in ListedEntry entry) => _options.SkipHidden && IsHidden(_levels[^1].Directory, in entry);

        /// <summary>
        /// Fails the walk if the entry just read lies deeper than
        /// <see cref="WalkOptions.MaxDepth"/>.
        /// </summary>
        /// <remarks>
        /// Asked of each entry the caller would be shown, so what fails the walk is an entry
        /// that exists past the limit, not a directory at the limit that might have held one.
        /// The directory named is the one at the limit, the last the walk was allowed to read.
        /// </remarks>
        public void Admit()
        {
            if (_levels.Count > _options.MaxDepth)
            {
                throw FailureTranslation.ToException(
                    CapError.FromCategory(CapErrorCategory.PathTooDeep),
                    _levels[^1].Name!,
                    ExpectedTarget.Directory);
            }
        }

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
        /// same answer arrived at without trusting the claim. What the open refused with is
        /// what separates such a name from a directory that is there and could not be
        /// opened, which is never skipped without the caller's say.
        /// </para>
        /// <para>
        /// The same holds for links. When they are not being followed the open itself refuses
        /// one, rather than relying on the read having called it a link: an entry reported as
        /// a directory or as unclassified that is in fact a link fails the open and is not
        /// entered. The handle that open produces still carries the starting handle's policy,
        /// so what a caller can do through an entry's directory does not depend on its depth.
        /// </para>
        /// <para>
        /// The depth is not checked here. A directory at the limit is entered like any other,
        /// and the walk fails only when <see cref="Admit"/> finds an entry inside it, so an
        /// empty directory at the limit is walked and one with anything in it is refused. That
        /// costs one level past the limit, and never more, because the refusal comes at the
        /// first entry that level yields.
        /// </para>
        /// </remarks>
        public void Enter(in ListedEntry entry, int[]? states = null)
        {
            if (!MayDescend(entry.Type) || !TryOpen(in entry, out IDir? child))
            {
                return;
            }

            bool kept = false;
            try
            {
                if (_entered is not null && !_entered.Add(child.GetMetadata().FileId))
                {
                    // Already on the way down to here, so entering it again is a loop rather
                    // than a subtree. Reported as an entry like any other and not descended
                    // into; the alternative is a walk that never ends.
                    return;
                }

                Push(child, entry.Name, owned: true, states);
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

        /// <summary>
        /// Opens an entry as the next level down, or says there is nothing there to enter.
        /// </summary>
        /// <remarks>
        /// A directory that is there and could not be opened fails the walk, unless
        /// <see cref="WalkOptions.OnError"/> is given the failure and says to go on without it.
        /// </remarks>
        private bool TryOpen(in ListedEntry entry, [NotNullWhen(true)] out IDir? child)
        {
            Exception? failure = Open(in entry, out child);
            if (failure is null)
            {
                return child is not null;
            }

            if (failure is IOException or UnauthorizedAccessException &&
                _options.OnError is { } onError &&
                onError(new WalkEntry(_levels[^1].Directory, entry, Depth), failure))
            {
                return false;
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
            return false;
        }

        /// <summary>Opens an entry as a directory, following a link only when asked to.</summary>
        /// <returns>
        /// Null, with <paramref name="child"/> set when the entry was opened and null when it
        /// is not something to enter; otherwise why a directory that is there did not open.
        /// </returns>
        /// <remarks>
        /// When links are not followed a <see cref="Dir"/> refuses one anywhere in the
        /// resolution. Any other handle is asked not to follow one at the last component, which
        /// for the single name used here is the whole of the resolution.
        /// </remarks>
        private Exception? Open(in ListedEntry entry, out IDir? child)
        {
            child = null;
            IDir directory = _levels[^1].Directory;

            if (directory is Dir concrete)
            {
                CapError error = concrete.OpenDirForWalk(entry.Name, refuseLinks: !_options.FollowSymlinks, out Dir? opened);
                if (error.IsSuccess)
                {
                    child = opened;
                    return null;
                }

                return LeavesNothingToEnter(FailureTranslation.KindOf(error.Category))
                    ? null
                    : FailureTranslation.ToException(error, entry.Name, ExpectedTarget.Directory);
            }

            try
            {
                child = directory.OpenDir(entry.Name, noFollow: !_options.FollowSymlinks);
                return null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return LeavesNothingToEnter(CapIOException.KindOf(exception)) ? null : exception;
            }
        }

        /// <summary>
        /// Whether a failed open means the name is not a directory the walk should enter,
        /// rather than one it could not.
        /// </summary>
        /// <remarks>
        /// The name has gone, is not a directory, is a link the walk is not following, is a
        /// chain of links that never arrives, leads out of the subtree, or changed while it
        /// was being opened. Each is a name that was never going to add entries beneath it,
        /// and each is still yielded as an entry of its own. Anything else — the filesystem
        /// refused, the process ran out of handles, the device failed — is a directory whose
        /// contents are missing from the answer.
        /// </remarks>
        private static bool LeavesNothingToEnter(CapErrorKind kind) => kind is
            CapErrorKind.NotFound or
            CapErrorKind.NotADirectory or
            CapErrorKind.SymbolicLink or
            CapErrorKind.LinkNotFollowed or
            CapErrorKind.Escaped or
            CapErrorKind.ConcurrentChange;

        /// <summary>Opens a directory's entries and makes it the level the walk is reading.</summary>
        private void Push(IDir directory, string? name, bool owned, int[]? states)
        {
            CapFileId id = _entered is null ? default : directory.GetMetadata().FileId;

            WalkLevel level = new(
                directory,
                name,
                owned,
                id,
                _asynchronous
                    ? EntryReader.OpenAsync(directory, _cancellationToken)
                    : EntryReader.Open(directory));

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
        /// leave it out. Whether the platform records one is the handle's to say rather than the
        /// running machine's, since a filesystem held in memory can keep Windows attributes on
        /// a machine that is not Windows.
        /// </remarks>
        private static bool IsHidden(IDir directory, in ListedEntry entry)
        {
            if (entry.Name.StartsWith('.'))
            {
                return true;
            }

            if (Handles.SyntaxOf(directory) != CapPathSyntax.Windows || !entry.TryGetMetadata(out CapMetadata metadata))
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

        /// <summary>The reading in progress, in whichever form the walk reads.</summary>
        private readonly EntryReader _reader;

        public WalkLevel(IDir directory, string? name, bool owned, CapFileId id, EntryReader reader)
        {
            Directory = directory;
            Name = name;
            _owned = owned;
            Id = id;
            _reader = reader;
        }

        /// <summary>The directory, open for as long as the walk is inside it.</summary>
        public IDir Directory { get; }

        /// <summary>
        /// The name it was entered by, for naming it in a failure. Null for the directory the
        /// walk started at.
        /// </summary>
        public string? Name { get; }

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

        /// <summary>The reading in progress, reached in place rather than copied out per entry.</summary>
        public ref readonly EntryReader Reader => ref _reader;

        /// <summary>Stops the reading and closes the directory, if this level opened it.</summary>
        /// <remarks>
        /// Only ever called on a level a walk that reads on the calling thread created. A walk
        /// is one kind or the other for its whole life, so there is no reading here that would
        /// have to be waited for.
        /// </remarks>
        public void Dispose()
        {
            Reader.Dispose();
            Close();
        }

        /// <summary>Stops the reading and closes the directory, waiting for the reading to stop.</summary>
        public async ValueTask DisposeAsync()
        {
            await Reader.DisposeAsync().ConfigureAwait(false);
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
