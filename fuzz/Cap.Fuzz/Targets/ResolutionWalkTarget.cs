using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Std.Testing;
using Cap.Tests.Fakes;
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
    /// How resolution ended, or <see langword="null"/> when the path was not one the parser
    /// accepts and so never reached the walk.
    /// </returns>
    public static ResolutionOutcome? Check(ResolutionScenario scenario)
    {
        if (!CapPath.TryParse(scenario.Path, scenario.Syntax, ParentLinkPolicy.Preserve, out CapPath path, out _))
        {
            return null;
        }

        (FakeFileSystem fs, MemoryNode sandbox, MemoryNode outside) = Build(scenario.Entries, scenario.Syntax);
        string shown = $"{Show(scenario.Path)} ({scenario.Operation}, {scenario.Options}, {scenario.Syntax})";

        FakePlatformOps ops = new(fs);
        Require(!ops.Capabilities.SupportsConfinedOpen, "The simulation offers a confined open, so the walk would not run.");
        CapResult<SafeDirHandle> opened = ops.OpenAmbientDirectory(ResolutionScenario.SandboxName, CapAccess.Read);
        Require(opened.IsSuccess, "The simulated sandbox could not be opened.");
        using SafeDirHandle root = opened.Value!;

        HashSet<MemoryNode> inside = ResolutionChecks.Beneath(sandbox);
        string outsideBefore = ResolutionChecks.Describe(outside);
        int handlesBefore = ops.OpenHandleCount;

        fs.BeforeLookup = (directory, name) => Require(
            inside.Contains(directory),
            $"{shown} looked up {Show(name)} in a directory outside the sandbox.");

        ResolutionOutcome outcome;
        try
        {
            outcome = ResolutionChecks.Resolve(ops, root, path, scenario, sandbox, shown);
        }
        finally
        {
            fs.BeforeLookup = null;
        }

        Require(ops.OpenHandleCount == handlesBefore, $"{shown} left {ops.OpenHandleCount - handlesBefore} handles open.");
        Require(ResolutionChecks.Describe(outside) == outsideBefore, $"{shown} changed something outside the sandbox.");
        ResolutionChecks.RequireNoClimbWithoutLinks(scenario, path, outcome.Succeeded, shown);

        return outcome;
    }

    /// <summary>
    /// Builds the simulated filesystem: a root holding the sandbox and the directory beside
    /// it, each with a little in it, and then the entries. Link targets are read by
    /// <paramref name="syntax"/>'s rules.
    /// </summary>
    internal static (FakeFileSystem Fs, MemoryNode Sandbox, MemoryNode Outside) Build(
        IReadOnlyList<TopologyEntry> entries,
        CapPathSyntax syntax)
    {
        FakeFileSystem fs = new() { PathSyntax = syntax };
        MemoryNode sandbox = fs.AddDirectory(ResolutionScenario.SandboxName);
        MemoryNode outside = fs.AddDirectory(ResolutionScenario.OutsideName);
        _ = fs.AddFile($"{ResolutionScenario.OutsideName}/{ResolutionScenario.OutsideFileName}");
        MemoryNode outsideSub = fs.AddDirectory($"{ResolutionScenario.OutsideName}/{ResolutionScenario.OutsideSubdirectoryName}");
        MemoryNode plain = fs.AddDirectory($"{ResolutionScenario.SandboxName}/{ResolutionScenario.PlainDirectoryName}");
        _ = fs.AddFile($"{ResolutionScenario.SandboxName}/{ResolutionScenario.PlainDirectoryName}/{ResolutionScenario.PlainFileName}");

        List<MemoryNode> directories = [sandbox, outside, outsideSub, plain];
        ulong nextVolume = 2;

        foreach (TopologyEntry entry in entries)
        {
            if (!ResolutionScenario.IsEntryName(entry.Name, syntax))
            {
                continue;
            }

            MemoryNode parent = directories[entry.Parent % directories.Count];
            MemoryNode node = new()
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
                ReparseTag = entry.Kind == EntryKind.OpaqueReparsePoint ? ContainerLinkTag : 0,
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
    /// The tag of a container link, one of the reparse points that redirects without being a
    /// link or holding a path.
    /// </summary>
    /// <remarks>
    /// A redirecting tag, because that is what an opaque node models: a reparse point whose tag
    /// only names the filter serving the entry is the file or directory it is, and the tree
    /// already has those.
    /// </remarks>
    private const uint ContainerLinkTag = 0xA0000027;
}
