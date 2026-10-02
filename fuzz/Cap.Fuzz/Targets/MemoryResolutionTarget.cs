using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Std.Testing;
using Cap.Tests.Fakes;
using static Cap.Fuzz.Targets.InvariantViolation;

namespace Cap.Fuzz.Targets;

/// <summary>
/// The in-memory confined open, run over the same trees as the walk and held both to the
/// walk's checks and to the walk's answers.
/// </summary>
/// <remarks>
/// <para>
/// <c>Cap.Std.Testing</c> resolves a whole path in one call, in memory, the way
/// <c>openat2</c> does under <c>RESOLVE_BENEATH</c>: its own <c>..</c> accounting, its own
/// bound on links, its own refusals of links and of mount crossings. It is what the
/// in-memory confined legs of CI put in the kernel's place, and it ships to consumers. A
/// version of it more permissive than the kernel would let those legs pass while hiding an
/// escape, so it is checked for everything the walk is checked for:
/// </para>
/// <list type="bullet">
///   <item>it never looks a name up in a directory outside the sandbox;</item>
///   <item>it hands back only something inside the sandbox, when it hands back anything;</item>
///   <item>it leaves everything outside the sandbox as it was;</item>
///   <item>it closes every handle it opened, whether it succeeded or not;</item>
///   <item>
///     in a tree with no links, it refuses any path whose <c>..</c> steps climb above where
///     it started.
///   </item>
/// </list>
/// <para>
/// And, because the confined open and the walk are meant to reach the same verdict when
/// nothing changes the tree underneath them, it must agree with the walk: the same success
/// or the same reason for failing, and on success the same object.
/// </para>
/// <para>
/// The operations go through the library's own choice of resolver, with a handle from an
/// in-memory filesystem set to the confined open, which is the path those CI legs take. That
/// path gives no way to watch each lookup, so the resolver is also run directly with a lookup
/// that checks where it is looking.
/// </para>
/// </remarks>
internal static class MemoryResolutionTarget
{
    public const string Name = "resolution-memory";

    public static void Run(ReadOnlySpan<byte> data) => Check(ResolutionScenario.Decode(data));

    /// <summary>
    /// Builds the tree, resolves the path, checks what must hold of the confined open, and
    /// checks that the walk reached the same answer.
    /// </summary>
    /// <returns>
    /// How resolution ended, or <see langword="null"/> when the path was not one the parser
    /// accepts and so never reached either resolver.
    /// </returns>
    public static ResolutionOutcome? Check(ResolutionScenario scenario)
    {
        if (!CapPath.TryParse(scenario.Path, scenario.Syntax, ParentLinkPolicy.Preserve, out CapPath path, out _))
        {
            return null;
        }

        string shown = $"{Show(scenario.Path)} ({scenario.Operation}, {scenario.Options}, {scenario.Syntax})";

        (InMemoryFileSystem fs, MemoryNode sandbox, MemoryNode outside) = Build(scenario.Entries, scenario.Syntax);
        HashSet<MemoryNode> inside = ResolutionChecks.Beneath(sandbox);
        string outsideBefore = ResolutionChecks.Describe(outside);

        CheckLookups(scenario, path, sandbox, inside, shown);

        InMemoryPlatformOps ops = fs.Backend;
        Require(ops.Capabilities.SupportsConfinedOpen, "The in-memory filesystem does not offer the confined open.");
        int handlesBefore = ops.OpenHandleCount;
        ResolutionOutcome outcome;
        using (SafeDirHandle root = ops.OpenDirectoryHandle(sandbox, CapAccess.Read))
        {
            outcome = ResolutionChecks.Resolve(ops, root, path, scenario, sandbox, shown);
        }

        Require(ops.OpenHandleCount == handlesBefore, $"{shown} left {ops.OpenHandleCount - handlesBefore} handles open.");
        Require(ResolutionChecks.Describe(outside) == outsideBefore, $"{shown} changed something outside the sandbox.");
        ResolutionChecks.RequireNoClimbWithoutLinks(scenario, path, outcome.Succeeded, shown);

        ResolutionOutcome walk = ResolutionWalkTarget.Check(scenario)!.Value;
        Require(
            outcome == walk,
            $"{shown} {outcome} through the confined open in memory but {walk} through the walk, " +
            $"in a tree of [{string.Join(", ", scenario.Entries)}].");

        return outcome;
    }

    /// <summary>
    /// Runs the confined resolver itself over the tree, with a lookup that refuses to be
    /// asked about any directory outside the sandbox, and checks that what it reports finding
    /// is inside.
    /// </summary>
    private static void CheckLookups(
        ResolutionScenario scenario,
        CapPath path,
        MemoryNode sandbox,
        HashSet<MemoryNode> inside,
        string shown)
    {
        MemoryNode? Lookup(MemoryNode directory, ReadOnlySpan<char> name)
        {
            Require(inside.Contains(directory), $"{shown} looked up {Show(name.ToString())} in a directory outside the sandbox.");
            return directory.Entries.GetValueOrDefault(name);
        }

        CapError error = MemoryPathWalk.Resolve(
            sandbox,
            path.Raw,
            scenario.Syntax,
            scenario.Options,
            followFinalLink: ResolutionChecks.Request(scenario.Operation).FollowsFinalLink,
            Lookup,
            out MemoryWalkResult found);

        Require(
            found.Parent is null || inside.Contains(found.Parent),
            $"{shown} reported a directory outside the sandbox as where it looked its last name up.");
        if (error.IsSuccess)
        {
            Require(found.Node is not null && inside.Contains(found.Node), $"{shown} reached something that is not inside the sandbox.");
        }
    }

    /// <summary>
    /// Builds the tree the walk would be run over, as an in-memory filesystem that resolves
    /// by the confined open: the same names, kinds, link targets and volumes, in the same
    /// places.
    /// </summary>
    internal static (InMemoryFileSystem Fs, MemoryNode Sandbox, MemoryNode Outside) Build(
        IReadOnlyList<TopologyEntry> entries,
        CapPathSyntax syntax)
    {
        (FakeFileSystem simulated, _, _) = ResolutionWalkTarget.Build(entries, syntax);
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions
        {
            PathSyntax = syntax,

            // The simulation the walk runs over compares names exactly under either syntax, so
            // this copy does too, as an NTFS directory marked case-sensitive does. Otherwise the
            // two would disagree over a name spelled in another case, which is a difference
            // between the trees and not between the resolvers.
            CaseSensitive = true,
            Resolution = ResolutionBackend.ConfinedOpen,
        });

        ulong simulatedVolume = simulated.Root.VolumeId;
        Copy(simulated.Root, fs.Root);
        return (fs, fs.Root.Entries[ResolutionScenario.SandboxName], fs.Root.Entries[ResolutionScenario.OutsideName]);

        void Copy(MemoryNode from, MemoryNode to)
        {
            foreach ((string name, MemoryNode child) in from.Entries)
            {
                MemoryNode copy = fs.NewNode(child.Type);
                if (child.VolumeId != simulatedVolume)
                {
                    copy.VolumeId = child.VolumeId;
                }

                copy.LinkTarget = child.LinkTarget;
                copy.ReparseTag = child.ReparseTag;
                fs.Attach(to, name, copy);
                Copy(child, copy);
            }
        }
    }
}
