using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Std;
using Cap.Tests.Fakes;

namespace Cap.Fs.Ext.Tests;

/// <summary>
/// Walking a simulated tree whose directory reads say less than a real one's usually do.
/// </summary>
/// <remarks>
/// <para>
/// A walk decides whether to descend from the kind the directory read reported, and some
/// filesystems report no kind at all. When even the lookup that covers for them cannot say
/// what an entry is, the walk has to try opening it — and if the entry is a link, that open is
/// the only thing standing between a walk told not to follow links and the directory the link
/// names. No filesystem the tests can rely on produces such an entry on demand, so the
/// simulation does.
/// </para>
/// <para>
/// The simulated platform replaces the real one for the whole process, so these tests run on
/// their own rather than beside the ones walking real trees.
/// </para>
/// </remarks>
[Collection(Name)]
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WalkSimulationTests
{
    /// <summary>The collection that keeps the simulated platform away from every other test.</summary>
    public const string Name = "simulated platform";

    /// <summary>The three searches that share the walk's descent.</summary>
    public enum Search
    {
        Walk,
        WalkAsync,
        Glob,
    }

    /// <summary>
    /// A link the directory read could not classify, pointing at a directory in the same tree,
    /// is reported and not entered when links are not followed.
    /// </summary>
    [Theory]
    [InlineData(Search.Walk)]
    [InlineData(Search.WalkAsync)]
    [InlineData(Search.Glob)]
    public void An_unclassified_link_is_not_entered(Search search)
    {
        FakeFileSystem fs = TreeWithAnUnclassifiedLink();
        using (PlatformOps.Substitute(new FakePlatformOps(fs)))
        {
            using Dir root = Dir.Open("/tree", AmbientAuthority.Acquire());
            Assert.Equal(SymlinkPolicy.FollowWithinSandbox, root.SymlinkPolicy);

            List<(string Name, CapFileType Type)> seen = Run(root, search, WalkOptions.Default);

            Assert.Contains(("link", CapFileType.Unknown), seen);
            Assert.Equal(1, seen.Count(e => e.Name == "inside.txt"));
        }
    }

    /// <summary>
    /// The same link is entered when links are followed, which is what makes the test above
    /// about the option rather than about a tree the walk could never have entered.
    /// </summary>
    [Theory]
    [InlineData(Search.Walk)]
    [InlineData(Search.WalkAsync)]
    [InlineData(Search.Glob)]
    public void An_unclassified_link_is_entered_when_links_are_followed(Search search)
    {
        FakeFileSystem fs = TreeWithAnUnclassifiedLink();
        using (PlatformOps.Substitute(new FakePlatformOps(fs)))
        {
            using Dir root = Dir.Open("/tree", AmbientAuthority.Acquire());

            List<(string Name, CapFileType Type)> seen = Run(root, search, new WalkOptions { FollowSymlinks = true });

            Assert.Equal(2, seen.Count(e => e.Name == "inside.txt"));
        }
    }

    /// <summary>
    /// A directory holding a file, and beside it a link to that directory whose kind neither
    /// the directory read nor the lookup after it will report.
    /// </summary>
    private static FakeFileSystem TreeWithAnUnclassifiedLink()
    {
        FakeFileSystem fs = new();
        _ = fs.AddFile("/tree/real/inside.txt");
        FakeNode link = fs.AddSymbolicLink("/tree/link", "real");
        link.HidesKindFromDirectoryRead = true;
        link.EntryType = CapFileType.Unknown;
        return fs;
    }

    private static List<(string Name, CapFileType Type)> Run(Dir root, Search search, WalkOptions options)
    {
        IEnumerable<WalkEntry> entries = search switch
        {
            Search.Walk => root.Walk(options),
            Search.WalkAsync => root.WalkAsync(options).ToBlockingEnumerable(),
            _ => root.Glob("**/*", options),
        };

        return [.. entries.Select(e => (e.Name, e.Type))];
    }
}
