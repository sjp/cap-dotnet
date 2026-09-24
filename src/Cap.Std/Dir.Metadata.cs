using Cap.Primitives;
using Cap.Primitives.Interop;

namespace Cap.Std;

public sealed partial class Dir
{
    /// <summary>
    /// Describes the directory this handle refers to.
    /// </summary>
    /// <returns>A snapshot of the directory, taken at the moment of the call.</returns>
    /// <remarks>
    /// <para>
    /// Asked of the handle and not of a name, so nothing is resolved and there is nothing
    /// for a concurrent rename to interfere with: the answer describes the object this
    /// handle was opened on, whatever that object is currently called and whether it is
    /// called anything at all. A directory that has been removed while this handle was held
    /// still answers, which is the honest result — the object exists as long as something
    /// holds it open, even once no directory names it.
    /// </para>
    /// <para>
    /// No symbolic link is involved: the handle already refers to a directory, never to a
    /// link, whatever route opened it. Safe to call concurrently with any other member of
    /// this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the question.</exception>
    /// <exception cref="CapIOException">The question could not be answered.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public CapMetadata GetMetadata()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        CapError error = PlatformOps.Current.DescribeHandle(_handle, out CapNodeStat stat);
        return error.IsSuccess ? new CapMetadata(stat) : throw FailureTranslation.ToHandleException(error);
    }

    /// <summary>
    /// Asks the filesystem to commit this directory's own record of what it holds.
    /// </summary>
    /// <returns>
    /// The platform's answer, so that a caller can tell a refusal from a system that has no
    /// such request at all.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The step that makes a name durable. Committing a file's contents says nothing about
    /// the entry that reaches them, so an operation promising that a file will be there after
    /// the power fails has to commit the directory as well — and only the directory's own
    /// handle can be asked.
    /// </para>
    /// <para>
    /// Internal because it is a step of the operations that publish a file rather than
    /// something a caller composes for themselves. Reported rather than thrown for the same
    /// reason: the one platform that cannot do it at all is a case the caller above decides
    /// about, not a failure.
    /// </para>
    /// </remarks>
    internal CapError SyncContents()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        return PlatformOps.Current.SyncDirectory(_handle);
    }

    /// <summary>
    /// Writes permissions onto the directory this handle refers to.
    /// </summary>
    /// <param name="permissions">A value read from some other object's snapshot.</param>
    /// <remarks>
    /// Applied to the object rather than to a name, so nothing is looked up a second time.
    /// Internal, and reached only by copying: choosing permissions is a decision about a file
    /// that a caller makes when they create it, while carrying an existing object's across is
    /// a mechanical step of reproducing that object.
    /// </remarks>
    internal CapError SetPermissions(in CapPermissions permissions)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        return PlatformOps.Current.SetHandlePermissions(
            _handle, permissions.UnixMode, permissions.WindowsAttributes);
    }

    /// <summary>
    /// Describes what a name beneath this handle holds.
    /// </summary>
    /// <param name="path">A relative path to the name to describe.</param>
    /// <param name="followLink">
    /// Whether a symbolic link at the last component is followed, so that what it leads to is
    /// described instead of the link, as <c>stat</c> does where the default is
    /// <c>lstat</c>.
    /// </param>
    /// <returns>A snapshot of the entry, taken at the moment of the call.</returns>
    /// <remarks>
    /// <para>
    /// <strong>By default it describes the name, and does not follow a link that holds
    /// it.</strong> A symbolic link is reported as a symbolic link, with its own length and
    /// its own times, whether its target exists, does not exist, or lies outside the subtree
    /// entirely. That is the same rule every other member taking a name follows, and it is
    /// what keeps the answer from depending on the handle's symbolic-link policy — the same
    /// name would otherwise describe one thing through a permissive handle and something else
    /// through a strict one.
    /// </para>
    /// <para>
    /// <strong>With <paramref name="followLink"/> set, a final link is followed</strong> as
    /// a link on the way would be: while its target stays beneath this handle, through any
    /// further links the target reaches, and refused with
    /// <see cref="SandboxEscapeException"/> once it leaves. Under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> the link is refused with
    /// <see cref="CapIOException"/> instead, since this handle follows no links at all. A link
    /// that dangles is reported as missing. Nothing is opened to answer: the entry the chain
    /// ends at is described by name, so the answer needs no permission to open it.
    /// </para>
    /// <para>
    /// Path resolution up to the last component is confined exactly as it is for an open,
    /// and a link met on the way there is followed or refused by the handle's policy in the
    /// usual way.
    /// </para>
    /// <para>
    /// A path spelled so that it must name a directory — one ending in a separator — is
    /// described only if a directory is what holds the name.
    /// </para>
    /// <para>
    /// <strong>Symbolic links, in short.</strong> The last component is followed only when
    /// <paramref name="followLink"/> asks for it, and then only as far as a link on the way
    /// would be. A link before it is followed while its target stays beneath this handle and
    /// refused with <see cref="SandboxEscapeException"/> when it leaves, and under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> is refused with
    /// <see cref="CapIOException"/> wherever it points.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority, or a final
    /// link being followed led outside it.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no such name.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above it is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the question.</exception>
    /// <exception cref="CapIOException">
    /// A symbolic link the policy will not follow is in the way, or the question could not be
    /// answered.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public CapMetadata GetMetadata(string path, bool followLink = false)
    {
        CapPathError pathError = MetadataCore(path, followLink, out CapMetadata metadata, out CapError error);
        if (pathError != CapPathError.None)
        {
            throw FailureTranslation.ToException(pathError, path, nameof(path));
        }

        return error.IsSuccess
            ? metadata
            : throw FailureTranslation.ToException(error, path, ExpectedTarget.Name);
    }

    /// <summary>
    /// Describes what a name beneath this handle holds, reporting failure rather than
    /// throwing.
    /// </summary>
    /// <param name="path">A relative path to the name. See <see cref="GetMetadata(string, bool)"/>.</param>
    /// <param name="metadata">The snapshot, when this returns true.</param>
    /// <returns>True when the name was described.</returns>
    /// <remarks>
    /// <para>
    /// A name that is not there is the expected answer for this question rather than an
    /// exceptional one — describing an entry read a moment ago is the ordinary case, and the
    /// entry being gone by now is the ordinary way that fails. Arguments that are wrong
    /// rather than unlucky still throw.
    /// </para>
    /// <para>
    /// Symbolic links are treated exactly as <see cref="GetMetadata(string, bool)"/> describes:
    /// a link at the last component is described as itself under either policy, one on the
    /// way is followed or refused by this handle's policy, and a refusal — containment
    /// included — is reported as false. Safe to call concurrently with any other member of
    /// this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryGetMetadata(string path, out CapMetadata metadata) =>
        TryGetMetadata(path, followLink: false, out metadata);

    /// <summary>
    /// Describes what a name beneath this handle holds, or what a symbolic link holding it
    /// leads to, reporting failure rather than throwing.
    /// </summary>
    /// <param name="path">A relative path to the name. See <see cref="GetMetadata(string, bool)"/>.</param>
    /// <param name="followLink">
    /// Whether a symbolic link at the last component is followed. See
    /// <see cref="GetMetadata(string, bool)"/>.
    /// </param>
    /// <param name="metadata">The snapshot, when this returns true.</param>
    /// <returns>True when the name, or what it leads to, was described.</returns>
    /// <remarks>
    /// Symbolic links are followed or refused exactly as
    /// <see cref="GetMetadata(string, bool)"/> describes for the same
    /// <paramref name="followLink"/>, and every refusal, containment included, is reported as
    /// false. Safe to call concurrently with any other member of this handle, from any thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryGetMetadata(string path, bool followLink, out CapMetadata metadata)
    {
        CapPathError pathError = MetadataCore(path, followLink, out metadata, out CapError error);
        return pathError == CapPathError.None && error.IsSuccess;
    }

    /// <summary>
    /// Sets when the directory this handle refers to was last read and last written.
    /// </summary>
    /// <param name="lastAccess">
    /// What to do with the last-access time. Left as it is unless given.
    /// </param>
    /// <param name="lastWrite">
    /// What to do with the last-write time. Left as it is unless given.
    /// </param>
    /// <remarks>
    /// <para>
    /// Applied to the object rather than to a name, so it changes the directory this handle
    /// was opened on, whatever it is called now. Adding, removing or renaming an entry
    /// afterwards changes the last-write time again, as it always does.
    /// </para>
    /// <para>
    /// <see cref="CapFileTime.Now"/> is filled in by the system as it records the change;
    /// nothing here reads a clock. A given instant is stored as precisely as the filesystem
    /// allows. The creation time is not settable: some systems cannot change it at all.
    /// </para>
    /// <para>
    /// No symbolic link is involved: the handle refers to a directory, never to a link.
    /// Safe to call concurrently with any other member of this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An instant was given that this platform cannot record at all.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the change.</exception>
    /// <exception cref="CapIOException">The change could not be made.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public void SetTimes(CapFileTime lastAccess = default, CapFileTime lastWrite = default)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        CapError error = PlatformOps.Current.SetHandleTimes(_handle, lastAccess, lastWrite);
        if (error.IsFailure)
        {
            throw FailureTranslation.ToTimesException(error);
        }
    }

    /// <summary>
    /// Sets when what a name beneath this handle holds was last read and last written.
    /// </summary>
    /// <param name="path">A relative path to the name.</param>
    /// <param name="lastAccess">
    /// What to do with the last-access time. Left as it is unless given.
    /// </param>
    /// <param name="lastWrite">
    /// What to do with the last-write time. Left as it is unless given.
    /// </param>
    /// <param name="followLink">
    /// Whether a symbolic link at the last component is followed, so that what it leads to
    /// has its times set instead of the link.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>By default it sets the name's times, and does not follow a link that holds
    /// it.</strong> A symbolic link has its own times changed, and whatever it points at is
    /// not reached, whether that is inside this handle, outside it or nowhere. This is the
    /// same rule <see cref="GetMetadata(string, bool)"/> follows, so what this sets is what
    /// that reports for the same <paramref name="followLink"/>.
    /// </para>
    /// <para>
    /// With <paramref name="followLink"/> set, a final link is followed exactly as
    /// <see cref="GetMetadata(string, bool)"/> follows one — refused with
    /// <see cref="SandboxEscapeException"/> if it leads out of this handle's subtree, and with
    /// <see cref="CapIOException"/> under <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> —
    /// and the entry the chain ends at has its times set by name. Nothing is opened, so no
    /// permission to open that entry is needed beyond what setting its times asks for.
    /// </para>
    /// <para>
    /// Path resolution up to the last component is confined exactly as it is for an open. A
    /// path spelled so that it must name a directory, one ending in a separator, is acted on
    /// only if a directory holds the name. A path ending in <c>..</c> names the directory it
    /// climbs back to, and that directory's times are set.
    /// </para>
    /// <para>
    /// <see cref="CapFileTime.Now"/> is filled in by the system as it records the change;
    /// nothing here reads a clock. A given instant is stored as precisely as the filesystem
    /// allows. The creation time is not settable: some systems cannot change it at all.
    /// </para>
    /// <para>
    /// <strong>Symbolic links, in short.</strong> The last component is followed only when
    /// <paramref name="followLink"/> asks for it. A link before it is followed while its target
    /// stays beneath this handle and refused with <see cref="SandboxEscapeException"/> when it
    /// leaves, and under <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> is refused with
    /// <see cref="CapIOException"/> wherever it points.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An instant was given that this platform cannot record at all.
    /// </exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no such name.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above it is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the change.</exception>
    /// <exception cref="CapIOException">The change could not be made.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public void SetTimes(
        string path,
        CapFileTime lastAccess = default,
        CapFileTime lastWrite = default,
        bool followLink = false)
    {
        CapPathError pathError = SetTimesCore(
            path, lastAccess, lastWrite, followLink, out CapError error, out bool refusedTime);
        if (pathError != CapPathError.None)
        {
            throw FailureTranslation.ToException(pathError, path, nameof(path));
        }

        if (refusedTime)
        {
            throw FailureTranslation.UnrecordableTime(error);
        }

        if (error.IsFailure)
        {
            throw FailureTranslation.ToException(error, path, ExpectedTarget.Name);
        }
    }

    /// <summary>
    /// Sets when what a name beneath this handle holds was last read and last written,
    /// reporting failure rather than throwing.
    /// </summary>
    /// <param name="path">A relative path to the name. See
    /// <see cref="SetTimes(string, CapFileTime, CapFileTime, bool)"/>.</param>
    /// <param name="lastAccess">What to do with the last-access time.</param>
    /// <param name="lastWrite">What to do with the last-write time.</param>
    /// <param name="followLink">
    /// Whether a symbolic link at the last component is followed. See
    /// <see cref="SetTimes(string, CapFileTime, CapFileTime, bool)"/>.
    /// </param>
    /// <returns>True when the times were set.</returns>
    /// <remarks>
    /// <para>
    /// A name that is missing, refused or not writable is reported as false. A time the
    /// platform cannot record at all still throws: that is a fault in the argument, and it
    /// would be refused wherever the name pointed.
    /// </para>
    /// <para>
    /// Symbolic links are treated exactly as
    /// <see cref="SetTimes(string, CapFileTime, CapFileTime, bool)"/> describes: a link at the
    /// last component has its own times set under either policy unless
    /// <paramref name="followLink"/> asks for it to be followed, and a refusal is reported as
    /// false. Safe to call concurrently with any other member of this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An instant was given that this platform cannot record at all.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TrySetTimes(
        string path,
        CapFileTime lastAccess = default,
        CapFileTime lastWrite = default,
        bool followLink = false)
    {
        CapPathError pathError = SetTimesCore(
            path, lastAccess, lastWrite, followLink, out CapError error, out bool refusedTime);
        if (refusedTime)
        {
            throw FailureTranslation.UnrecordableTime(error);
        }

        return pathError == CapPathError.None && error.IsSuccess;
    }

    /// <summary>
    /// Resolves the path to a directory and a name, and sets the times of what holds the
    /// name without following it — after following a final link to its end, when asked to.
    /// </summary>
    /// <param name="path">The path the caller gave.</param>
    /// <param name="lastAccess">What to do with the last-access time.</param>
    /// <param name="lastWrite">What to do with the last-write time.</param>
    /// <param name="followLink">Whether a final link is followed first.</param>
    /// <param name="error">Why the change was not made, when it was not.</param>
    /// <param name="refusedTime">
    /// Whether the failure was the platform refusing one of the times, rather than anything
    /// about the name. Only the call that sets the times can report that, so it is told apart
    /// here and not by the error's category, which resolution uses for other things.
    /// </param>
    /// <remarks>
    /// A path that must name a directory is checked by describing the name first. Something
    /// else could take the name between that check and the change. If it does, the change
    /// lands on an entry in the same directory, which the caller could have named directly.
    /// </remarks>
    private CapPathError SetTimesCore(
        string path,
        CapFileTime lastAccess,
        CapFileTime lastWrite,
        bool followLink,
        out CapError error,
        out bool refusedTime)
    {
        refusedTime = false;

        CapPathError pathError = Locate(
            path, out NameLookup lookup, out error, describing: true, followLastLink: followLink);
        using (lookup)
        {
            if (pathError != CapPathError.None || error.IsFailure)
            {
                return pathError;
            }

            if (lookup.NamesDirectoryItself)
            {
                error = PlatformOps.Current.SetHandleTimes(lookup.Directory, lastAccess, lastWrite);
            }
            else
            {
                if (lookup.RequiresDirectory)
                {
                    error = PlatformOps.Current.DescribeChild(lookup.Directory, lookup.Name, out CapNodeStat stat);
                    if (error.IsFailure)
                    {
                        return CapPathError.None;
                    }

                    if (stat.Type != CapFileType.Directory)
                    {
                        error = CapError.FromCategory(CapErrorCategory.NotADirectory);
                        return CapPathError.None;
                    }
                }

                error = PlatformOps.Current.SetChildTimes(lookup.Directory, lookup.Name, lastAccess, lastWrite);
            }

            refusedTime = error.Category == CapErrorCategory.InvalidArgument;
            return CapPathError.None;
        }
    }

    /// <summary>
    /// Resolves the path to a directory and a name, and describes what holds the name.
    /// </summary>
    /// <remarks>
    /// The check that a trailing separator was honoured is made here rather than by the
    /// platform, because the platform is being asked what something is and there is nothing
    /// wrong with the answer — the caller asked about a directory and the name turned out to
    /// hold something else, which is a fact about the request. Nothing is re-opened to
    /// decide it: the snapshot already says what the entry is.
    /// </remarks>
    private CapPathError MetadataCore(string path, bool followLink, out CapMetadata metadata, out CapError error)
    {
        metadata = default;

        CapPathError pathError = Locate(
            path, out NameLookup lookup, out error, describing: true, followLastLink: followLink);
        using (lookup)
        {
            if (pathError != CapPathError.None || error.IsFailure)
            {
                return pathError;
            }

            // A path ending in `..` has no name to describe: it named the directory the walk
            // climbed back to, which is never a link, so describing that directory is what a
            // description without following means for it.
            CapNodeStat stat;
            error = lookup.NamesDirectoryItself
                ? PlatformOps.Current.DescribeHandle(lookup.Directory, out stat)
                : PlatformOps.Current.DescribeChild(lookup.Directory, lookup.Name, out stat);
            if (error.IsFailure)
            {
                return CapPathError.None;
            }

            if (lookup.RequiresDirectory && stat.Type != CapFileType.Directory)
            {
                error = CapError.FromCategory(CapErrorCategory.NotADirectory);
                return CapPathError.None;
            }

            metadata = new CapMetadata(stat);
            return CapPathError.None;
        }
    }
}
