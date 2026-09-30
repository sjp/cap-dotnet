using Cap.Fuzz.Targets;
using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Std.Testing;
using CsCheck;

namespace Cap.Fuzz.Tests;

/// <summary>
/// The in-memory confined open, over generated trees and generated paths, checked on its own
/// and against the walk.
/// </summary>
/// <remarks>
/// <para>
/// The in-memory confined legs of CI put this resolver where the kernel's <c>openat2</c>
/// would be, so the answers it gives are the answers those legs test the library against.
/// The walk is the other resolver the library has, and absent anything changing the tree the
/// two must agree on every path: the same success, or the same reason for failing.
/// </para>
/// <para>
/// Run one case at a time, like <see cref="ResolutionPropertyTests"/>, whose generators these
/// are.
/// </para>
/// </remarks>
public sealed class MemoryResolutionPropertyTests
{
    private static readonly Gen<string> Word = Gen.OneOfConst([.. ResolutionScenario.Words]);

    private static readonly Gen<string> RelativeText =
        Word.Array[0, 7].Select(words => string.Join('/', words));

    private static readonly Gen<string> LinkTarget = Gen.Frequency(
        (4, RelativeText),
        (1, RelativeText.Select(text => "/" + text)));

    /// <summary>
    /// Any path the parser accepts, over any tree of directories, files, links, mount points
    /// and reparse points, resolves inside the sandbox or fails, touches nothing outside, and
    /// reaches the same answer as the walk.
    /// </summary>
    [Fact]
    public void Any_accepted_path_resolves_inside_the_sandbox_and_as_the_walk_does()
    {
        Gen<TopologyEntry> entry = Gen.Select(Gen.Enum<EntryKind>(), Gen.Int[0, 15], Word, LinkTarget)
            .Select((kind, parent, name, target) =>
                new TopologyEntry(kind, parent, name, kind == EntryKind.SymbolicLink ? target : null));

        Gen.Select(
            entry.List[0, 12],
            RelativeText,
            Gen.Enum<ResolutionOperation>(),
            Gen.Int[0, 3].Select(options => (ConfinedResolveOptions)options))
            .Select((entries, path, operation, options) => new ResolutionScenario(entries, path, operation, options))
            .Sample(
                scenario => { _ = MemoryResolutionTarget.Check(scenario); },
                iter: PropertySettings.Iterations,
                threads: 1,
                print: Show);
    }

    /// <summary>
    /// Link chains are where the two resolvers keep different books — the walk re-parses each
    /// target in a fresh frame, the confined open splices it into one stack — so trees made of
    /// little but links and directories get a run of their own.
    /// </summary>
    [Fact]
    public void Link_heavy_trees_resolve_as_the_walk_resolves_them()
    {
        Gen<TopologyEntry> entry = Gen.Select(
                Gen.OneOfConst(EntryKind.SymbolicLink, EntryKind.SymbolicLink, EntryKind.SymbolicLink, EntryKind.Directory),
                Gen.Int[0, 15],
                Word,
                LinkTarget)
            .Select((kind, parent, name, target) =>
                new TopologyEntry(kind, parent, name, kind == EntryKind.SymbolicLink ? target : null));

        Gen.Select(
            entry.List[0, 12],
            RelativeText,
            Gen.Enum<ResolutionOperation>(),
            Gen.Int[0, 3].Select(options => (ConfinedResolveOptions)options))
            .Select((entries, path, operation, options) => new ResolutionScenario(entries, path, operation, options))
            .Sample(
                scenario => { _ = MemoryResolutionTarget.Check(scenario); },
                iter: PropertySettings.Iterations,
                threads: 1,
                print: Show);
    }

    /// <summary>
    /// In a tree with nothing to redirect resolution, the confined open agrees with reading
    /// the path as text: it succeeds exactly when every name exists and is a directory and no
    /// <c>..</c> climbs above where it started, and then it reaches the directory the text
    /// names.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="ResolutionPropertyTests.Without_links_a_walk_agrees_with_the_text"/>.
    /// With links, mount points and reparse points gone, the only thing left for the confined
    /// open to get wrong is its own count of how far down it is.
    /// </remarks>
    [Fact]
    public void Without_links_the_confined_open_agrees_with_the_text()
    {
        Gen<TopologyEntry> entry = Gen.Select(
                Gen.OneOfConst(EntryKind.Directory, EntryKind.Directory, EntryKind.File, EntryKind.MountPoint),
                Gen.Int[0, 15],
                Word)
            .Select((kind, parent, name) => new TopologyEntry(kind, parent, name));

        Gen.Select(entry.List[0, 12], RelativeText).Sample(
            (entries, text) =>
            {
                if (!CapPath.TryParse(text, CapPathSyntax.Unix, ParentLinkPolicy.Preserve, out CapPath path, out _))
                {
                    return;
                }

                (InMemoryFileSystem fs, MemoryNode sandbox, _) = MemoryResolutionTarget.Build(entries);
                MemoryNode? expected = ResolutionPropertyTests.ReadAsText(sandbox, path);

                InMemoryPlatformOps ops = fs.Backend;
                using SafeDirHandle root = ops.OpenDirectoryHandle(sandbox, CapAccess.Read);

                CapResult<SafeDirHandle> result =
                    Resolver.OpenDirectory(root, in path, CapAccess.Read, ConfinedResolveOptions.None);

                Assert.Equal(expected is not null, result.IsSuccess);
                if (result.IsSuccess)
                {
                    using SafeDirHandle reached = result.Value!;
                    Assert.True(ops.StatHandle(reached, out CapNodeInfo info).IsSuccess);
                    Assert.True(info.IsSameNodeAs(expected!.Info), $"'{text}' reached a different directory from the one it spells out.");
                }
            },
            iter: PropertySettings.Iterations,
            threads: 1,
            print: input => $"{input.Item2} in a tree of [{string.Join(", ", input.Item1)}]");
    }

    /// <summary>
    /// The disagreements the check above found when it was first run, each now answered the
    /// same way by both resolvers, and the way the kernel answers it.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>
    ///     A parent resolution ending in <c>..</c> was refused from its text by the confined
    ///     open, as a bad argument, where the walk climbs first and meets the escape or the
    ///     missing name.
    ///   </item>
    ///   <item>
    ///     The simulation the walk runs over opened a redirecting reparse point as a file when
    ///     asked to create one under its name.
    ///   </item>
    ///   <item>
    ///     The confined open in memory dropped a final <c>.</c>, so the name before it was
    ///     taken for the last one and a missing one could be "created".
    ///   </item>
    ///   <item>
    ///     The walk looked up the name of a create spelled with a trailing separator, which
    ///     the kernel refuses as naming a directory before it looks.
    ///   </item>
    ///   <item>
    ///     The walk refused a link in <c>link/.</c> as the final component of a create, where
    ///     the kernel follows it as a link on the way.
    ///   </item>
    /// </list>
    /// </remarks>
    [Fact]
    public void The_disagreements_first_found_stay_resolved()
    {
        static TopologyEntry LinkInSandbox(string name, string target) => new(EntryKind.SymbolicLink, 0, name, target);

        (ResolutionScenario Scenario, CapErrorCategory Expected)[] cases =
        [
            (new([], "..", ResolutionOperation.ResolveParent, ConfinedResolveOptions.None), CapErrorCategory.Escaped),
            (new([], "plain/../..", ResolutionOperation.ResolveParent, ConfinedResolveOptions.None), CapErrorCategory.Escaped),
            (new([], "b/..", ResolutionOperation.ResolveParent, ConfinedResolveOptions.None), CapErrorCategory.NotFound),
            (new([], "plain/..", ResolutionOperation.ResolveParent, ConfinedResolveOptions.None), CapErrorCategory.InvalidArgument),
            (new([new(EntryKind.OpaqueReparsePoint, 0, "c")], "c", ResolutionOperation.CreateFile, ConfinedResolveOptions.None), CapErrorCategory.Reparse),
            (new([], "outside/.", ResolutionOperation.CreateFile, ConfinedResolveOptions.None), CapErrorCategory.NotFound),
            (new([], "b/./", ResolutionOperation.CreateFile, ConfinedResolveOptions.None), CapErrorCategory.NotFound),
            (new([], "b/", ResolutionOperation.CreateFile, ConfinedResolveOptions.None), CapErrorCategory.IsADirectory),
            (new([], "plain/marker/", ResolutionOperation.CreateFile, ConfinedResolveOptions.None), CapErrorCategory.IsADirectory),
            (new([LinkInSandbox("a", "plain")], "a/", ResolutionOperation.CreateFile, ConfinedResolveOptions.None), CapErrorCategory.IsADirectory),
            (new([LinkInSandbox("a", "c")], "a/.", ResolutionOperation.CreateFile, ConfinedResolveOptions.None), CapErrorCategory.NotFound),
            (new([LinkInSandbox("a", "/outside")], "a/.", ResolutionOperation.CreateFile, ConfinedResolveOptions.None), CapErrorCategory.Escaped),
            (new([LinkInSandbox("a", "plain")], "a/.", ResolutionOperation.CreateFile, ConfinedResolveOptions.None), CapErrorCategory.IsADirectory),
        ];

        foreach ((ResolutionScenario scenario, CapErrorCategory expected) in cases)
        {
            ResolutionOutcome? outcome = MemoryResolutionTarget.Check(scenario);
            Assert.True(outcome is not null, $"{Show(scenario)} did not parse.");
            Assert.True(outcome.Value.Category == expected, $"{Show(scenario)} {outcome.Value}, not {expected}.");
        }
    }

    private static string Show(ResolutionScenario scenario) =>
        $"{InvariantViolation.Show(scenario.Path)} ({scenario.Operation}, {scenario.Options}) in a tree of [{string.Join(", ", scenario.Entries)}]";
}
