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
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The pattern is not one that can be matched.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="WalkOptions.MaxDepth"/> is less than one.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">A directory could not be read.</exception>
    /// <exception cref="CapIOException">
    /// The tree descends past <see cref="WalkOptions.MaxDepth"/>, or a directory could not be
    /// read.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static IEnumerable<WalkEntry> Glob(this Dir dir, string pattern, WalkOptions? options = null) =>
        Glob(dir, ParsePattern(dir, pattern), options);

    /// <summary>Reads a pattern given as text as the handle it will search beneath reads a path.</summary>
    private static GlobPattern ParsePattern(Dir dir, string pattern)
    {
        ArgumentNullException.ThrowIfNull(dir);
        return GlobPattern.Parse(pattern, ignoreCase: false, dir.PathSyntax);
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
    /// <strong>Symbolic links.</strong> Treated exactly as the form taking the pattern as text
    /// treats them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="WalkOptions.MaxDepth"/> is less than one.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">A directory could not be read.</exception>
    /// <exception cref="CapIOException">
    /// The tree descends past <see cref="WalkOptions.MaxDepth"/>, or a directory could not be
    /// read.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static IEnumerable<WalkEntry> Glob(this Dir dir, GlobPattern pattern, WalkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(pattern);
        WalkOptions settings = Demand(options);

        return Globbing(dir, pattern, settings);
    }

    private static IEnumerable<WalkEntry> Globbing(Dir root, GlobPattern pattern, WalkOptions options)
    {
        Descent descent = new(root, options, asynchronous: false);

        try
        {
            descent.Level!.States = pattern.Start();

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
                    descent.Enter(entry, beneath);
                }
            }
        }
        finally
        {
            descent.Dispose();
        }
    }
}
