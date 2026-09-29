using System.Runtime.ExceptionServices;
using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Std;

namespace Cap.Fs.Ext;

/// <summary>
/// Removing a tree through a handle that is not a <see cref="Dir"/>, using only what
/// <see cref="IDir"/> offers.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Dir"/> is removed by the core layer's own tree removal, which works on the raw
/// handle. This is the same walk written against the interface, for a stub or a wrapper: each
/// directory is descended by opening its entry through the handle that listed it, refusing a
/// link, and each entry is removed by its single name against that handle. Descent is by
/// handle and never by a rebuilt path, as it is for a <see cref="Dir"/>, so a directory
/// swapped for a link between being listed and being entered is still not entered.
/// </para>
/// <para>
/// <strong>One thing the interface cannot do.</strong> On Windows a file or directory marked
/// read-only refuses to be removed, and the core layer clears the mark and tries again. No
/// member of <see cref="IDir"/> clears it, so here such an entry is reported as the removal
/// failure the implementation gave, and left in place.
/// </para>
/// <para>
/// Failures arrive as exceptions from the implementation rather than as values, so the first
/// one is kept and carried to the caller as it was thrown, and the walk goes on past it as the
/// core layer's does. Cancellation is the exception: it is looked at before each entry, as the
/// core layer looks at it, and a signal is thrown at once rather than kept.
/// </para>
/// </remarks>
internal static class InterfaceTreeRemoval
{
    /// <summary>
    /// How deep the descent may go before it refuses to go further.
    /// </summary>
    /// <remarks>
    /// The same limit the core layer applies to its own tree removal. The descent holds one
    /// open directory and one stack frame per level, and the depth of a tree is decided by
    /// whoever built it.
    /// </remarks>
    private const int MaximumDepth = 256;

    /// <summary>
    /// Removes the directory named <paramref name="name"/> beneath <paramref name="parent"/>,
    /// and everything inside it.
    /// </summary>
    /// <param name="parent">The directory holding the name.</param>
    /// <param name="name">A single component naming the directory to remove.</param>
    /// <param name="path">The caller's path, quoted back in a failure this builds itself.</param>
    /// <param name="cancellationToken">Stops the removal before the next entry.</param>
    /// <returns>Null when the name is gone, or the first failure that stopped it going.</returns>
    /// <remarks>
    /// The work is done through a copy of <paramref name="parent"/> restricted to refuse
    /// symbolic links, and every directory is opened asking that a link not be followed, so a
    /// link at <paramref name="name"/> or anywhere beneath it is removed as the link and what
    /// it points at is not reached.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    public static Exception? Remove(IDir parent, string name, string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            IDir strict = parent.Restrict(SymlinkPolicy.Deny);
            try
            {
                if (!strict.TryOpenDir(name, noFollow: true, out IDir? directory))
                {
                    CapError diagnosed = Diagnose(parent, name);
                    if (diagnosed.Category != CapErrorCategory.Unknown)
                    {
                        return FailureTranslation.ToException(diagnosed, path, ExpectedTarget.Directory);
                    }

                    // A directory that would not open. An empty one can still be removed, which
                    // asks nothing of the directory itself; a full one is reported with the
                    // reason it could not be opened, rather than with the "not empty" that
                    // follows from that.
                    return strict.TryDeleteDir(name) ? null : WhyNotOpened(strict, name, path);
                }

                Exception? emptied;
                using (directory)
                {
                    emptied = EmptyOpen(directory, MaximumDepth, new Trail(path, null), cancellationToken);
                }

                if (emptied is not null)
                {
                    return emptied;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return Unlink(strict, name, directory: true);
            }
            finally
            {
                Release(strict, parent);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception;
        }
    }

    /// <summary>Removes everything inside a directory, leaving the directory itself.</summary>
    /// <param name="directory">The directory to empty.</param>
    /// <param name="cancellationToken">Stops the emptying before the next entry.</param>
    /// <returns>Null when it is empty, or the first failure that stopped it becoming empty.</returns>
    /// <remarks>
    /// Done through a copy of <paramref name="directory"/> restricted to refuse symbolic
    /// links, for the reason <see cref="Remove"/> gives.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    public static Exception? Empty(IDir directory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            IDir strict = directory.Restrict(SymlinkPolicy.Deny);
            try
            {
                return EmptyOpen(strict, MaximumDepth, trail: null, cancellationToken);
            }
            finally
            {
                Release(strict, directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception;
        }
    }

    /// <summary>Throws a failure this walk kept, as it was first thrown.</summary>
    public static void Rethrow(Exception failure) => ExceptionDispatchInfo.Throw(failure);

    /// <summary>
    /// Disposes the handle a restriction produced, unless it is the handle it was asked of.
    /// </summary>
    /// <remarks>
    /// <see cref="Dir.Restrict"/> always produces a new handle, and says so, but
    /// <see cref="IDir"/> is implemented by stubs and wrappers too, and one that answers a
    /// request for the policy it already has by returning itself is doing nothing
    /// unreasonable. Disposing that answer would close the caller's own handle behind its
    /// back, so it is left alone.
    /// </remarks>
    private static void Release(IDir narrowed, IDir original)
    {
        if (!ReferenceEquals(narrowed, original))
        {
            narrowed.Dispose();
        }
    }

    /// <summary>Says why a name could not be opened as the directory it was meant to be.</summary>
    /// <remarks>
    /// Asked as a description of the name, so that a link, a file and a name holding nothing
    /// are told apart, as the core layer tells them apart. A directory that would not open
    /// answers <see cref="CapErrorCategory.Unknown"/>, which the caller takes as the cue to
    /// ask the open itself why.
    /// </remarks>
    private static CapError Diagnose(IDir parent, string name)
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
    /// Opens a name that would not open, in the form that throws, for the implementation's
    /// account of why, and reports it against <paramref name="where"/>.
    /// </summary>
    /// <returns>
    /// The failure, or null when the open now succeeds or fails only because the name is not
    /// a directory to enter — something changed it in between, and there is nothing to add.
    /// </returns>
    /// <remarks>
    /// Asked only once the name has also refused to be removed, so a removal that goes well
    /// never pays for it.
    /// </remarks>
    private static Exception? WhyNotOpened(IDir directory, string name, string? where)
    {
        try
        {
            directory.OpenDir(name, noFollow: true).Dispose();
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CapErrorKind kind = CapIOException.KindOf(exception);
            if (LeavesNothingToEnter(kind))
            {
                return null;
            }

            string quoted = where ?? name;
            return exception switch
            {
                UnauthorizedAccessException => new UnauthorizedAccessException(
                    $"'{quoted}' could not be opened to remove what is inside it: permission was refused.",
                    exception),
                _ => new CapIOException(
                    kind,
                    $"'{quoted}' could not be opened to remove what is inside it. ({kind})",
                    exception),
            };
        }
    }

    /// <summary>
    /// Whether a failed open means the name is not a directory to descend into, rather than
    /// one that could not be.
    /// </summary>
    private static bool LeavesNothingToEnter(CapErrorKind kind) => kind is
        CapErrorKind.NotFound or
        CapErrorKind.NotADirectory or
        CapErrorKind.SymbolicLink or
        CapErrorKind.LinkNotFollowed or
        CapErrorKind.Escaped or
        CapErrorKind.ConcurrentChange;

    /// <summary>
    /// The names descended through to reach a directory, kept only so that a failure inside it
    /// can say where it was.
    /// </summary>
    /// <param name="Start">The caller's path, when the removal began from one.</param>
    /// <param name="Up">The directory this one was found in, or null at the top.</param>
    /// <param name="Name">This directory's name in <paramref name="Up"/>, or null at the top.</param>
    /// <remarks>
    /// Never resolved and never handed to anything that resolves: it is spelled out into a
    /// message by <see cref="CapPath.DescribeBeneath"/> and nowhere else.
    /// </remarks>
    private sealed record Trail(string? Start, Trail? Up, string? Name = null)
    {
        public Trail Into(string name) => new(Start, this, name);

        /// <summary>Spells the entry <paramref name="name"/> in this directory for a message.</summary>
        public string Describe(string name)
        {
            List<string> names = [name];
            for (Trail? level = this; level?.Name is not null; level = level.Up)
            {
                names.Add(level.Name);
            }

            names.Reverse();
            return CapPath.DescribeBeneath(Start, names, CapPath.HostSyntax);
        }
    }

    /// <summary>Removes everything inside a directory that is already open.</summary>
    /// <param name="directory">The directory to empty.</param>
    /// <param name="remainingDepth">How many more levels the descent may go down.</param>
    /// <param name="trail">
    /// How the directory was reached, from the caller's path when the removal began from one;
    /// null when it began from a handle and this is that handle.
    /// </param>
    /// <param name="cancellationToken">Stops the removal before the next entry.</param>
    private static Exception? EmptyOpen(
        IDir directory, int remainingDepth, Trail? trail, CancellationToken cancellationToken)
    {
        if (remainingDepth == 0)
        {
            CapError tooDeep = CapError.FromCategory(CapErrorCategory.PathTooDeep);
            return trail?.Start is null
                ? FailureTranslation.ToEnumerationException(tooDeep)
                : FailureTranslation.ToException(tooDeep, trail.Start, ExpectedTarget.Directory);
        }

        Exception? first = null;

        try
        {
            foreach (IDirEntry entry in directory.EnumerateEntries())
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The first failure is the one reported, and the walk carries on past it, so
                // one entry that cannot be removed does not leave the rest of the tree behind.
                Exception? removed = RemoveEntry(directory, entry, remainingDepth, trail, cancellationToken);
                first ??= removed;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return first ?? exception;
        }

        return first;
    }

    /// <summary>Removes one entry, emptying it first when it may hold entries.</summary>
    /// <remarks>
    /// <para>
    /// A link is never descended into, and an entry the implementation declined to classify is
    /// treated as though it might be a directory: the open that follows refuses it if it is
    /// not, at the cost of one call.
    /// </para>
    /// <para>
    /// A directory that will not open is still removed when it is empty. When it will not go
    /// either, the failure reported is the reason it would not open, named by its place
    /// beneath the removal's start, rather than the "not empty" the removal was bound to meet.
    /// </para>
    /// </remarks>
    private static Exception? RemoveEntry(
        IDir directory, IDirEntry entry, int remainingDepth, Trail? trail, CancellationToken cancellationToken)
    {
        bool isDirectory = entry.Type == CapFileType.Directory;
        if (entry.Type is CapFileType.Directory or CapFileType.Unknown)
        {
            Trail here = trail ?? new Trail(null, null);
            if (directory.TryOpenDir(entry.Name, noFollow: true, out IDir? child))
            {
                Exception? emptied;
                using (child)
                {
                    emptied = EmptyOpen(child, remainingDepth - 1, here.Into(entry.Name), cancellationToken);
                }

                if (emptied is not null)
                {
                    return emptied;
                }
            }
            else
            {
                if (TryUnlinkAs(directory, entry.Name, isDirectory) || TryUnlinkAs(directory, entry.Name, !isDirectory))
                {
                    return null;
                }

                if (WhyNotOpened(directory, entry.Name, here.Describe(entry.Name)) is { } unopenable)
                {
                    return unopenable;
                }
            }
        }

        return Unlink(directory, entry.Name, directory: isDirectory);
    }
    /// <summary>
    /// Removes a single name, as the kind of thing it is expected to be and then as the other
    /// kind.
    /// </summary>
    /// <remarks>
    /// Both kinds are tried because a directory read is not a snapshot and does not always say
    /// what its entries are. When neither removal succeeds, the expected kind is attempted once
    /// more in the form that throws, so the failure reported is the implementation's own
    /// account of why.
    /// </remarks>
    private static Exception? Unlink(IDir parent, string name, bool directory)
    {
        if (TryUnlinkAs(parent, name, directory) || TryUnlinkAs(parent, name, !directory))
        {
            return null;
        }

        try
        {
            if (directory)
            {
                parent.DeleteDir(name);
            }
            else
            {
                parent.DeleteFile(name);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception;
        }
    }

    private static bool TryUnlinkAs(IDir parent, string name, bool directory) =>
        directory ? parent.TryDeleteDir(name) : parent.TryDeleteFile(name);
}
