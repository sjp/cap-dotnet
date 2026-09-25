using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Tests.Fakes;
using Microsoft.Win32.SafeHandles;
using static Cap.Fuzz.Targets.InvariantViolation;

namespace Cap.Fuzz.Targets;

/// <summary>
/// The component-at-a-time walk, run over a simulated tree whose shape and links the input
/// chooses.
/// </summary>
/// <remarks>
/// <para>
/// The walk is where containment is actually decided: every <c>..</c>, every link and every
/// volume boundary on the way is met there, one name at a time. Its example-based tests
/// cover the arrangements someone thought of. Here the input decides the arrangement — links
/// to links, links that climb, links that name themselves, a mount point halfway down — and
/// what is checked is only what must hold for all of them.
/// </para>
/// <para>
/// The simulation places the sandbox beside another directory, which stands for everything
/// outside it. Whatever the scenario, the walk must:
/// </para>
/// <list type="bullet">
///   <item>never look a name up in a directory outside the sandbox;</item>
///   <item>hand back only something inside the sandbox, when it hands back anything;</item>
///   <item>leave everything outside the sandbox as it was;</item>
///   <item>close every handle it opened, whether it succeeded or not;</item>
///   <item>
///     in a tree with no links, refuse any path whose <c>..</c> steps climb above where it
///     started, since with nothing to redirect it the only place such a step can go is out.
///   </item>
/// </list>
/// <para>
/// It must also never throw, and must finish: a cycle of links has to run into the bound on
/// how many are followed, not go round forever.
/// </para>
/// </remarks>
internal static class ResolutionWalkTarget
{
    public const string Name = "resolution";

    public static void Run(ReadOnlySpan<byte> data) => Check(ResolutionScenario.Decode(data));

    /// <summary>
    /// Builds the tree, resolves the path, and checks what must hold of the walk.
    /// </summary>
    /// <returns>
    /// Whether resolution succeeded, or <see langword="null"/> when the path was not one the
    /// parser accepts and so never reached the walk.
    /// </returns>
    public static bool? Check(ResolutionScenario scenario)
    {
        if (!CapPath.TryParse(scenario.Path, CapPathSyntax.Unix, ParentLinkPolicy.Preserve, out CapPath path, out _))
        {
            return null;
        }

        (FakeFileSystem fs, FakeNode sandbox, FakeNode outside) = Build(scenario.Entries);
        string shown = $"{Show(scenario.Path)} ({scenario.Operation}, {scenario.Options})";

        FakePlatformOps ops = new(fs);
        CapResult<SafeDirHandle> opened = ops.OpenAmbientDirectory(ResolutionScenario.SandboxName, CapAccess.Read);
        Require(opened.IsSuccess, "The simulated sandbox could not be opened.");
        using SafeDirHandle root = opened.Value!;

        HashSet<FakeNode> inside = Beneath(sandbox);
        string outsideBefore = Describe(outside);
        int handlesBefore = ops.OpenHandleCount;

        fs.BeforeLookup = (directory, name) => Require(
            inside.Contains(directory),
            $"{shown} looked up {Show(name)} in a directory outside the sandbox.");

        bool succeeded;
        try
        {
            succeeded = Resolve(ops, root, path, scenario, sandbox, shown);
        }
        finally
        {
            fs.BeforeLookup = null;
        }

        Require(ops.OpenHandleCount == handlesBefore, $"{shown} left {ops.OpenHandleCount - handlesBefore} handles open.");
        Require(Describe(outside) == outsideBefore, $"{shown} changed something outside the sandbox.");

        if (succeeded && !scenario.Entries.Any(entry => entry.Kind == EntryKind.SymbolicLink))
        {
            Require(
                !ClimbsAboveStart(path),
                $"{shown} climbs above where it started, in a tree with no links to redirect it, and still resolved.");
        }

        return succeeded;
    }

    private static bool Resolve(
        FakePlatformOps ops,
        SafeDirHandle root,
        CapPath path,
        ResolutionScenario scenario,
        FakeNode sandbox,
        string shown)
    {
        switch (scenario.Operation)
        {
            case ResolutionOperation.OpenDirectory:
                {
                    CapResult<SafeDirHandle> result = PortableResolver.OpenDirectory(root, in path, CapAccess.Read, scenario.Options);
                    if (!result.IsSuccess)
                    {
                        return false;
                    }

                    using SafeDirHandle directory = result.Value!;
                    Require(ops.StatHandle(directory, out CapNodeInfo info).IsSuccess, $"{shown} handed back a handle that cannot be described.");
                    RequireInside(sandbox, info.VolumeId, info.NodeId, shown);
                    return true;
                }

            case ResolutionOperation.OpenFile:
            case ResolutionOperation.CreateFile:
                {
                    FileOpenRequest request = scenario.Operation == ResolutionOperation.CreateFile
                        ? new FileOpenRequest(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, FileOptions.None, 0)
                        : FileOpenRequest.Existing(FileAccess.Read);

                    CapResult<SafeFileHandle> result = PortableResolver.OpenFile(root, in path, in request, scenario.Options);
                    if (!result.IsSuccess)
                    {
                        return false;
                    }

                    using SafeFileHandle file = result.Value!;
                    Require(ops.DescribeHandle(file, out CapNodeStat stat).IsSuccess, $"{shown} handed back a file that cannot be described.");
                    RequireInside(sandbox, stat.VolumeId, (ulong)stat.NodeId, shown);
                    return true;
                }

            case ResolutionOperation.ResolveParent:
                {
                    CapResult<ResolvedParent> result = PortableResolver.ResolveParent(root, in path, scenario.Options);
                    if (!result.IsSuccess)
                    {
                        return false;
                    }

                    using ResolvedParent parent = result.Value!;
                    Require(ops.StatHandle(parent.Directory, out CapNodeInfo info).IsSuccess, $"{shown} handed back a parent that cannot be described.");
                    RequireInside(sandbox, info.VolumeId, info.NodeId, shown);

                    // What an operation then acts on is this name in that directory, so it has to
                    // be exactly one name: anything that could be read as a step elsewhere would
                    // move the operation out of the directory that was checked.
                    Require(
                        parent.Name.Length > 0 && parent.Name is not "." and not ".." && !parent.Name.Contains('/', StringComparison.Ordinal),
                        $"{shown} handed back {Show(parent.Name)} as the name to act on.");
                    return true;
                }

            default:
                throw new InvariantViolation($"No such operation as {scenario.Operation}.");
        }
    }

    /// <summary>
    /// Requires the object reached to be one of those in the sandbox now. Now rather than
    /// before, because a create adds a file and that file is what is handed back.
    /// </summary>
    private static void RequireInside(FakeNode sandbox, ulong volumeId, ulong nodeId, string shown) =>
        Require(
            Beneath(sandbox).Any(node => node.VolumeId == volumeId && node.NodeId == nodeId),
            $"{shown} reached something that is not inside the sandbox.");

    /// <summary>
    /// Whether some prefix of the path has taken more steps up than down.
    /// </summary>
    private static bool ClimbsAboveStart(CapPath path)
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
    /// Builds the simulated filesystem: a root holding the sandbox and the directory beside
    /// it, each with a little in it, and then the entries.
    /// </summary>
    internal static (FakeFileSystem Fs, FakeNode Sandbox, FakeNode Outside) Build(IReadOnlyList<TopologyEntry> entries)
    {
        FakeFileSystem fs = new();
        FakeNode sandbox = fs.AddDirectory(ResolutionScenario.SandboxName);
        FakeNode outside = fs.AddDirectory(ResolutionScenario.OutsideName);
        _ = fs.AddFile($"{ResolutionScenario.OutsideName}/{ResolutionScenario.OutsideFileName}");
        FakeNode outsideSub = fs.AddDirectory($"{ResolutionScenario.OutsideName}/{ResolutionScenario.OutsideSubdirectoryName}");
        FakeNode plain = fs.AddDirectory($"{ResolutionScenario.SandboxName}/{ResolutionScenario.PlainDirectoryName}");
        _ = fs.AddFile($"{ResolutionScenario.SandboxName}/{ResolutionScenario.PlainDirectoryName}/{ResolutionScenario.PlainFileName}");

        List<FakeNode> directories = [sandbox, outside, outsideSub, plain];
        ulong nextVolume = 2;

        foreach (TopologyEntry entry in entries)
        {
            if (!ResolutionScenario.IsEntryName(entry.Name))
            {
                continue;
            }

            FakeNode parent = directories[entry.Parent % directories.Count];
            FakeNode node = new()
            {
                Type = entry.Kind switch
                {
                    EntryKind.Directory or EntryKind.MountPoint => CapNodeType.Directory,
                    EntryKind.File => CapNodeType.File,
                    EntryKind.SymbolicLink => CapNodeType.SymbolicLink,
                    _ => CapNodeType.UnknownReparsePoint,
                },
                VolumeId = entry.Kind == EntryKind.MountPoint ? nextVolume++ : parent.VolumeId,
                NodeId = fs.NextNodeId(),
                LinkTarget = entry.Kind == EntryKind.SymbolicLink ? entry.Target ?? string.Empty : null,
                ReparseTag = entry.Kind == EntryKind.OpaqueReparsePoint ? AppExecLinkTag : 0,
            };

            parent.Entries[entry.Name] = node;
            if (node.Type == CapNodeType.Directory)
            {
                directories.Add(node);
            }
        }

        return (fs, sandbox, outside);
    }

    /// <summary>
    /// The tag of an application execution alias, one of the reparse points that is not a
    /// link and holds no path.
    /// </summary>
    private const uint AppExecLinkTag = 0x8000001B;

    /// <summary>
    /// Everything reachable from <paramref name="top"/> by entries alone, without following
    /// a link: what is inside it.
    /// </summary>
    internal static HashSet<FakeNode> Beneath(FakeNode top)
    {
        HashSet<FakeNode> found = new(ReferenceEqualityComparer.Instance);
        Stack<FakeNode> pending = new([top]);
        while (pending.TryPop(out FakeNode? node))
        {
            if (found.Add(node))
            {
                foreach (FakeNode child in node.Entries.Values)
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
    private static string Describe(FakeNode top)
    {
        System.Text.StringBuilder description = new();
        Stack<(string Name, FakeNode Node)> pending = new([("", top)]);
        HashSet<FakeNode> seen = new(ReferenceEqualityComparer.Instance);
        while (pending.TryPop(out (string Name, FakeNode Node) item))
        {
            if (!seen.Add(item.Node))
            {
                continue;
            }

            _ = description.Append(item.Name).Append('|').Append(item.Node.Type).Append('|')
                .Append(item.Node.NodeId).Append('|').Append(item.Node.LinkTarget).Append('\n');
            foreach (KeyValuePair<string, FakeNode> child in item.Node.Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                pending.Push(($"{item.Name}/{child.Key}", child.Value));
            }
        }

        return description.ToString();
    }
}
