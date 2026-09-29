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
/// <strong>Both entry points do their work through a handle that refuses symbolic
/// links,</strong> whatever policy the caller's own handle carries. Removing a tree is the one
/// operation where following a link is never what was meant: a name at the top that is a link
/// would redirect the whole removal somewhere else, and a directory further down that turns
/// into a link between being listed and being entered would do the same to a subtree — even
/// when the link leads somewhere inside the handle's own subtree, what it leads to is not what
/// was listed and not what the caller asked to be removed. Narrowing the policy for the
/// duration makes those refusals come from resolution rather than from a check that something
/// could be arranged to pass.
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
    /// <para>
    /// Takes the directory as a handle rather than as a name beneath one, because the caller
    /// that empties a directory it created has been holding that handle since it created it.
    /// Going back to the name to start the work would introduce a lookup where there was
    /// none, and a lookup is a thing that can be answered differently the second time.
    /// </para>
    /// <para>
    /// <strong>The work is done through a handle that refuses symbolic links,</strong> a
    /// duplicate of <paramref name="directory"/> narrowed for the duration, for the reason
    /// the class gives. The handle passed in is the directory emptied however it was reached,
    /// since there is no name to resolve on the way in; the narrowing governs every directory
    /// entered beneath it, so one swapped for a link after being listed is not entered and its
    /// name is unlinked as the link.
    /// </para>
    /// <para>
    /// <paramref name="cancellationToken"/> is looked at before each entry is removed, and a
    /// signal stops the work there by throwing rather than by being reported as a failure:
    /// what had been removed is gone, and what had not is left as it was.
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    public static CapError Empty(Dir directory, CancellationToken cancellationToken = default) =>
        Empty(directory, out _, cancellationToken);

    /// <summary>
    /// Removes everything inside an open directory, leaving the directory itself, and says
    /// where the failure it reports was met.
    /// </summary>
    /// <param name="directory">The directory to empty.</param>
    /// <param name="failedAt">
    /// The names leading from <paramref name="directory"/> to the entry the reported failure
    /// concerns, outermost first; null when it concerns <paramref name="directory"/> itself or
    /// there was no failure.
    /// </param>
    /// <param name="cancellationToken">Stops the removal before the next entry.</param>
    /// <returns>As <see cref="Empty(Dir, CancellationToken)"/> returns.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    public static CapError Empty(Dir directory, out IReadOnlyList<string>? failedAt, CancellationToken cancellationToken = default)
    {
        failedAt = null;
        cancellationToken.ThrowIfCancellationRequested();

        if (!directory.TryRestrict(SymlinkPolicy.Deny, out Dir? strict))
        {
            return CapError.FromCategory(CapErrorCategory.OutOfHandles);
        }

        using (strict)
        {
            Outcome emptied = EmptyOpen(strict, MaximumDepth, cancellationToken);
            failedAt = emptied.Where();
            return emptied.Error;
        }
    }

    /// <summary>
    /// Removes the directory named <paramref name="name"/> beneath <paramref name="parent"/>,
    /// and everything inside it.
    /// </summary>
    /// <param name="parent">The directory holding the name.</param>
    /// <param name="name">A single component naming the directory to remove.</param>
    /// <param name="cancellationToken">Stops the removal before the next entry.</param>
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
    /// <strong>The work is done through a handle that refuses symbolic links,</strong> a
    /// duplicate of <paramref name="parent"/> narrowed for the duration, for the reason the
    /// class gives. Here it covers the name at the top as well as every directory beneath it,
    /// so a link at <paramref name="name"/> is refused rather than followed.
    /// </para>
    /// <para>
    /// A name that is not a directory — a file, a link, or nothing at all — is reported rather
    /// than unlinked. Removing a tree is a request about a directory, and quietly deleting
    /// whatever else was found under the name would make it a request about a name.
    /// </para>
    /// <para>
    /// <paramref name="cancellationToken"/> is honoured as <see cref="Empty(Dir, CancellationToken)"/>
    /// honours it.
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    public static CapError Remove(Dir parent, string name, CancellationToken cancellationToken = default) =>
        Remove(parent, name, out _, cancellationToken);

    /// <summary>
    /// Removes the directory named <paramref name="name"/> beneath <paramref name="parent"/>,
    /// and everything inside it, and says where the failure it reports was met.
    /// </summary>
    /// <param name="parent">The directory holding the name.</param>
    /// <param name="name">A single component naming the directory to remove.</param>
    /// <param name="failedAt">
    /// The names leading from <paramref name="name"/> to the entry the reported failure
    /// concerns, outermost first; null when it concerns <paramref name="name"/> itself or
    /// there was no failure.
    /// </param>
    /// <param name="cancellationToken">Stops the removal before the next entry.</param>
    /// <returns>As <see cref="Remove(Dir, string, CancellationToken)"/> returns.</returns>
    /// <remarks>
    /// <para>
    /// A directory that stats as one but cannot be opened — one whose permissions refuse this
    /// process a read — is not a reason to give up on the name. Removing an empty directory
    /// asks nothing of the directory itself, only of the one holding it, so the removal is
    /// attempted anyway: an empty one goes, as it would for <c>rm -rf</c>, and a full one
    /// stays, reported with the failure that stopped it being opened rather than with the
    /// "not empty" that followed from it.
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    public static CapError Remove(Dir parent, string name, out IReadOnlyList<string>? failedAt, CancellationToken cancellationToken = default)
    {
        failedAt = null;
        cancellationToken.ThrowIfCancellationRequested();

        if (!parent.TryRestrict(SymlinkPolicy.Deny, out Dir? strict))
        {
            return CapError.FromCategory(CapErrorCategory.OutOfHandles);
        }

        using (strict)
        {
            CapError opened = strict.OpenDirForWalk(name, refuseLinks: true, out Dir? directory);
            if (opened.IsFailure)
            {
                CapError diagnosed = Diagnose(parent, name);
                if (diagnosed.Category != CapErrorCategory.Unknown)
                {
                    return diagnosed;
                }

                return RemoveEmpty(strict, name).IsSuccess ? CapError.Success : opened;
            }

            Outcome emptied;
            using (directory)
            {
                emptied = EmptyOpen(directory!, MaximumDepth, cancellationToken);
            }

            if (emptied.Error.IsFailure)
            {
                failedAt = emptied.Where();
                return emptied.Error;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return RemoveEmpty(strict, name);
        }
    }

    /// <summary>Says why a name could not be opened as the directory it was meant to be.</summary>
    /// <remarks>
    /// Asked as a description of the name rather than as another attempt to open it, so that
    /// the answer says what is actually there. A link and a file are worth telling apart from
    /// each other and from a name holding nothing, because each means a different mistake in
    /// the calling code. A directory that would not open answers <see cref="CapErrorCategory.Unknown"/>
    /// here, which the caller takes as the cue to report the open's own failure instead.
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
    /// A failure met while emptying a directory, and where beneath that directory it was met.
    /// </summary>
    /// <param name="Error">The failure, or success.</param>
    /// <param name="Trail">
    /// The names from the directory being emptied down to the entry the failure concerns,
    /// innermost first — the order a failure climbing back out of the recursion adds them in —
    /// or null when it concerns the directory itself.
    /// </param>
    /// <remarks>
    /// The names are gathered only on the way back out from a failure, so a removal that
    /// succeeds builds nothing.
    /// </remarks>
    private readonly record struct Outcome(CapError Error, List<string>? Trail)
    {
        public static Outcome Success => new(CapError.Success, null);

        /// <summary>This outcome as met one level further out, inside the entry <paramref name="name"/>.</summary>
        public Outcome Inside(string name)
        {
            List<string> trail = Trail ?? [];
            trail.Add(name);
            return this with { Trail = trail };
        }

        /// <summary>The trail outermost first, as the caller reads it.</summary>
        public string[]? Where()
        {
            if (Trail is null)
            {
                return null;
            }

            string[] names = [.. Trail];
            Array.Reverse(names);
            return names;
        }
    }

    /// <summary>
    /// Removes everything inside the directory named <paramref name="name"/>, leaving the
    /// directory itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A name that is not a directory that can be entered — gone already, replaced with a
    /// link, never one, or changed while being opened — is not descended into, and reported as
    /// nothing to do: the caller unlinks the name instead, which acts on the name and not on
    /// whatever it now refers to.
    /// </para>
    /// <para>
    /// A directory that could not be opened for any other reason — permission, above all — is
    /// reported as the failure it was, in <paramref name="unopenable"/>, and not as a failure
    /// of this step. The caller still tries to unlink the name, since an empty directory can be
    /// removed without being opened, and reports the open's failure only when that does not
    /// work: "not empty" is what the unlink says, but the reason is that it could not be looked
    /// inside.
    /// </para>
    /// </remarks>
    private static Outcome Empty(
        Dir parent, string name, int remainingDepth, out CapError unopenable, CancellationToken cancellationToken)
    {
        unopenable = CapError.Success;

        CapError opened = parent.OpenDirForWalk(name, refuseLinks: true, out Dir? directory);
        if (opened.IsFailure)
        {
            if (!LeavesNothingToEnter(opened.Category))
            {
                unopenable = opened;
            }

            return Outcome.Success;
        }

        using (directory)
        {
            return EmptyOpen(directory!, remainingDepth, cancellationToken);
        }
    }

    /// <summary>
    /// Whether a failed open means the name is not a directory to descend into, rather than
    /// one that could not be.
    /// </summary>
    /// <remarks>
    /// The same division a walk makes: gone, not a directory, a link, leading out of the
    /// subtree, or changed during the open are all names with nothing beneath them to remove.
    /// </remarks>
    private static bool LeavesNothingToEnter(CapErrorCategory category) => FailureTranslation.KindOf(category) is
        CapErrorKind.NotFound or
        CapErrorKind.NotADirectory or
        CapErrorKind.SymbolicLink or
        CapErrorKind.LinkNotFollowed or
        CapErrorKind.Escaped or
        CapErrorKind.ConcurrentChange;

    /// <summary>Removes everything inside a directory that is already open.</summary>
    /// <remarks>
    /// The reading of the directory is the one step here that reports by throwing, because it
    /// is the public enumeration and that is how the public surface answers. It is caught
    /// rather than allowed out: this runs from disposal, where an exception would replace
    /// whatever was already being thrown with one about tidying up, and a directory that has
    /// been removed from underneath us — by the very thing we are cleaning up after, or by
    /// somebody else — is an ordinary outcome rather than a fault.
    /// </remarks>
    private static Outcome EmptyOpen(Dir directory, int remainingDepth, CancellationToken cancellationToken)
    {
        if (remainingDepth == 0)
        {
            return new Outcome(CapError.FromCategory(CapErrorCategory.PathTooDeep), null);
        }

        Outcome first = Outcome.Success;

        try
        {
            foreach (DirEntry entry in directory.EnumerateEntries())
            {
                cancellationToken.ThrowIfCancellationRequested();

                Outcome removed = RemoveEntry(directory, entry, remainingDepth, cancellationToken);
                if (removed.Error.IsFailure && first.Error.IsSuccess)
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
            return first.Error.IsFailure ? first : new Outcome(CapError.FromCategory(CapErrorCategory.Unknown), null);
        }
        catch (UnauthorizedAccessException)
        {
            return first.Error.IsFailure ? first : new Outcome(CapError.FromCategory(CapErrorCategory.PermissionDenied), null);
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
    private static Outcome RemoveEntry(
        Dir directory, DirEntry entry, int remainingDepth, CancellationToken cancellationToken)
    {
        CapError unopenable = CapError.Success;
        if (entry.Type is CapFileType.Directory or CapFileType.Unknown)
        {
            Outcome emptied = Empty(directory, entry.Name, remainingDepth - 1, out unopenable, cancellationToken);
            if (emptied.Error.IsFailure)
            {
                return emptied.Inside(entry.Name);
            }
        }

        CapError unlinked = Unlink(directory, entry.Name, directory: entry.Type == CapFileType.Directory);
        if (unlinked.IsSuccess)
        {
            return Outcome.Success;
        }

        return new Outcome(unopenable.IsFailure ? unopenable : unlinked, null).Inside(entry.Name);
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
