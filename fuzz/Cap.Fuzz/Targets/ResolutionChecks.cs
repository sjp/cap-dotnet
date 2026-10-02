using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Std.Testing;
using Microsoft.Win32.SafeHandles;
using static Cap.Fuzz.Targets.InvariantViolation;

namespace Cap.Fuzz.Targets;

/// <summary>How one resolution ended.</summary>
/// <param name="Category">Why it failed, or <see cref="CapErrorCategory.None"/> when it succeeded.</param>
/// <param name="Reached">
/// Where what it handed back sits, as the names leading to it from the sandbox, when it
/// succeeded. Said by place rather than by node, so that two resolvers run over two copies of
/// the same tree can be compared.
/// </param>
internal readonly record struct ResolutionOutcome(CapErrorCategory Category, string? Reached)
{
    public bool Succeeded => Category == CapErrorCategory.None;

    public override string ToString() => Succeeded ? $"reached '{Reached}'" : $"failed with {Category}";
}

/// <summary>
/// What the resolution targets share: running a scenario's operation through whichever
/// resolver a handle's backend chooses, and the checks made of every answer.
/// </summary>
internal static class ResolutionChecks
{
    /// <summary>
    /// Runs the scenario's operation on <paramref name="path"/> beneath <paramref name="root"/>
    /// and checks that anything handed back is inside <paramref name="sandbox"/>.
    /// </summary>
    public static ResolutionOutcome Resolve(
        IPlatformOps ops,
        SafeDirHandle root,
        CapPath path,
        ResolutionScenario scenario,
        MemoryNode sandbox,
        string shown)
    {
        switch (scenario.Operation)
        {
            case ResolutionOperation.OpenDirectory:
                {
                    CapResult<SafeDirHandle> result = Resolver.OpenDirectory(root, in path, CapAccess.Read, scenario.Options);
                    if (!result.IsSuccess)
                    {
                        return new ResolutionOutcome(result.Error.Category, null);
                    }

                    using SafeDirHandle directory = result.Value!;
                    Require(ops.StatHandle(directory, out CapNodeInfo info).IsSuccess, $"{shown} handed back a handle that cannot be described.");
                    return Reached(sandbox, info.VolumeId, info.NodeId, shown);
                }

            case ResolutionOperation.OpenFile:
            case ResolutionOperation.CreateFile:
                {
                    FileOpenRequest request = Request(scenario.Operation);
                    CapResult<SafeFileHandle> result = Resolver.OpenFile(root, in path, in request, scenario.Options);
                    if (!result.IsSuccess)
                    {
                        return new ResolutionOutcome(result.Error.Category, null);
                    }

                    using SafeFileHandle file = result.Value!;
                    Require(ops.DescribeHandle(file, out CapNodeStat stat).IsSuccess, $"{shown} handed back a file that cannot be described.");
                    return Reached(sandbox, stat.VolumeId, (ulong)stat.NodeId, shown);
                }

            case ResolutionOperation.ResolveParent:
                {
                    CapResult<ResolvedParent> result = Resolver.ResolveParent(root, in path, scenario.Options);
                    if (!result.IsSuccess)
                    {
                        return new ResolutionOutcome(result.Error.Category, null);
                    }

                    using ResolvedParent parent = result.Value!;
                    Require(ops.StatHandle(parent.Directory, out CapNodeInfo info).IsSuccess, $"{shown} handed back a parent that cannot be described.");

                    // What an operation then acts on is this name in that directory, so it has to
                    // be exactly one name: anything that could be read as a step elsewhere would
                    // move the operation out of the directory that was checked.
                    Require(
                        ResolutionScenario.IsEntryName(parent.Name, scenario.Syntax),
                        $"{shown} handed back {Show(parent.Name)} as the name to act on.");
                    ResolutionOutcome outcome = Reached(sandbox, info.VolumeId, info.NodeId, shown);
                    return outcome with { Reached = $"{outcome.Reached} + {parent.Name}" };
                }

            default:
                throw new InvariantViolation($"No such operation as {scenario.Operation}.");
        }
    }

    /// <summary>The request an open of a file makes for the operation.</summary>
    public static FileOpenRequest Request(ResolutionOperation operation) =>
        operation == ResolutionOperation.CreateFile
            ? new FileOpenRequest(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, FileOptions.None, 0)
            : FileOpenRequest.Existing(FileAccess.Read);

    /// <summary>
    /// Requires the object reached to be one of those in the sandbox now, and says where it
    /// is. Now rather than before, because a create adds a file and that file is what is
    /// handed back.
    /// </summary>
    private static ResolutionOutcome Reached(MemoryNode sandbox, ulong volumeId, ulong nodeId, string shown)
    {
        string? place = Locate(sandbox, node => node.VolumeId == volumeId && node.NodeId == nodeId);
        Require(place is not null, $"{shown} reached something that is not inside the sandbox.");
        return new ResolutionOutcome(CapErrorCategory.None, place);
    }

    /// <summary>
    /// The names leading from <paramref name="top"/> to the first node beneath it that
    /// <paramref name="match"/> accepts, or nothing when none does. Every node the trees here
    /// hold has one name, so the place of a node is also which node it is.
    /// </summary>
    public static string? Locate(MemoryNode top, Func<MemoryNode, bool> match)
    {
        HashSet<MemoryNode> seen = new(ReferenceEqualityComparer.Instance);
        Stack<(string Place, MemoryNode Node)> pending = new([("", top)]);
        while (pending.TryPop(out (string Place, MemoryNode Node) item))
        {
            if (!seen.Add(item.Node))
            {
                continue;
            }

            if (match(item.Node))
            {
                return item.Place;
            }

            foreach (KeyValuePair<string, MemoryNode> child in item.Node.Entries)
            {
                pending.Push(($"{item.Place}/{child.Key}", child.Value));
            }
        }

        return null;
    }

    /// <summary>
    /// Everything reachable from <paramref name="top"/> by entries alone, without following
    /// a link: what is inside it.
    /// </summary>
    public static HashSet<MemoryNode> Beneath(MemoryNode top)
    {
        HashSet<MemoryNode> found = new(ReferenceEqualityComparer.Instance);
        Stack<MemoryNode> pending = new([top]);
        while (pending.TryPop(out MemoryNode? node))
        {
            if (found.Add(node))
            {
                foreach (MemoryNode child in node.Entries.Values)
                {
                    pending.Push(child);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// A description of everything beneath a directory — each name, what it is and which
    /// object it is — that changes if anything there is added, removed, replaced or retargeted.
    /// </summary>
    public static string Describe(MemoryNode top)
    {
        System.Text.StringBuilder description = new();
        Stack<(string Name, MemoryNode Node)> pending = new([("", top)]);
        HashSet<MemoryNode> seen = new(ReferenceEqualityComparer.Instance);
        while (pending.TryPop(out (string Name, MemoryNode Node) item))
        {
            if (!seen.Add(item.Node))
            {
                continue;
            }

            _ = description.Append(item.Name).Append('|').Append(item.Node.Type).Append('|')
                .Append(item.Node.NodeId).Append('|').Append(item.Node.LinkTarget).Append('\n');
            foreach (KeyValuePair<string, MemoryNode> child in item.Node.Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                pending.Push(($"{item.Name}/{child.Key}", child.Value));
            }
        }

        return description.ToString();
    }

    /// <summary>
    /// Whether some prefix of the path has taken more steps up than down.
    /// </summary>
    public static bool ClimbsAboveStart(CapPath path)
    {
        int depth = 0;
        foreach (ReadOnlySpan<char> component in path.EnumerateComponents())
        {
            depth += component.SequenceEqual("..") ? -1 : 1;
            if (depth < 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Requires that, in a tree with no links, a path whose <c>..</c> steps climb above where
    /// it started did not resolve: with nothing to redirect it, the only place such a step can
    /// go is out.
    /// </summary>
    public static void RequireNoClimbWithoutLinks(ResolutionScenario scenario, CapPath path, bool succeeded, string shown)
    {
        if (succeeded && !scenario.Entries.Any(entry => entry.Kind == EntryKind.SymbolicLink))
        {
            Require(
                !ClimbsAboveStart(path),
                $"{shown} climbs above where it started, in a tree with no links to redirect it, and still resolved.");
        }
    }
}
