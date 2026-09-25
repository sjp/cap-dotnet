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
/// directory the link is in, and one with a rooted target is refused as an escape. A path
/// that ends in a separator follows a final link whatever the caller asked, and insists on
/// finding a directory. A name longer than the kernel looks up is refused as too long, as it
/// is by <c>openat2</c>, whether it came from the path or from a link's target.
/// </para>
/// <para>
/// Written as one loop over a stack of components rather than as a recursion, because a
/// recursion that started a fresh walk at each link would lose how far above the starting
/// point resolution already was, and would report a link such as <c>../sibling</c> — from a
/// subdirectory, and entirely inside the subtree — as an escape.
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
        Func<MemoryNode, string, MemoryNode?> lookup,
        out MemoryWalkResult result)
    {
        result = default;

        List<MemoryNode> stack = [start];
        Stack<string> pending = new();
        Push(pending, path, syntax);
        bool mustBeDirectory = EndsAsDirectory(path, syntax);
        int budget = LinkBudget;

        MemoryNode? parent = null;
        string? finalName = null;

        while (pending.Count > 0)
        {
            string component = pending.Pop();
            bool isFinal = pending.Count == 0;
            MemoryNode current = stack[^1];

            if (current.Type != CapNodeType.Directory)
            {
                return CapError.FromCategory(CapErrorCategory.NotADirectory);
            }

            if (component == "..")
            {
                if (stack.Count == 1)
                {
                    return CapError.FromCategory(CapErrorCategory.Escaped);
                }

                stack.RemoveAt(stack.Count - 1);
                parent = null;
                finalName = null;
                continue;
            }

            if (current.Unreadable)
            {
                return CapError.FromCategory(CapErrorCategory.PermissionDenied);
            }

            int length = syntax == CapPathSyntax.Windows ? component.Length : PathEncoding.GetByteCount(component);
            if (length > MaxNameLength)
            {
                return CapError.FromCategory(CapErrorCategory.NameTooLong);
            }

            MemoryNode? next = lookup(current, component);
            if (next is null)
            {
                result = new MemoryWalkResult(null, current, isFinal ? component : null, mustBeDirectory);
                return CapError.FromCategory(CapErrorCategory.NotFound);
            }

            if (next.Type == CapNodeType.SymbolicLink)
            {
                if ((options & ConfinedResolveOptions.RefuseSymlinks) != 0)
                {
                    return CapError.FromCategory(CapErrorCategory.SymbolicLinkLoop);
                }

                if (isFinal && !followFinalLink && !mustBeDirectory)
                {
                    result = new MemoryWalkResult(next, current, component, mustBeDirectory);
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

                // The target takes the link's place: its components are resolved before
                // whatever was left of the path, from the directory the link is in. When the
                // link was the last component, the target's own ending decides whether what it
                // leads to has to be a directory.
                if (isFinal)
                {
                    mustBeDirectory |= EndsAsDirectory(target, syntax);
                }

                Push(pending, target, syntax);
                parent = null;
                finalName = null;
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
            stack.Add(next);
        }

        MemoryNode found = stack[^1];
        result = new MemoryWalkResult(found, parent, finalName, mustBeDirectory);
        return mustBeDirectory && found.Type != CapNodeType.Directory
            ? CapError.FromCategory(CapErrorCategory.NotADirectory)
            : CapError.Success;
    }

    /// <summary>Pushes a path's components so that the first is popped first.</summary>
    private static void Push(Stack<string> pending, ReadOnlySpan<char> path, CapPathSyntax syntax)
    {
        List<string> components = [];
        int start = 0;
        for (int i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && !CapPath.IsSeparator(path[i], syntax))
            {
                continue;
            }

            ReadOnlySpan<char> component = path[start..i];
            start = i + 1;
            if (!component.IsEmpty && !component.SequenceEqual("."))
            {
                components.Add(component.ToString());
            }
        }

        for (int i = components.Count - 1; i >= 0; i--)
        {
            pending.Push(components[i]);
        }
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
