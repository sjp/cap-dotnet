using Cap.Fuzz.Targets;
using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Tests.Fakes;
using CsCheck;
using Microsoft.Win32.SafeHandles;

namespace Cap.Fuzz.Tests;

/// <summary>
/// The component-at-a-time walk, over generated trees and generated paths.
/// </summary>
/// <remarks>
/// <para>
/// Run one case at a time, because the walk reaches the simulated filesystem through the
/// process-wide platform slot, and two cases at once would each be walking the other's tree.
/// </para>
/// <para>
/// A failing case is shrunk to a small tree and a short path, which is what makes these worth
/// having beside the fuzzer: its findings are whatever bytes it happened on, and these are a
/// scenario a person can read.
/// </para>
/// </remarks>
public sealed class ResolutionPropertyTests
{
    private static readonly Gen<string> Word = Gen.OneOfConst([.. ResolutionScenario.Words]);

    private static readonly Gen<string> RelativeText =
        Word.Array[0, 7].Select(words => string.Join('/', words));

    private static readonly Gen<string> LinkTarget = Gen.Frequency(
        (4, RelativeText),
        (1, RelativeText.Select(text => "/" + text)));

    /// <summary>
    /// Any path the parser accepts, over any tree of directories, files, links, mount points
    /// and reparse points, resolves to something inside the sandbox or fails — and in failing
    /// or succeeding looks at nothing outside, changes nothing outside and leaves no handle
    /// open.
    /// </summary>
    [Fact]
    public void Any_accepted_path_resolves_inside_the_sandbox_or_fails()
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
                scenario => { _ = ResolutionWalkTarget.Check(scenario); },
                iter: PropertySettings.Iterations,
                threads: 1,
                print: Show);
    }

    /// <summary>
    /// In a tree with nothing to redirect a walk, the walk agrees with reading the path as
    /// text: it succeeds exactly when every name exists and is a directory and no <c>..</c>
    /// climbs above where the walk started, and then it reaches the directory the text names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything that makes resolution hard is a way for the text and the tree to disagree —
    /// a link, a rename mid-walk, a step up out of a directory that was moved. Take all of
    /// those away and the only thing left for the walk to get wrong is its own bookkeeping,
    /// above all the count of how far down it is. A <c>..</c> that it let climb past its
    /// starting point would show here as a success where the text says the path escapes.
    /// </para>
    /// <para>
    /// This is the one place reading a path as text is the right answer, and it is the right
    /// answer only because the test built a tree in which it has to be.
    /// </para>
    /// </remarks>
    [Fact]
    public void Without_links_a_walk_agrees_with_the_text()
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

                (FakeFileSystem fs, FakeNode sandbox, _) = ResolutionWalkTarget.Build(entries);
                FakeNode? expected = ReadAsText(sandbox, path);

                FakePlatformOps ops = new(fs);
                using SafeDirHandle root = ops.OpenAmbientDirectory(ResolutionScenario.SandboxName, CapAccess.Read).Value!;

                CapResult<SafeDirHandle> result =
                    PortableResolver.OpenDirectory(root, in path, CapAccess.Read, ConfinedResolveOptions.None);

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
    /// Follows the path's components through the tree's entries, stepping up by returning to
    /// the directory the step down came from. Nothing when a name is missing or not a
    /// directory, or when a step up would leave the starting directory.
    /// </summary>
    private static FakeNode? ReadAsText(FakeNode start, CapPath path)
    {
        Stack<FakeNode> above = new();
        FakeNode current = start;
        foreach (ReadOnlySpan<char> component in path.EnumerateComponents())
        {
            if (component.SequenceEqual(".."))
            {
                if (!above.TryPop(out FakeNode? parent))
                {
                    return null;
                }

                current = parent;
                continue;
            }

            if (!current.Entries.TryGetValue(component.ToString(), out FakeNode? next) || next.Type != CapNodeType.Directory)
            {
                return null;
            }

            above.Push(current);
            current = next;
        }

        return current;
    }

    private static string Show(ResolutionScenario scenario) =>
        $"{InvariantViolation.Show(scenario.Path)} ({scenario.Operation}, {scenario.Options}) in a tree of [{string.Join(", ", scenario.Entries)}]";
}
