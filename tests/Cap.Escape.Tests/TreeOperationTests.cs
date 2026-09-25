using Cap.Fs.Ext;
using Cap.Primitives;
using Cap.Std;

namespace Cap.Escape.Tests;

/// <summary>
/// The operations that take no path at all — listing, walking, matching, copying and removing
/// a whole tree — run over a tree full of every kind of link that leads out.
/// </summary>
/// <remarks>
/// <para>
/// These are where a sandbox library's own code does the path handling, rather than its
/// caller, and they are where such libraries are most often found wanting: a walk that joins
/// names into paths, a copy that reads through a link, a recursive removal that follows one out
/// of the tree and deletes whatever it finds there. None of them names anything outside, so
/// none of them can be refused the way a path can; the only defence is that every step they
/// take is itself confined.
/// </para>
/// <para>
/// The assertions are the corpus's own: nothing outside changed, and nothing any entry yielded
/// came from outside — no name, no contents, no identity.
/// </para>
/// </remarks>
[Collection(CorpusGroup.Name)]
public sealed class TreeOperationTests
{
    /// <summary>What each test does to the hostile tree.</summary>
    public enum TreeOperation
    {
        Enumerate,
        Walk,
        WalkFollowingLinks,
        Glob,
        CopyRecreatingLinks,
        CopySkippingLinks,
        CopyRefusingLinks,
        DeleteTreeContents,
        DeleteTree,
    }

    public static TheoryData<string, SymlinkPolicy, TreeOperation> Matrix
    {
        get
        {
            TheoryData<string, SymlinkPolicy, TreeOperation> rows = [];
            foreach (string backend in Backends.OnThisHost)
            {
                foreach (SymlinkPolicy policy in new[] { SymlinkPolicy.FollowWithinSandbox, SymlinkPolicy.Deny })
                {
                    foreach (TreeOperation operation in Enum.GetValues<TreeOperation>())
                    {
                        rows.Add(backend, policy, operation);
                    }
                }
            }

            return rows;
        }
    }

    /// <summary>
    /// A whole-tree operation over a tree of escaping links reaches nothing outside it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public void A_whole_tree_operation_reaches_nothing_the_tree_links_out_to(
        string backend, SymlinkPolicy policy, TreeOperation operation)
    {
        HostFeature features = HostFeatures.Current;
        if ((features & HostFeature.Symlinks) == 0)
        {
            Assert.Skip("This volume cannot hold symbolic links, so the hostile tree cannot be built.");
        }

        using Arena arena = new();
        arena.Plant(HostileTree(features));
        string destination = Path.Join(arena.HostPath, "destination");
        HostDirectory.CreateDirectory(destination);

        Oracle oracle = new(arena, destination);
        Observation observation = new(Outcome.Success, null);
        string context = $"{operation}, {backend}, {policy}";

        using (BackendScope scope = Backends.Enter(backend))
        {
            using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), policy);
            using Dir copyTo = Dir.Open(destination, AmbientAuthority.Acquire());

            try
            {
                Perform(root, copyTo, operation, observation);
            }
            catch (Exception e) when (OperationRunner.Classify(e) is Outcome outcome)
            {
                observation = new Observation(outcome, e);
            }

            scope.AssertItRan();
        }

        oracle.AssertContained(observation, context);
        AssertNothingOutsideWasCopied(destination, context);

        Assert.True(
            Expected(operation) == observation.Outcome,
            $"{context}: expected {Expected(operation)}, got {observation.Outcome}" +
            (observation.Detail is null ? "." : $": {observation.Detail.Message}"));

        if (operation == TreeOperation.DeleteTreeContents && observation.Outcome == Outcome.Success)
        {
            Assert.Empty(HostDirectory.GetFileSystemEntries(arena.SandboxPath));
        }

        if (operation == TreeOperation.DeleteTree && observation.Outcome == Outcome.Success)
        {
            Assert.False(arena.ExistsInside(EscapeCorpus.PlainDirectory));
        }

        // Carried across as the text it stores, not as what it names -- when the copy reached
        // it before stopping at a rooted one, which depends on the order the directory lists in.
        if (operation == TreeOperation.CopyRecreatingLinks && HostEntry.Exists(Path.Join(destination, "escape-dir")))
        {
            Assert.Equal(
                $"../{EscapeCorpus.OutsideDirectory}".Replace('/', Path.DirectorySeparatorChar),
                HostEntry.LinkTarget(Path.Join(destination, "escape-dir")));
        }
    }

    /// <summary>
    /// What each operation comes to. A copy that refuses links fails at the first one, and one
    /// that makes them again fails at the first with a rooted target, which no link beneath a
    /// handle may store; every other operation completes, having treated each link as an entry
    /// and never as a way in.
    /// </summary>
    private static Outcome Expected(TreeOperation operation) => operation switch
    {
        TreeOperation.CopyRefusingLinks => Outcome.Refused,
        TreeOperation.CopyRecreatingLinks => Outcome.Escape,
        _ => Outcome.Success,
    };

    /// <summary>A tree holding every shape of link that leads somewhere it should not.</summary>
    private static List<SetupStep> HostileTree(HostFeature features)
    {
        List<SetupStep> tree =
        [
            new(SetupKind.FileLink, "escape-file", $"../{EscapeCorpus.OutsideDirectory}/{EscapeCorpus.OutsideFile}"),
            new(SetupKind.DirectoryLink, "escape-dir", $"../{EscapeCorpus.OutsideDirectory}"),
            new(SetupKind.DirectoryLink, "absolute-dir", "{outside}"),
            new(SetupKind.DirectoryLink, "parent", ".."),
            new(SetupKind.DirectoryLink, "inside-dir", EscapeCorpus.PlainDirectory),
            new(SetupKind.FileLink, "dangling", "no-such-entry"),
            new(SetupKind.FileLink, "self-cycle", "self-cycle"),
            new(SetupKind.DirectoryLink, "plain/deeper/up", $"../../{EscapeCorpus.OutsideDirectory}"),
            new(SetupKind.DirectoryLink, "plain/deeper/back-to-root", "../.."),
            new(SetupKind.File, "plain/deeper/kept"),
        ];

        if ((features & HostFeature.ProcessFilesystem) != 0)
        {
            tree.Add(new(SetupKind.DirectoryLink, "magic", "/proc/self/root"));
        }

        if ((features & HostFeature.Junctions) != 0)
        {
            tree.Add(new(SetupKind.Junction, "junction", "{outside}"));
        }

        return tree;
    }

    private static void Perform(Dir root, Dir destination, TreeOperation operation, Observation observation)
    {
        switch (operation)
        {
            case TreeOperation.Enumerate:
                foreach (DirEntry entry in root.EnumerateEntries())
                {
                    observation.Names.Add(entry.Name);
                }

                break;

            case TreeOperation.Walk:
                Collect(root.Walk(), observation);
                break;

            case TreeOperation.WalkFollowingLinks:
                Collect(root.Walk(new WalkOptions { FollowSymlinks = true }), observation);
                break;

            case TreeOperation.Glob:
                Collect(root.Glob("**/*", new WalkOptions { FollowSymlinks = true }), observation);
                break;

            case TreeOperation.CopyRecreatingLinks:
                _ = root.CopyTo(destination, new CopyOptions { Symlinks = CopyAction.Recreate, OtherKinds = CopyAction.Skip });
                break;

            case TreeOperation.CopySkippingLinks:
                _ = root.CopyTo(destination, new CopyOptions { Symlinks = CopyAction.Skip, OtherKinds = CopyAction.Skip });
                break;

            case TreeOperation.CopyRefusingLinks:
                _ = root.CopyTo(destination);
                break;

            case TreeOperation.DeleteTreeContents:
                root.DeleteTreeContents();
                break;

            case TreeOperation.DeleteTree:
                root.DeleteTree(EscapeCorpus.PlainDirectory);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation.");
        }
    }

    /// <summary>
    /// Keeps every name a walk yielded, and for each regular file the identity and contents of
    /// what opening it reaches.
    /// </summary>
    private static void Collect(IEnumerable<WalkEntry> entries, Observation observation)
    {
        foreach (WalkEntry entry in entries)
        {
            observation.Names.Add(entry.Name);
            if (entry.Type != CapFileType.File)
            {
                continue;
            }

            using ICapFile file = entry.OpenFile();
            observation.Objects.Add(file.GetMetadata().FileId);
            byte[] buffer = new byte[4096];
            observation.Contents.Add(System.Text.Encoding.UTF8.GetString(buffer, 0, file.Read(buffer, 0)));
        }
    }

    /// <summary>Nothing from outside arrived in the copy's destination.</summary>
    /// <remarks>
    /// Walked without following links, since a recreated link keeps its stored target and so
    /// can lead out of the destination exactly as it led out of the source.
    /// </remarks>
    private static void AssertNothingOutsideWasCopied(string destination, string context)
    {
        foreach (string entry in HostDirectory.GetFileSystemEntries(destination))
        {
            Assert.False(
                Path.GetFileName(entry).StartsWith("outside", StringComparison.Ordinal),
                $"{context}: the copy produced '{entry}', a name that exists only outside the sandbox.");

            HostEntryKind kind = HostEntry.KindOf(entry);
            if (kind == HostEntryKind.SymbolicLink)
            {
                continue;
            }

            if (kind == HostEntryKind.Directory)
            {
                AssertNothingOutsideWasCopied(entry, context);
            }
            else
            {
                Assert.False(
                    HostFile.ReadAllText(entry).Contains(EscapeCorpus.OutsideContent, StringComparison.Ordinal),
                    $"{context}: the copy wrote the contents of a file outside the sandbox to '{entry}'.");
            }
        }
    }
}
