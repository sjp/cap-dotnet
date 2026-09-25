using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Std;

namespace Cap.Fs.Ext;

/// <summary>
/// The directory a name is about to be used against, and that name.
/// </summary>
/// <remarks>
/// <para>
/// Every operation here that acts on a name rather than on an object starts from one of
/// these. The reason is the same one the core layer has: an operation that publishes a file,
/// or removes a tree, or copies an object performs several calls against one directory, and
/// all of them have to land in the directory the path was resolved to once. Re-resolving the
/// whole path per call would give each call its own answer, and a caller could not tell that
/// two of them had disagreed.
/// </para>
/// <para>
/// The directory is sometimes owned and sometimes borrowed. A path of a single component is
/// used against the handle the caller already holds, so nothing is opened and nothing has to
/// be closed; anything longer resolves to a handle of its own. Disposing this is correct
/// either way and closes only what was opened for it.
/// </para>
/// </remarks>
internal readonly struct ParentLocation : IDisposable
{
    private readonly IDir? _owned;

    private ParentLocation(IDir? owned, IDir directory, string name, CapError refusal = default)
    {
        _owned = owned;
        Directory = directory;
        Name = name;
        Refusal = refusal;
    }

    /// <summary>The directory the name is used against.</summary>
    public IDir Directory { get; }

    /// <summary>The single component the operation acts on.</summary>
    public string Name { get; }

    /// <summary>
    /// Why there is no name to act on, when the path ended in <c>..</c> and so named a
    /// directory by where it sits. Success otherwise. An operation must not go on to use
    /// <see cref="Name"/> when this is a failure.
    /// </summary>
    public CapError Refusal { get; }

    /// <summary>
    /// Resolves everything ahead of a path's last component and hands back that component
    /// with the directory it belongs to.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">The caller's path.</param>
    /// <param name="parameterName">What to call the path in a refusal.</param>
    /// <param name="mayNameDirectory">
    /// Whether a path spelled so that it must name a directory — one ending in a separator —
    /// is acceptable. An operation that publishes a file is not one, and the distinction is
    /// lost the moment the path is divided into components, so it is applied here.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// The part ahead of the last component named something outside the handle's authority.
    /// </exception>
    public static ParentLocation Resolve(IDir dir, string path, string parameterName, bool mayNameDirectory)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(path);

        // Parsed as the handle parses it, with `..` carried through, so that a path means the
        // same thing to these operations as it does to the handle's own members.
        if (!CapPath.TryParse(path, Handles.SyntaxOf(dir), ParentLinkPolicy.Preserve, out CapPath parsed, out CapPathError parseError))
        {
            throw FailureTranslation.ToException(parseError, path, parameterName);
        }

        if (!parsed.TrySplitLastComponent(out ReadOnlySpan<char> prefix, out ReadOnlySpan<char> name))
        {
            throw FailureTranslation.ToException(CapPathError.Empty, path, parameterName);
        }

        if (name.SequenceEqual(".."))
        {
            // A path ending in `..` names a directory by where it sits, not by a name in its
            // parent, and every operation here needs that name. Removing what it names could
            // mean removing a directory the caller never spelled out, up to and including this
            // handle's own. It is resolved through the ordinary open first, so that a climb
            // above the handle is reported as the escape it is and a missing directory as
            // missing, and only then refused for what it is.
            using (dir.OpenDir(path))
            {
            }

            if (mayNameDirectory)
            {
                return new ParentLocation(null, dir, string.Empty, CapError.FromCategory(CapErrorCategory.InvalidArgument));
            }
        }

        if (!mayNameDirectory && parsed.RequiresDirectory)
        {
            throw new ArgumentException(
                $"'{path}' is spelled so that it has to name a directory, and this operation " +
                $"acts on a file.",
                parameterName);
        }

        if (prefix.IsEmpty)
        {
            return new ParentLocation(null, dir, new string(name));
        }

        // Resolved through the ordinary confined open, so the part of the path ahead of the
        // last component is subject to the same refusals as any other: a link the handle's
        // policy declines, a component that climbs out, a name this platform will not open.
        IDir parent = dir.OpenDir(new string(prefix));
        return new ParentLocation(parent, parent, new string(name));
    }

    /// <summary>Closes the directory, if this one opened it.</summary>
    public void Dispose() => _owned?.Dispose();
}
