using System.Text;
using Cap.Fs.Ext;
using Cap.Primitives;
using Cap.Std;

namespace Cap.Stress.Tests;

/// <summary>
/// Removing and copying whole trees while an attacker swaps directories in them for links
/// pointing outside.
/// </summary>
/// <remarks>
/// <para>
/// These are the operations with the worst history. A tree removal that lists a directory,
/// joins each name onto its path and removes the result will, if a directory in the middle is
/// replaced by a link between the listing and the removal, remove whatever the link points at —
/// with the caller's own privileges, and very often in the caller's home directory. A copy with
/// the same flaw reads from wherever the link points and hands the contents to whoever asked
/// for the copy.
/// </para>
/// <para>
/// Each round builds a tree, starts an attacker exchanging every directory in it with a link
/// to a directory outside the sandbox and back again, runs the operation through it, and then
/// checks outside the sandbox byte for byte. Whether the operation itself succeeded is recorded
/// and does not matter: a removal that gives up half-way is fine, and a removal that finishes
/// having also emptied the directory outside is the one failure these tests exist for.
/// </para>
/// </remarks>
public sealed class TreeRaceTests(ITestOutputHelper output)
{
    /// <summary>How many single attempts one round of building and tearing down a tree is worth.</summary>
    private const int AttemptsPerRound = 100;

    /// <summary>How deep each round's tree is.</summary>
    private const int Depth = 4;

    /// <summary>The backends this host has, as the test framework's rows.</summary>
    public static TheoryData<string> OnThisHost => new(Backends.OnThisHost);

    /// <summary>
    /// Removing a tree while its directories are swapped for links to a directory outside
    /// never removes or changes anything outside.
    /// </summary>
    /// <remarks>
    /// Rounds alternate between removing the tree by name and emptying it through a handle,
    /// since the two start differently — one resolves a name and the other has been holding the
    /// directory all along — and share everything after that.
    /// </remarks>
    [Theory]
    [MemberData(nameof(OnThisHost))]
    public void Removing_a_tree_under_attack_never_touches_anything_outside(string backend)
    {
        using StressArena arena = new();
        arena.RequireSymbolicLinks();
        PopulateOutside(arena);

        string victim = arena.Inside("victim");
        Tally tally = new();
        string context = "tree removed under attack on " + backend;
        string outsideBefore = arena.SnapshotOutside();
        Descriptors descriptors = Descriptors.Before(arena.HostPath);
        int rounds = StressSettings.Rounds(AttemptsPerRound);

        using (BackendScope scope = Backends.Enter(backend))
        using (Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire()))
        {
            for (int round = 0; round < rounds; round++)
            {
                string[] levels = BuildTree(victim, arena.Outside(StressArena.DirectoryName));
                SwappingAttacker attacker = new(levels);
                using (ThreadAdversary adversary = new(attacker.Cycle))
                {
                    try
                    {
                        if (round % 2 == 0)
                        {
                            root.DeleteTree("victim");
                        }
                        else
                        {
                            using Dir tree = root.OpenDir("victim");
                            tree.DeleteTreeContents();
                        }

                        tally.Record(Outcome.Consistent);
                    }
                    catch (Exception e)
                    {
                        tally.Refused(e);
                    }

                    tally.AdversaryCycles = (tally.AdversaryCycles ?? 0) + adversary.Stop();
                }

                Assert.True(
                    outsideBefore == arena.SnapshotOutside(),
                    $"{context}: round {round} changed the directory outside the sandbox. {tally}");

                HostOps.RemoveWithoutFollowing(victim);
            }

            scope.AssertItRan();
        }

        StressReport.Publish(output, "tree removed under attack", backend, tally);
        descriptors.AssertNoneLeaked(context);
        tally.RequireContest(context, Outcome.Consistent, Outcome.RefusedAsEscape, Outcome.OtherRefusal, Outcome.Missing);
    }

    /// <summary>
    /// Copying a tree while its directories are swapped for links to a directory outside, and
    /// while links are planted in the destination, never reads from or writes to anything
    /// outside.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both ends are attacked. In the source, the attacker swaps each directory for a link to a
    /// directory outside full of recognisable content, so a copy that followed one would carry
    /// that content into the destination. In the destination, it plants a link to a writable
    /// directory outside at the name the copy is about to create, so a copy that followed one
    /// would write outside.
    /// </para>
    /// <para>
    /// Rounds cycle through the three things a copy can do with a link it meets — stop, skip it,
    /// or make the same link again — because each takes a different path through the copier.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(OnThisHost))]
    public void Copying_a_tree_under_attack_never_reads_or_writes_outside(string backend)
    {
        using StressArena arena = new();
        arena.RequireSymbolicLinks();
        PopulateOutside(arena);

        string source = arena.Inside("source");
        string destination = arena.Inside("destination");
        string outsideDirectory = arena.Outside(StressArena.DirectoryName);
        CopyAction[] actions = [CopyAction.Fail, CopyAction.Skip, CopyAction.Recreate];

        Tally tally = new();
        string context = "tree copied under attack on " + backend;
        string outsideBefore = arena.SnapshotOutside();
        Descriptors descriptors = Descriptors.Before(arena.HostPath);
        int rounds = StressSettings.Rounds(AttemptsPerRound);

        using (BackendScope scope = Backends.Enter(backend))
        using (Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire()))
        {
            for (int round = 0; round < rounds; round++)
            {
                string[] levels = BuildTree(source, outsideDirectory);
                Directory.CreateDirectory(destination);
                SwappingAttacker attacker = new(levels);
                string planted = Path.Join(destination, "level0");

                using (ThreadAdversary adversary = new(() =>
                {
                    attacker.Cycle();
                    try
                    {
                        Directory.CreateSymbolicLink(planted, outsideDirectory);
                    }
                    finally
                    {
                        if (new FileInfo(planted).LinkTarget is not null)
                        {
                            File.Delete(planted);
                        }
                    }
                }))
                {
                    try
                    {
                        using Dir from = root.OpenDir("source");
                        using Dir to = root.OpenDir("destination");
                        from.CopyTo(to, new CopyOptions { Symlinks = actions[round % actions.Length] });
                        tally.Record(Outcome.Consistent);
                    }
                    catch (Exception e)
                    {
                        tally.Refused(e);
                    }

                    tally.AdversaryCycles = (tally.AdversaryCycles ?? 0) + adversary.Stop();
                }

                Assert.True(
                    outsideBefore == arena.SnapshotOutside(),
                    $"{context}: round {round} changed the directory outside the sandbox. {tally}");

                string? carried = FindOutsideContent(destination);
                Assert.True(
                    carried is null,
                    $"{context}: round {round} copied content from outside the sandbox into '{carried}'. {tally}");

                HostOps.RemoveWithoutFollowing(source);
                HostOps.RemoveWithoutFollowing(destination);
            }

            scope.AssertItRan();
        }

        StressReport.Publish(output, "tree copied under attack", backend, tally);
        descriptors.AssertNoneLeaked(context);
        tally.RequireContest(context, Outcome.Consistent, Outcome.RefusedAsEscape, Outcome.OtherRefusal, Outcome.Missing);
    }

    /// <summary>
    /// Gives the directory outside enough in it that removing or reading any of it would show:
    /// several files and a nested directory, all holding the content that marks them.
    /// </summary>
    private static void PopulateOutside(StressArena arena)
    {
        string directory = arena.Outside(StressArena.DirectoryName);
        for (int i = 0; i < 4; i++)
        {
            File.WriteAllText(Path.Join(directory, $"canary{i}"), StressArena.OutsideContent);
        }

        Directory.CreateDirectory(Path.Join(directory, "nested"));
        File.WriteAllText(Path.Join(directory, "nested", "canary"), StressArena.OutsideContent);
    }

    /// <summary>
    /// Builds a chain of directories, each holding files and the next, with a link beside each
    /// pointing at the directory outside.
    /// </summary>
    /// <returns>The directories, outermost first.</returns>
    private static string[] BuildTree(string top, string outside)
    {
        string[] levels = new string[Depth];
        string current = top;
        Directory.CreateDirectory(top);
        for (int level = 0; level < Depth; level++)
        {
            current = Path.Join(current, $"level{level}");
            Directory.CreateDirectory(current);
            Directory.CreateSymbolicLink(current + ".link", outside);
            for (int file = 0; file < 3; file++)
            {
                File.WriteAllText(Path.Join(current, $"file{file}"), "inside");
            }

            levels[level] = current;
        }

        return levels;
    }

    /// <summary>
    /// The first file beneath a directory holding the content that marks the directory outside,
    /// found without following a link.
    /// </summary>
    private static string? FindOutsideContent(string directory)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            FileInfo info = new(path);
            if (info.LinkTarget is not null)
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                if (FindOutsideContent(path) is { } found)
                {
                    return found;
                }
            }
            else if (File.ReadAllText(path, Encoding.UTF8) == StressArena.OutsideContent)
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Exchanges each directory in a chain with the link beside it, and back again.
    /// </summary>
    /// <remarks>
    /// The attacker keeps track of which levels it has swapped, because the path to a level
    /// deeper down runs through whatever is at the level above: with a level swapped, the real
    /// directory is under the link's name, and a path through the original name would lead
    /// through the link to the directory outside. An attacker that went there would be
    /// changing outside itself, and the check afterwards could not tell who did it.
    /// </remarks>
    private sealed class SwappingAttacker(string[] levels)
    {
        private readonly bool[] _swapped = new bool[levels.Length];

        public void Cycle()
        {
            for (int level = 0; level < levels.Length; level++)
            {
                string directory = Current(level);
                string link = directory + ".link";

                // After a swap the directory is at the link's name and the link at the
                // directory's, so the same exchange undoes it.
                HostOps.Exchange(directory, link);
                _swapped[level] = !_swapped[level];
            }
        }

        /// <summary>Where a level's name is now, given which levels above it are swapped.</summary>
        private string Current(int level)
        {
            string path = Path.GetDirectoryName(levels[0])!;
            for (int above = 0; above <= level; above++)
            {
                string name = Path.GetFileName(levels[above]);
                path = Path.Join(path, above < level && _swapped[above] ? name + ".link" : name);
            }

            return path;
        }
    }
}
