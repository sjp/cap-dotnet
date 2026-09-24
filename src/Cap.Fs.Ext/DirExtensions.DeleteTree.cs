using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Std;

namespace Cap.Fs.Ext;

/// <summary>
/// Removing a directory and everything inside it.
/// </summary>
/// <remarks>
/// <para>
/// This is where sandbox libraries get their vulnerabilities, and the flawed version is the
/// one that looks obvious: list the directory, join each name onto the directory's path, and
/// delete the resulting strings. It works until something replaces a directory in the middle
/// of the tree with a symbolic link between the listing and the deletion, at which point the
/// joined path names somewhere else and the deletion goes there — carried out with the
/// caller's own privileges, on files the caller never meant to touch, and invisible to any
/// test written about ordinary trees.
/// </para>
/// <para>
/// So this is a walk and not a call. Each directory is descended by opening its entry through
/// the handle that listed it, an open that refuses to follow a link and refuses to leave the
/// subtree; each entry is removed by its single name against the handle of the directory it
/// was found in. A name swapped for a link after it was listed cannot be descended into, and
/// unlinking it removes the link rather than what it points at. The worst that anything
/// writing inside the tree can achieve is that something it owns is left behind.
/// </para>
/// <para>
/// <strong>It is not atomic, and nothing can make it so.</strong> It is many operations, and
/// an entry created while it runs may or may not be removed. What it guarantees is that every
/// one of those operations lands inside the subtree the handle covers.
/// </para>
/// </remarks>
public static partial class DirExtensions
{
    /// <summary>
    /// Removes a directory beneath this handle, and everything inside it.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the directory to remove.</param>
    /// <remarks>
    /// <para>
    /// The path names a directory, and only a directory. A name holding a file is not removed
    /// and neither is a name holding a symbolic link — not even one pointing at a directory,
    /// because following it would take the removal outside the tree it was asked about.
    /// Removing either of those is removing a name, which is the single-name deletion's
    /// business.
    /// </para>
    /// <para>
    /// A failure part of the way through leaves the tree partly removed. The first failure is
    /// the one reported and the walk carries on past it, so a single entry that cannot be
    /// removed does not leave behind the rest of a tree that had nothing to do with it.
    /// </para>
    /// <para>
    /// Safe to call from any thread, and concurrently with anything else on the same tree,
    /// another removal included: every step stays inside the subtree whatever else is
    /// happening. Two removals racing over one tree do not coordinate, though, and either may
    /// report a failure for an entry the other removed first.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> Components ahead of the last are resolved under this
    /// handle's own policy, so a link among them is followed while it stays inside the subtree
    /// or refused as for any other operation. A link as the last component is refused and left
    /// in place, not followed and not removed. A link anywhere inside the tree is removed as
    /// the link, and what it points at is not reached; a directory swapped for a link while
    /// the removal runs is not entered, and its name is removed as the link it now is.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">There is no such directory.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused a removal.</exception>
    /// <exception cref="CapIOException">
    /// The name holds something that is not a directory, or the tree could not be removed.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static void DeleteTree(this Dir dir, string path)
    {
        using ParentLocation location = ParentLocation.Resolve(dir, path, nameof(path), mayNameDirectory: true);

        CapError error = location.Refusal.IsFailure
            ? location.Refusal
            : TreeRemoval.Remove(location.Directory, location.Name);
        if (error.IsFailure)
        {
            throw FailureTranslation.ToException(error, path, ExpectedTarget.Directory);
        }
    }

    /// <summary>
    /// Removes a directory beneath this handle and everything inside it, reporting failure
    /// rather than throwing.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the directory. See <see cref="DeleteTree"/>.</param>
    /// <returns>True when the directory and everything in it are gone.</returns>
    /// <remarks>
    /// <para>
    /// The form to prefer when clearing up. A tree that has already been removed — by a
    /// previous attempt, by whatever created it, by another process tidying the same place —
    /// is an ordinary outcome, and building an exception to describe it is work done on the
    /// path that runs most often.
    /// </para>
    /// <para>
    /// Safe to call from any thread, on the same terms as <see cref="DeleteTree"/>: racing
    /// removals stay contained, and either may answer false for an entry the other removed.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> As for <see cref="DeleteTree"/>: a link ahead of the
    /// last component is resolved under the handle's policy, a link as the last component
    /// answers false and is left in place, and a link inside the tree is removed as the link.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static bool TryDeleteTree(this Dir dir, string path)
    {
        using ParentLocation location = ParentLocation.Resolve(dir, path, nameof(path), mayNameDirectory: true);

        return location.Refusal.IsSuccess && TreeRemoval.Remove(location.Directory, location.Name).IsSuccess;
    }

    /// <summary>
    /// Removes everything inside this directory, leaving the directory itself.
    /// </summary>
    /// <param name="dir">The directory to empty.</param>
    /// <remarks>
    /// <para>
    /// Takes the directory as a handle rather than as a name beneath one, which is what a
    /// caller holding the root of a sandbox has: there is no handle above it for a name to be
    /// used against, and there is not meant to be. Going back to a name in order to start the
    /// work would introduce a lookup where there was none, and a lookup can be answered
    /// differently the second time.
    /// </para>
    /// <para>
    /// The emptying is performed through a handle that refuses symbolic links, whatever policy
    /// the handle passed in carries, for the reason the named form gives: a directory that
    /// turns into a link between being listed and being entered would otherwise send part of
    /// the removal somewhere else.
    /// </para>
    /// <para>
    /// Safe to call from any thread, and concurrently with anything else on the same
    /// directory, another emptying included: every step stays inside it. Two emptyings racing
    /// do not coordinate, and either may report a failure for an entry the other removed first.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> There is no path, so nothing is resolved on the way in:
    /// the handle is the directory emptied, however it was reached. Every link inside is
    /// removed as the link, whatever it points at and whether or not its target is inside, and
    /// what it points at is never reached; a directory swapped for a link while the emptying
    /// runs is not entered, and its name is removed as the link.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> is null.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused a removal.</exception>
    /// <exception cref="CapIOException">The directory could not be emptied.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static void DeleteTreeContents(this Dir dir)
    {
        ArgumentNullException.ThrowIfNull(dir);

        CapError error = TreeRemoval.Empty(dir);
        if (error.IsFailure)
        {
            throw FailureTranslation.ToEnumerationException(error);
        }
    }
}
