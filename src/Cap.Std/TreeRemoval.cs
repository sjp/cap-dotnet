using Cap.Primitives;
using Cap.Primitives.Interop;

namespace Cap.Std;

/// <summary>
/// Emptying a directory and removing it, without ever naming anything by a path.
/// </summary>
/// <remarks>
/// <para>
/// This is the operation sandbox libraries are most often found to have got wrong, and the
/// wrong version is the obvious one: list the directory, join each name onto the directory's
/// path, and delete the resulting strings. That works until somebody replaces a directory in
/// the middle of the tree with a symbolic link between the listing and the deletion, at which
/// point the joined path names something outside the tree and the deletion follows it there.
/// The damage is done by the caller's own privileges, on files the caller never meant to
/// touch, and no test written about ordinary trees would ever show it.
/// </para>
/// <para>
/// So nothing here builds a path. A directory is descended by opening its entry as a
/// directory through the handle that listed it — an open that refuses to follow a link and
/// refuses to leave the subtree — and each entry is removed by its single name against the
/// handle of the directory it was found in. A name swapped for a link after it was listed
/// cannot be descended into, because the open refuses it, and unlinking it removes the link
/// rather than what it points at. The worst an attacker inside the tree can achieve is that
/// something they own is not removed.
/// </para>
/// <para>
/// <strong>This is not atomic, and nothing can make it so.</strong> It is many operations,
/// and an entry created while it runs may or may not be removed. What it guarantees is the
/// containment: every operation it performs lands inside the subtree it was given.
/// </para>
/// </remarks>
internal static class TreeRemoval
{
    /// <summary>
    /// How deep the descent may go before it refuses to go further.
    /// </summary>
    /// <remarks>
    /// The descent holds one open directory per level, and the recursion holds one stack
    /// frame per level, so an unbounded tree is a way to exhaust either. The limit is the
    /// same one resolution applies to a path's components, because the question is the same
    /// question: how far down this library is willing to follow something it did not create.
    /// </remarks>
    private const int MaximumDepth = 256;

    /// <summary>
    /// Removes everything inside an open directory, leaving the directory itself.
    /// </summary>
    /// <returns>
    /// Success when it is empty, or the first failure that stopped it from becoming empty.
    /// </returns>
    /// <remarks>
    /// Takes the directory as a handle rather than as a name beneath one, because the caller
    /// that empties a directory it created has been holding that handle since it created it.
    /// Going back to the name to start the work would introduce a lookup where there was
    /// none, and a lookup is a thing that can be answered differently the second time.
    /// </remarks>
    public static CapError Empty(Dir directory) => EmptyOpen(directory, MaximumDepth);

    /// <summary>
    /// Removes the directory named <paramref name="name"/> beneath <paramref name="parent"/>,
    /// and everything inside it.
    /// </summary>
    /// <param name="parent">The directory holding the name.</param>
    /// <param name="name">A single component naming the directory to remove.</param>
    /// <returns>Success when the name is gone, or the failure that stopped it going.</returns>
    /// <remarks>
    /// <para>
    /// The whole operation in the order it has to happen: open the name as a directory, empty
    /// it through that handle, close the handle, then unlink the name. The close comes before
    /// the unlink because Windows will not remove a directory anything still has open, and
    /// the unlink acts on the name rather than on the handle so that a name swapped for
    /// something else in the meantime is removed as the name it now is rather than followed.
    /// </para>
    /// <para>
    /// <strong>The work is done through a handle that refuses symbolic links,</strong>
    /// whatever policy the caller's own handle carries. Removing a tree is the one operation
    /// where following a link is never what was meant: the name at the top would redirect the
    /// whole removal somewhere else, and a directory further down that turns into a link
    /// between being listed and being entered would do the same to a subtree. Narrowing the
    /// policy for the duration makes both refusals come from resolution rather than from a
    /// check that something could be arranged to pass.
    /// </para>
    /// <para>
    /// A name that is not a directory — a file, a link, or nothing at all — is reported rather
    /// than unlinked. Removing a tree is a request about a directory, and quietly deleting
    /// whatever else was found under the name would make it a request about a name.
    /// </para>
    /// </remarks>
    public static CapError Remove(Dir parent, string name)
    {
        if (!parent.TryRestrict(SymlinkPolicy.Deny, out Dir? strict))
        {
            return CapError.FromCategory(CapErrorCategory.OutOfHandles);
        }

        using (strict)
        {
            if (!strict.TryOpenDir(name, out Dir? directory))
            {
                return Diagnose(parent, name);
            }

            CapError emptied;
            using (directory)
            {
                emptied = EmptyOpen(directory, MaximumDepth);
            }

            return emptied.IsFailure ? emptied : RemoveEmpty(strict, name);
        }
    }

    /// <summary>Says why a name could not be opened as the directory it was meant to be.</summary>
    /// <remarks>
    /// Asked as a description of the name rather than as another attempt to open it, so that
    /// the answer says what is actually there. A link and a file are worth telling apart from
    /// each other and from a name holding nothing, because each means a different mistake in
    /// the calling code. Anything else is left unexplained rather than blamed on a guess.
    /// </remarks>
    private static CapError Diagnose(Dir parent, string name)
    {
        if (!parent.TryGetMetadata(name, out CapMetadata metadata))
        {
            return CapError.FromCategory(CapErrorCategory.NotFound);
        }

        return metadata.Type switch
        {
            CapFileType.Symlink => CapError.FromCategory(CapErrorCategory.SymbolicLink),
            CapFileType.Directory => CapError.FromCategory(CapErrorCategory.Unknown),
            _ => CapError.FromCategory(CapErrorCategory.NotADirectory),
        };
    }

    /// <summary>
    /// Removes an empty directory by name, reporting the platform's answer.
    /// </summary>
    /// <remarks>
    /// The last step of removing a tree, taken after the handle on the directory has been
    /// closed — Windows will not remove a directory anything still has open.
    /// </remarks>
    public static CapError RemoveEmpty(Dir parent, string name) =>
        Unlink(parent, name, directory: true);

    /// <summary>
    /// Removes everything inside the directory named <paramref name="name"/>, leaving the
    /// directory itself.
    /// </summary>
    private static CapError Empty(Dir parent, string name, int remainingDepth)
    {
        if (!parent.TryOpenDir(name, out Dir? directory))
        {
            // The name is not a directory that can be opened: either it has gone already, or
            // something replaced it with a link, or it was never one. None of those is
            // descended into; the caller unlinks the name instead, which acts on the name and
            // not on whatever it now refers to.
            return CapError.Success;
        }

        using (directory)
        {
            return EmptyOpen(directory, remainingDepth);
        }
    }

    /// <summary>Removes everything inside a directory that is already open.</summary>
    /// <remarks>
    /// The reading of the directory is the one step here that reports by throwing, because it
    /// is the public enumeration and that is how the public surface answers. It is caught
    /// rather than allowed out: this runs from disposal, where an exception would replace
    /// whatever was already being thrown with one about tidying up, and a directory that has
    /// been removed from underneath us — by the very thing we are cleaning up after, or by
    /// somebody else — is an ordinary outcome rather than a fault.
    /// </remarks>
    private static CapError EmptyOpen(Dir directory, int remainingDepth)
    {
        if (remainingDepth == 0)
        {
            return CapError.FromCategory(CapErrorCategory.PathTooDeep);
        }

        CapError first = CapError.Success;

        try
        {
            foreach (DirEntry entry in directory.EnumerateEntries())
            {
                CapError removed = RemoveEntry(directory, entry, remainingDepth);
                if (removed.IsFailure && first.IsSuccess)
                {
                    // The first failure is the one reported, and the walk carries on past it.
                    // Stopping would leave behind entries that had nothing to do with the
                    // failure, and a caller clearing out a directory of untrusted content
                    // wants as much of it gone as can be.
                    first = removed;
                }
            }
        }
        catch (IOException)
        {
            return first.IsFailure ? first : CapError.FromCategory(CapErrorCategory.Unknown);
        }
        catch (UnauthorizedAccessException)
        {
            return first.IsFailure ? first : CapError.FromCategory(CapErrorCategory.PermissionDenied);
        }

        return first;
    }

    /// <summary>Removes one entry, emptying it first when it is something that holds entries.</summary>
    /// <remarks>
    /// <para>
    /// A link is never descended into, whatever it leads to, and neither is a reparse point
    /// of a kind this library does not recognise. Both are removed as the names they are,
    /// which is what makes it safe to clear out a tree somebody else filled: what the name
    /// refers to is not reached, not read, and not removed.
    /// </para>
    /// <para>
    /// An entry the filesystem declined to classify is treated as though it might hold
    /// entries, because the cost of being wrong that way is one refused open and the cost of
    /// being wrong the other way is a directory left behind.
    /// </para>
    /// </remarks>
    private static CapError RemoveEntry(Dir directory, DirEntry entry, int remainingDepth)
    {
        if (entry.Type is CapFileType.Directory or CapFileType.Unknown)
        {
            CapError emptied = Empty(directory, entry.Name, remainingDepth - 1);
            if (emptied.IsFailure)
            {
                return emptied;
            }
        }

        return Unlink(directory, entry.Name, directory: entry.Type == CapFileType.Directory);
    }

    /// <summary>
    /// Removes a single name, as the kind of thing it is expected to be and then as the other
    /// kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two attempts because a directory read is not a snapshot and, on several filesystems,
    /// does not say what its entries are in the first place. The kind is therefore a guess
    /// that puts the likely call first, and an answer of "that is the other kind" is followed
    /// rather than reported — the two calls remove a name by that name in the same directory
    /// handle, so nothing about containment turns on which of them does it.
    /// </para>
    /// <para>
    /// A name that will not be removed because the object itself refuses it — the read-only
    /// flag on Windows, which is a property of the file rather than a permission — is cleared
    /// and tried once more. Only after the removal has already failed, so the ordinary case
    /// pays nothing for it, and only once, so a name something is actively re-marking ends as
    /// a failure rather than as a loop.
    /// </para>
    /// </remarks>
    private static CapError Unlink(Dir parent, string name, bool directory)
    {
        CapError first = UnlinkAs(parent, name, directory);
        if (first.IsSuccess)
        {
            return first;
        }

        if (first.Category is CapErrorCategory.NotADirectory or CapErrorCategory.IsADirectory)
        {
            CapError other = UnlinkAs(parent, name, !directory);
            if (other.IsSuccess)
            {
                return other;
            }
        }

        if (first.Category is not (CapErrorCategory.PermissionDenied or CapErrorCategory.ReadOnlyFilesystem))
        {
            return first;
        }

        CapError cleared = parent.ClearRemovalBlock(name);
        return cleared.IsFailure ? first : UnlinkAs(parent, name, directory);
    }

    /// <summary>Removes a name as one kind of object, reporting the platform's answer.</summary>
    private static CapError UnlinkAs(Dir parent, string name, bool directory) =>
        directory ? parent.DeleteDirCore(name) : parent.DeleteFileCore(name);
}
