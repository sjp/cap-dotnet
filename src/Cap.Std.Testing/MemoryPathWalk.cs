using System.Buffers;
using System.Runtime.CompilerServices;
using Cap.Primitives;
using Cap.Primitives.Interop;

namespace Cap.Std.Testing;

/// <summary>
/// Resolves a whole relative path through a tree held in memory, in one call, confined to the
/// subtree it starts from: the in-memory counterpart of the kernel's confined open.
/// </summary>
/// <remarks>
/// <para>
/// Follows what <c>openat2</c> does under <c>RESOLVE_BENEATH</c>, because that is the
/// confined open whose answers the rest of the library was written against. A <c>..</c>
/// above the starting directory is refused as an escape rather than clamped, so a path that
/// tries to leave is reported as having tried and not quietly rewritten into one that stays.
/// A link's target is resolved in the same walk as the path that reached it, from the
/// directory the link is in, and is read under the same path rules as a caller's own string:
/// one that is rooted, or that names something those rules do not allow, is refused exactly as
/// a caller passing it would be. A path that ends in a separator follows a final link whatever
/// the caller asked, and insists on finding a directory. A name longer than the kernel looks up
/// is refused as too long, as it is by <c>openat2</c>, whether it came from the path or from a
/// link's target.
/// </para>
/// <para>
/// Written as one loop over a stack of components rather than as a recursion, because a
/// recursion that started a fresh walk at each link would lose how far above the starting
/// point resolution already was, and would report a link such as <c>../sibling</c> — from a
/// subdirectory, and entirely inside the subtree — as an escape.
/// </para>
/// <para>
/// Nothing is allocated for the components a resolution passes through, only for the final
/// name it reports. The components still to come are positions in the caller's path and in
/// the targets of the links being followed, rather than strings cut from them, a name is
/// looked up as a span, and the directories passed through are kept in an array borrowed from
/// the pool, because the walk runs once for every path a test opens and a suite opens a great
/// many.
/// </para>
/// </remarks>
internal static class MemoryPathWalk
{
    /// <summary>
    /// How many links one resolution may follow before it is refused as a loop: the number
    /// Linux allows.
    /// </summary>
    public const int LinkBudget = 40;

    /// <summary>
    /// The longest single name the kernel looks up: <c>NAME_MAX</c> bytes on Linux, and as many
    /// UTF-16 units on Windows.
    /// </summary>
    public const int MaxNameLength = 255;

    /// <summary>
    /// Whether <paramref name="name"/> is longer than <see cref="MaxNameLength"/>, measured as
    /// <paramref name="syntax"/>'s system measures it: UTF-8 bytes under Unix rules, UTF-16
    /// units under Windows rules.
    /// </summary>
    public static bool NameTooLong(ReadOnlySpan<char> name, CapPathSyntax syntax) =>
        (syntax == CapPathSyntax.Windows ? name.Length : PathEncoding.GetByteCount(name)) > MaxNameLength;

    /// <summary>
    /// Resolves <paramref name="path"/> beneath <paramref name="start"/>.
    /// </summary>
    /// <param name="start">The directory resolution is confined to.</param>
    /// <param name="path">A relative path under <paramref name="syntax"/>.</param>
    /// <param name="syntax">Which characters separate components, and what counts as rooted.</param>
    /// <param name="options">Links and mount crossings to refuse.</param>
    /// <param name="followFinalLink">
    /// Whether a link at the last component is followed. When false it is refused as
    /// <see cref="CapErrorCategory.SymbolicLinkLoop"/>, unless the path ends in a separator.
    /// </param>
    /// <param name="lookup">Looks one name up in one directory.</param>
    /// <param name="result">What was found, and where, as far as resolution got.</param>
    /// <returns>Success, or why resolution stopped.</returns>
    public static CapError Resolve(
        MemoryNode start,
        ReadOnlySpan<char> path,
        CapPathSyntax syntax,
        ConfinedResolveOptions options,
        bool followFinalLink,
        Func<MemoryNode, ReadOnlySpan<char>, MemoryNode?> lookup,
        out MemoryWalkResult result)
    {
        DirectoryStack stack = new(start);
        try
        {
            return Walk(ref stack, path, syntax, options, followFinalLink, lookup, out result);
        }
        finally
        {
            stack.Dispose();
        }
    }

    private static CapError Walk(
        ref DirectoryStack stack,
        ReadOnlySpan<char> path,
        CapPathSyntax syntax,
        ConfinedResolveOptions options,
        bool followFinalLink,
        Func<MemoryNode, ReadOnlySpan<char>, MemoryNode?> lookup,
        out MemoryWalkResult result)
    {
        result = default;

        PendingComponents pending = new(path, syntax);
        bool mustBeDirectory = EndsAsDirectory(path, syntax);
        int budget = LinkBudget;

        // The last name looked up, and where; empty when the last step was `.` or `..`. The
        // name stays a view of the string it came from until a result needs it.
        MemoryNode? parent = null;
        ReadOnlySpan<char> finalName = default;

        while (pending.TryNext(out ReadOnlySpan<char> component))
        {
            bool isFinal = !pending.HasMore;
            MemoryNode current = stack.Top;

            if (current.Type != CapNodeType.Directory)
            {
                return CapError.FromCategory(CapErrorCategory.NotADirectory);
            }

            if (component.SequenceEqual(".."))
            {
                if (stack.Count == 1)
                {
                    return CapError.FromCategory(CapErrorCategory.Escaped);
                }

                stack.Pop();
                parent = null;
                finalName = default;
                continue;
            }

            // A step that stays put, but a step all the same: the name before it is not the
            // last one, so a missing name there is missing on the way and not one that could
            // be created, as it is for the kernel.
            if (component.SequenceEqual("."))
            {
                parent = null;
                finalName = default;
                continue;
            }

            if (current.Unreadable)
            {
                return CapError.FromCategory(CapErrorCategory.PermissionDenied);
            }

            if (NameTooLong(component, syntax))
            {
                return CapError.FromCategory(CapErrorCategory.NameTooLong);
            }

            MemoryNode? next = lookup(current, component);
            if (next is null)
            {
                result = new MemoryWalkResult(null, current, isFinal ? component.ToString() : null, mustBeDirectory);
                return CapError.FromCategory(CapErrorCategory.NotFound);
            }

            if (next.Type == CapNodeType.SymbolicLink)
            {
                if ((options & ConfinedResolveOptions.RefuseSymlinks) != 0)
                {
                    // A refused final link still holds the name, and an exclusive creation is
                    // refused for that before the link is looked at, as O_EXCL is by openat2.
                    if (isFinal && !mustBeDirectory)
                    {
                        result = new MemoryWalkResult(next, current, component.ToString(), mustBeDirectory);
                    }

                    return CapError.FromCategory(CapErrorCategory.SymbolicLinkLoop);
                }

                if (isFinal && !followFinalLink && !mustBeDirectory)
                {
                    result = new MemoryWalkResult(next, current, component.ToString(), mustBeDirectory);
                    return CapError.FromCategory(CapErrorCategory.SymbolicLinkLoop);
                }

                if (--budget < 0)
                {
                    return CapError.FromCategory(CapErrorCategory.SymbolicLinkLoop);
                }

                string target = next.LinkTarget ?? string.Empty;
                if (target.Length == 0)
                {
                    return CapError.FromCategory(CapErrorCategory.NotFound);
                }

                if (CapPath.IsRooted(target, syntax))
                {
                    return CapError.FromCategory(CapErrorCategory.Escaped);
                }

                // The rest of the target is read under the same rules as a caller's own path,
                // and refused for the same reasons: under Windows rules a component naming a
                // character device leads out of the subtree wherever it appears, and one the
                // platform would rewrite is a name resolution cannot use. A target spelling
                // nothing but the directory it sits in -- `.` -- is not a refusal at all, and
                // the components that follow take the link's place as they do below.
                if (!CapPath.TryParse(target, syntax, ParentLinkPolicy.Preserve, out _, out CapPathError unusable) &&
                    unusable != CapPathError.Empty)
                {
                    return PortableResolver.TranslateLinkTarget(unusable);
                }

                // The target takes the link's place: its components are resolved before
                // whatever was left of the path, from the directory the link is in. When the
                // link was the last component, the target's own ending decides whether what it
                // leads to has to be a directory.
                if (isFinal)
                {
                    mustBeDirectory |= EndsAsDirectory(target, syntax);
                }

                pending.Push(target);
                parent = null;
                finalName = default;
                continue;
            }

            if (next.Type == CapNodeType.UnknownReparsePoint)
            {
                return CapError.FromCategory(CapErrorCategory.Reparse);
            }

            // Reported as the walk reports it. The kernel reports the same refusal as an escape,
            // but a tree in memory has one volume unless a test grafts on another, and a test
            // that does is checking the walk's answer.
            if ((options & ConfinedResolveOptions.RefuseMountCrossing) != 0 &&
                next.VolumeId != current.VolumeId)
            {
                return CapError.FromCategory(CapErrorCategory.CrossDevice);
            }

            parent = current;
            finalName = component;
            stack.Push(next);
        }

        MemoryNode found = stack.Top;
        result = new MemoryWalkResult(found, parent, parent is null ? null : finalName.ToString(), mustBeDirectory);
        return mustBeDirectory && found.Type != CapNodeType.Directory
            ? CapError.FromCategory(CapErrorCategory.NotADirectory)
            : CapError.Success;
    }

    /// <summary>
    /// Whether a path insists on ending at a directory: it ends in a separator, or its last
    /// component is <c>.</c> or <c>..</c>.
    /// </summary>
    private static bool EndsAsDirectory(ReadOnlySpan<char> path, CapPathSyntax syntax)
    {
        if (path.IsEmpty)
        {
            return false;
        }

        if (CapPath.IsSeparator(path[^1], syntax))
        {
            return true;
        }

        int start = path.Length;
        while (start > 0 && !CapPath.IsSeparator(path[start - 1], syntax))
        {
            start--;
        }

        ReadOnlySpan<char> last = path[start..];
        return last.SequenceEqual(".") || last.SequenceEqual("..");
    }

    /// <summary>
    /// The components a resolution has still to look at, in the order it will meet them: what
    /// is left of the caller's path, with the target of each link being followed in front of
    /// whatever was left when the link was met.
    /// </summary>
    /// <remarks>
    /// Each source is held with a position in it rather than cut into components, so taking a
    /// component costs nothing. A <c>.</c> is returned like any other name, because the walk
    /// treats it as a step.
    /// </remarks>
    private ref struct PendingComponents
    {
        private readonly ReadOnlySpan<char> _path;
        private readonly CapPathSyntax _syntax;
        private int _pathPosition;
        private Targets _targets;
        private int _targetCount;

        public PendingComponents(ReadOnlySpan<char> path, CapPathSyntax syntax)
        {
            _path = path;
            _syntax = syntax;
        }

        /// <summary>Whether any component remains after the one last taken.</summary>
        public readonly bool HasMore
        {
            get
            {
                for (int i = _targetCount - 1; i >= 0; i--)
                {
                    if (StartsAt(_targets[i].Text, _targets[i].Position, _syntax) >= 0)
                    {
                        return true;
                    }
                }

                return StartsAt(_path, _pathPosition, _syntax) >= 0;
            }
        }

        /// <summary>Takes the next component, finishing with any link target that has run out.</summary>
        public bool TryNext(out ReadOnlySpan<char> component)
        {
            while (_targetCount > 0)
            {
                ref Target top = ref _targets[_targetCount - 1];
                if (Take(top.Text, ref top.Position, _syntax, out component))
                {
                    return true;
                }

                top = default;
                _targetCount--;
            }

            return Take(_path, ref _pathPosition, _syntax, out component);
        }

        /// <summary>Puts a followed link's target in front of everything still pending.</summary>
        /// <remarks>
        /// Never more than <see cref="LinkBudget"/> at once, because the walk spends one of
        /// its budget on every link before it gets here.
        /// </remarks>
        public void Push(string target) => _targets[_targetCount++] = new Target { Text = target };

        private static bool Take(ReadOnlySpan<char> text, scoped ref int position, CapPathSyntax syntax, out ReadOnlySpan<char> component)
        {
            int start = StartsAt(text, position, syntax);
            if (start < 0)
            {
                position = text.Length;
                component = default;
                return false;
            }

            int end = start;
            while (end < text.Length && !CapPath.IsSeparator(text[end], syntax))
            {
                end++;
            }

            position = end;
            component = text[start..end];
            return true;
        }

        /// <summary>Where the next component begins, at or after a position, or -1 when none does.</summary>
        private static int StartsAt(ReadOnlySpan<char> text, int position, CapPathSyntax syntax)
        {
            while (position < text.Length && CapPath.IsSeparator(text[position], syntax))
            {
                position++;
            }

            return position < text.Length ? position : -1;
        }
    }

    /// <summary>A link target being followed, and how far into it the walk has got.</summary>
    private struct Target
    {
        public string Text;
        public int Position;
    }

    /// <summary>Room for every link target a resolution can be following at once.</summary>
    [InlineArray(LinkBudget)]
    private struct Targets
    {
        private Target _first;
    }

    /// <summary>
    /// The directories a resolution has passed through and not come back out of, so that a
    /// <c>..</c> can return to the one before, in an array borrowed from the pool.
    /// </summary>
    private ref struct DirectoryStack
    {
        private MemoryNode[] _nodes;

        public DirectoryStack(MemoryNode start)
        {
            _nodes = ArrayPool<MemoryNode>.Shared.Rent(16);
            _nodes[0] = start;
            Count = 1;
        }

        public int Count { get; private set; }

        public readonly MemoryNode Top => _nodes[Count - 1];

        public void Push(MemoryNode node)
        {
            if (Count == _nodes.Length)
            {
                MemoryNode[] grown = ArrayPool<MemoryNode>.Shared.Rent(_nodes.Length * 2);
                _nodes.AsSpan(0, Count).CopyTo(grown);
                ArrayPool<MemoryNode>.Shared.Return(_nodes, clearArray: true);
                _nodes = grown;
            }

            _nodes[Count++] = node;
        }

        public void Pop() => _nodes[--Count] = null!;

        /// <summary>
        /// Returns the array, cleared so that the pool does not keep the tree's nodes alive.
        /// </summary>
        public void Dispose()
        {
            ArrayPool<MemoryNode>.Shared.Return(_nodes, clearArray: true);
            _nodes = [];
            Count = 0;
        }
    }
}

/// <summary>What a confined resolution in memory found, and where.</summary>
/// <param name="Node">
/// What the path named, when it named something: on success, the object reached; on a refused
/// final link, the link itself.
/// </param>
/// <param name="Parent">
/// The directory the last component was looked up in, when the last step was a lookup rather
/// than <c>..</c> or <c>.</c>.
/// </param>
/// <param name="FinalName">
/// The last component, when <paramref name="Parent"/> is known. Set on a failed lookup only
/// when it was the last component that was missing, which is the one case where the name can
/// be created.
/// </param>
/// <param name="RequiresDirectory">Whether the path, or a final link's target, insisted on a directory.</param>
internal readonly record struct MemoryWalkResult(
    MemoryNode? Node,
    MemoryNode? Parent,
    string? FinalName,
    bool RequiresDirectory);
