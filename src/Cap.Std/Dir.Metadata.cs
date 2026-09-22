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
    /// Asked of the handle and not of a name, so nothing is resolved and there is nothing
    /// for a concurrent rename to interfere with: the answer describes the object this
    /// handle was opened on, whatever that object is currently called and whether it is
    /// called anything at all. A directory that has been removed while this handle was held
    /// still answers, which is the honest result — the object exists as long as something
    /// holds it open, even once no directory names it.
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
    /// Describes what a name beneath this handle holds.
    /// </summary>
    /// <param name="path">A relative path to the name to describe.</param>
    /// <returns>A snapshot of the entry, taken at the moment of the call.</returns>
    /// <remarks>
    /// <para>
    /// <strong>It describes the name, and does not follow a link that holds it.</strong> A
    /// symbolic link is reported as a symbolic link, with its own length and its own times,
    /// whether its target exists, does not exist, or lies outside the subtree entirely. That
    /// is the same rule every other member taking a name follows, and it is what keeps the
    /// answer from depending on the handle's symbolic-link policy — the same name would
    /// otherwise describe one thing through a permissive handle and something else through a
    /// strict one. A caller that wants the target described opens the target and asks the
    /// handle it gets back.
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
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no such name.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above it is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the question.</exception>
    /// <exception cref="CapIOException">The question could not be answered.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public CapMetadata GetMetadata(string path)
    {
        CapPathError pathError = MetadataCore(path, out CapMetadata metadata, out CapError error);
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
    /// <param name="path">A relative path to the name. See <see cref="GetMetadata(string)"/>.</param>
    /// <param name="metadata">The snapshot, when this returns true.</param>
    /// <returns>True when the name was described.</returns>
    /// <remarks>
    /// A name that is not there is the expected answer for this question rather than an
    /// exceptional one — describing an entry read a moment ago is the ordinary case, and the
    /// entry being gone by now is the ordinary way that fails. Arguments that are wrong
    /// rather than unlucky still throw.
    /// </remarks>
    public bool TryGetMetadata(string path, out CapMetadata metadata)
    {
        CapPathError pathError = MetadataCore(path, out metadata, out CapError error);
        return pathError == CapPathError.None && error.IsSuccess;
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
    private CapPathError MetadataCore(string path, out CapMetadata metadata, out CapError error)
    {
        metadata = default;

        CapPathError pathError = Locate(path, out NameLookup lookup, out error);
        using (lookup)
        {
            if (pathError != CapPathError.None || error.IsFailure)
            {
                return pathError;
            }

            error = PlatformOps.Current.DescribeChild(lookup.Directory, lookup.Name, out CapNodeStat stat);
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
