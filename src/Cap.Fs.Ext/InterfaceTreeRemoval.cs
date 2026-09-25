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
/// core layer's does.
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
    /// <returns>Null when the name is gone, or the first failure that stopped it going.</returns>
    /// <remarks>
    /// The work is done through a copy of <paramref name="parent"/> restricted to refuse
    /// symbolic links, and every directory is opened asking that a link not be followed, so a
    /// link at <paramref name="name"/> or anywhere beneath it is removed as the link and what
    /// it points at is not reached.
    /// </remarks>
    public static Exception? Remove(IDir parent, string name, string path)
    {
        try
        {
            using IDir strict = parent.Restrict(SymlinkPolicy.Deny);

            if (!strict.TryOpenDir(name, noFollow: true, out IDir? directory))
            {
                return FailureTranslation.ToException(Diagnose(parent, name), path, ExpectedTarget.Directory);
            }

            Exception? emptied;
            using (directory)
            {
                emptied = EmptyOpen(directory, MaximumDepth, path);
            }

            return emptied ?? Unlink(strict, name, directory: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception;
        }
    }

    /// <summary>Removes everything inside a directory, leaving the directory itself.</summary>
    /// <param name="directory">The directory to empty.</param>
    /// <returns>Null when it is empty, or the first failure that stopped it becoming empty.</returns>
    /// <remarks>
    /// Done through a copy of <paramref name="directory"/> restricted to refuse symbolic
    /// links, for the reason <see cref="Remove"/> gives.
    /// </remarks>
    public static Exception? Empty(IDir directory)
    {
        try
        {
            using IDir strict = directory.Restrict(SymlinkPolicy.Deny);
            return EmptyOpen(strict, MaximumDepth, path: null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception;
        }
    }

    /// <summary>Throws a failure this walk kept, as it was first thrown.</summary>
    public static void Rethrow(Exception failure) => ExceptionDispatchInfo.Throw(failure);

    /// <summary>Says why a name could not be opened as the directory it was meant to be.</summary>
    /// <remarks>
    /// Asked as a description of the name, so that a link, a file and a name holding nothing
    /// are told apart, as the core layer tells them apart.
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

    /// <summary>Removes everything inside a directory that is already open.</summary>
    /// <param name="directory">The directory to empty.</param>
    /// <param name="remainingDepth">How many more levels the descent may go down.</param>
    /// <param name="path">
    /// The caller's path, when the removal began from one; null when it began from a handle.
    /// </param>
    private static Exception? EmptyOpen(IDir directory, int remainingDepth, string? path)
    {
        if (remainingDepth == 0)
        {
            CapError tooDeep = CapError.FromCategory(CapErrorCategory.PathTooDeep);
            return path is null
                ? FailureTranslation.ToEnumerationException(tooDeep)
                : FailureTranslation.ToException(tooDeep, path, ExpectedTarget.Directory);
        }

        Exception? first = null;

        try
        {
            foreach (IDirEntry entry in directory.EnumerateEntries())
            {
                // The first failure is the one reported, and the walk carries on past it, so
                // one entry that cannot be removed does not leave the rest of the tree behind.
                Exception? removed = RemoveEntry(directory, entry, remainingDepth, path);
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
    /// A link is never descended into, and an entry the implementation declined to classify is
    /// treated as though it might be a directory: the open that follows refuses it if it is
    /// not, at the cost of one call.
    /// </remarks>
    private static Exception? RemoveEntry(IDir directory, IDirEntry entry, int remainingDepth, string? path)
    {
        if (entry.Type is CapFileType.Directory or CapFileType.Unknown &&
            directory.TryOpenDir(entry.Name, noFollow: true, out IDir? child))
        {
            Exception? emptied;
            using (child)
            {
                emptied = EmptyOpen(child, remainingDepth - 1, path);
            }

            if (emptied is not null)
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
