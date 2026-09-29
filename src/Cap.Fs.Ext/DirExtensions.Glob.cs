using Cap.Std;

namespace Cap.Fs.Ext;

/// <summary>
/// Finding the names in a tree that a pattern describes.
/// </summary>
/// <remarks>
/// <para>
/// A walk that the pattern steers. Each directory carries the set of pattern pieces still
/// live in it, and an entry is entered only when some piece could still match something
/// beneath it — so a pattern anchored near the top of a tree costs a read of the directories
/// on the way and nothing for the rest of the tree, and a pattern that crosses levels costs a
/// full walk, which is what it asked for.
/// </para>
/// <para>
/// What comes back are entries, not paths. That is the same decision the walk makes and for
/// the same reason: a match handed back as a string would be resolved again by whatever
/// received it, with the process's own privileges and none of the confinement the search was
/// performed under.
/// </para>
/// </remarks>
public static partial class DirExtensions
{
    /// <summary>
    /// Finds everything beneath this handle whose name the pattern describes.
    /// </summary>
    /// <param name="dir">The directory to search.</param>
    /// <param name="pattern">The pattern, in the syntax <see cref="GlobPattern"/> describes.</param>
    /// <param name="options">How the search descends, or null for the defaults.</param>
    /// <returns>The matching entries, parents before their children.</returns>
    /// <remarks>
    /// <para>
    /// Lazy and re-run from the start each time it is enumerated, exactly as a walk is. Each
    /// entry is usable only while it is the current one, for the reason
    /// <see cref="WalkEntry"/> gives.
    /// </para>
    /// <para>
    /// A piece of the pattern matches a name beginning with a dot like any other, unlike a
    /// shell. The names come from a directory read rather than from a command line, hiding
    /// some of them would be a second rule to learn, and a caller who wants them left out asks
    /// for that in <see cref="WalkOptions.SkipHidden"/> — where it also applies to the
    /// directories the search would otherwise descend into.
    /// </para>
    /// <para>
    /// The pattern's separators are the ones the handle reads a path with: its filesystem's
    /// own for a <see cref="Dir"/>, and the machine's for any other <see cref="IDir"/>, which
    /// has no way to say.
    /// </para>
    /// <para>
    /// Safe to enumerate any number of times from any number of threads at once, as a walk
    /// is; each enumerator is for one consumer at a time.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> A link is matched by its own name and yielded when the
    /// pattern describes that name, whatever it points at. It is searched through only as
    /// <see cref="Walk"/> would descend into it — under
    /// <see cref="WalkOptions.FollowSymlinks"/>, the handle's policy and the subtree's bounds —
    /// and only when some piece of the pattern is still live beneath it.
    /// </para>
    /// <para>
    /// <strong>Directories that cannot be opened</strong> fail the search as they fail a
    /// walk, unless <see cref="WalkOptions.OnError"/> says to go on without them — but only
    /// the ones the search tries to enter. A directory no piece of the pattern could match
    /// through is never opened, so it cannot fail.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The pattern is not one that can be matched.</exception>
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
    public static IEnumerable<WalkEntry> Glob(this IDir dir, string pattern, WalkOptions? options = null) =>
        Glob(dir, ParsePattern(dir, pattern, ignoreCase: false), options);

    /// <summary>
    /// Finds everything beneath this handle whose name the pattern describes, saying whether
    /// letters match regardless of spelling.
    /// </summary>
    /// <param name="dir">The directory to search.</param>
    /// <param name="pattern">The pattern, in the syntax <see cref="GlobPattern"/> describes.</param>
    /// <param name="ignoreCase">
    /// Whether letters match regardless of spelling, compared by the invariant culture's rules
    /// as <see cref="GlobPattern.Parse(string, bool)"/> compares them.
    /// </param>
    /// <param name="options">How the search descends, or null for the defaults.</param>
    /// <returns>The matching entries, parents before their children.</returns>
    /// <remarks>
    /// <para>
    /// The search <see cref="Glob(IDir, string, WalkOptions?)"/> performs, with the choice about
    /// case made here. The pattern is divided as the handle reads a path, which
    /// <see cref="GlobPattern.Parse(string, bool)"/> cannot do: it has no handle to ask, and
    /// divides by the running machine's rules.
    /// </para>
    /// <para>
    /// Entries, <strong>symbolic links</strong> and directories that cannot be opened are
    /// treated exactly as the form without the choice treats them, and it is as safe to use
    /// from several threads.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The pattern is not one that can be matched.</exception>
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
    public static IEnumerable<WalkEntry> Glob(this IDir dir, string pattern, bool ignoreCase, WalkOptions? options = null) =>
        Glob(dir, ParsePattern(dir, pattern, ignoreCase), options);

    /// <summary>Reads a pattern given as text as the handle it will search beneath reads a path.</summary>
    private static GlobPattern ParsePattern(IDir dir, string pattern, bool ignoreCase)
    {
        ArgumentNullException.ThrowIfNull(dir);
        return GlobPattern.Parse(pattern, ignoreCase, Handles.SyntaxOf(dir));
    }

    /// <summary>
    /// Finds everything beneath this handle whose name a pattern read in advance describes.
    /// </summary>
    /// <param name="dir">The directory to search.</param>
    /// <param name="pattern">The pattern.</param>
    /// <param name="options">How the search descends, or null for the defaults.</param>
    /// <returns>The matching entries, parents before their children.</returns>
    /// <remarks>
    /// <para>
    /// The form to use when the same pattern is applied more than once: the pattern is divided
    /// into its pieces when it is parsed, and a pattern parsed once is matched without that
    /// work being repeated.
    /// </para>
    /// <para>
    /// Safe to enumerate any number of times from any number of threads at once, and one
    /// pattern may drive several searches concurrently; each enumerator is for one consumer
    /// at a time.
    /// </para>
    /// <para>
    /// <strong>Symbolic links</strong>, and directories that cannot be opened, are treated
    /// exactly as the form taking the pattern as text treats them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
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
    public static IEnumerable<WalkEntry> Glob(this IDir dir, GlobPattern pattern, WalkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(pattern);
        WalkOptions settings = Demand(options);

        return Walking(dir, pattern, settings);
    }

    /// <summary>
    /// Finds everything beneath this handle whose name a pattern describes, without holding the
    /// calling thread.
    /// </summary>
    /// <param name="dir">The directory to search.</param>
    /// <param name="pattern">
    /// The pattern, read as <see cref="Glob(IDir, string, WalkOptions?)"/> reads it.
    /// </param>
    /// <param name="options">How the search descends, or null for the defaults.</param>
    /// <param name="cancellationToken">Stops the search between batches of entries.</param>
    /// <returns>The matching entries, as <see cref="Glob(IDir, string, WalkOptions?)"/> produces them.</returns>
    /// <remarks>
    /// <para>
    /// The same search, with each directory read as
    /// <see cref="WalkAsync(IDir, WalkOptions?, CancellationToken)"/> reads it: on a thread-pool
    /// thread, with the opens between levels on whichever thread the enumeration resumes on.
    /// The pattern is read before the sequence is handed back, so a pattern that cannot be
    /// matched is refused at the call rather than at the first step.
    /// </para>
    /// <para>
    /// As for the synchronous form, the sequence may be enumerated any number of times,
    /// concurrently included, and each enumerator is for one consumer at a time.
    /// </para>
    /// <para>
    /// <strong>Symbolic links</strong>, and directories that cannot be opened, are treated
    /// exactly as the synchronous form treats them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The pattern is not one that can be matched.</exception>
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
    public static IAsyncEnumerable<WalkEntry> GlobAsync(
        this IDir dir,
        string pattern,
        WalkOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GlobAsync(dir, ParsePattern(dir, pattern, ignoreCase: false), options, cancellationToken);

    /// <summary>
    /// Finds everything beneath this handle whose name a pattern describes, saying whether
    /// letters match regardless of spelling, without holding the calling thread.
    /// </summary>
    /// <param name="dir">The directory to search.</param>
    /// <param name="pattern">
    /// The pattern, read as <see cref="Glob(IDir, string, bool, WalkOptions?)"/> reads it.
    /// </param>
    /// <param name="ignoreCase">Whether letters match regardless of spelling.</param>
    /// <param name="options">How the search descends, or null for the defaults.</param>
    /// <param name="cancellationToken">Stops the search between batches of entries.</param>
    /// <returns>The matching entries, parents before their children.</returns>
    /// <remarks>
    /// The search <see cref="GlobAsync(IDir, string, WalkOptions?, CancellationToken)"/>
    /// performs, with the choice about case made here and the pattern divided as the handle
    /// reads a path. The pattern is read before the sequence is handed back, so one that
    /// cannot be matched is refused at the call.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The pattern is not one that can be matched.</exception>
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
    public static IAsyncEnumerable<WalkEntry> GlobAsync(
        this IDir dir,
        string pattern,
        bool ignoreCase,
        WalkOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GlobAsync(dir, ParsePattern(dir, pattern, ignoreCase), options, cancellationToken);

    /// <summary>
    /// Finds everything beneath this handle whose name a pattern read in advance describes,
    /// without holding the calling thread.
    /// </summary>
    /// <param name="dir">The directory to search.</param>
    /// <param name="pattern">The pattern.</param>
    /// <param name="options">How the search descends, or null for the defaults.</param>
    /// <param name="cancellationToken">Stops the search between batches of entries.</param>
    /// <returns>The matching entries, parents before their children.</returns>
    /// <remarks>
    /// <para>
    /// The form to use when the same pattern is applied more than once, as for
    /// <see cref="Glob(IDir, GlobPattern, WalkOptions?)"/>; one pattern may drive several
    /// searches concurrently, in either form.
    /// </para>
    /// <para>
    /// <strong>Symbolic links</strong>, and directories that cannot be opened, are treated
    /// exactly as the synchronous form treats them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
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
    public static IAsyncEnumerable<WalkEntry> GlobAsync(
        this IDir dir,
        GlobPattern pattern,
        WalkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(pattern);
        WalkOptions settings = Demand(options);

        return WalkingAsync(dir, pattern, settings, cancellationToken);
    }
}
