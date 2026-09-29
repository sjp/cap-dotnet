using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Std;
using Cap.Std.Testing;
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
/// Each root is opened through the simulation directly, and the handle carries it from there,
/// so these tests run beside the ones walking real trees without either seeing the other.
/// </para>
/// </remarks>
public sealed class WalkSimulationTests
{
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
        FakePlatformOps ops = new(fs);
        using Dir root = Dir.OpenThrough(ops, "/tree", AmbientAuthority.Acquire());
        Assert.Equal(SymlinkPolicy.FollowWithinSandbox, root.SymlinkPolicy);

        List<(string Name, CapFileType Type)> seen = Run(root, search, WalkOptions.Default);

        Assert.Contains(("link", CapFileType.Unknown), seen);
        Assert.Equal(1, seen.Count(e => e.Name == "inside.txt"));
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
        FakePlatformOps ops = new(fs);
        using Dir root = Dir.OpenThrough(ops, "/tree", AmbientAuthority.Acquire());

        List<(string Name, CapFileType Type)> seen = Run(root, search, new WalkOptions { FollowSymlinks = true });

        Assert.Equal(2, seen.Count(e => e.Name == "inside.txt"));
    }

    /// <summary>
    /// A directory that is there and cannot be opened for want of handles fails the walk,
    /// rather than its contents quietly going missing from the answer.
    /// </summary>
    [Theory]
    [InlineData(Search.Walk)]
    [InlineData(Search.WalkAsync)]
    [InlineData(Search.Glob)]
    public void A_directory_that_cannot_be_opened_for_want_of_handles_fails_the_walk(Search search)
    {
        (FakeFileSystem fs, MemoryNode full) = TreeWithADirectory();
        FakePlatformOps ops = new(fs) { DirectoryOpenFault = OutOfHandlesAt(full) };
        using Dir root = Dir.OpenThrough(ops, "/tree", AmbientAuthority.Acquire());
        int before = ops.OpenHandleCount;

        CapIOException thrown = Assert.ThrowsAny<CapIOException>(() => Run(root, search, WalkOptions.Default));

        Assert.Equal(CapErrorKind.OutOfHandles, thrown.Kind);
        Assert.Equal(before, ops.OpenHandleCount);
    }

    /// <summary>
    /// The same directory is left out, and the rest walked, when the caller's handler says so;
    /// the handler is told which entry it was and why.
    /// </summary>
    [Theory]
    [InlineData(Search.Walk)]
    [InlineData(Search.WalkAsync)]
    [InlineData(Search.Glob)]
    public void A_directory_that_cannot_be_opened_is_left_out_when_the_handler_says_so(Search search)
    {
        (FakeFileSystem fs, MemoryNode full) = TreeWithADirectory();
        _ = fs.AddFile("/tree/open/beside.txt");
        FakePlatformOps ops = new(fs) { DirectoryOpenFault = OutOfHandlesAt(full) };
        using Dir root = Dir.OpenThrough(ops, "/tree", AmbientAuthority.Acquire());
        List<(string Name, CapErrorKind Kind)> reported = [];
        WalkOptions options = new()
        {
            OnError = (entry, exception) =>
            {
                reported.Add((entry.Name, CapIOException.KindOf(exception)));
                return true;
            },
        };

        List<string> seen = [.. Run(root, search, options).Select(e => e.Name)];

        Assert.Equal([("full", CapErrorKind.OutOfHandles)], reported);
        Assert.Contains("full", seen);
        Assert.DoesNotContain("inside.txt", seen);
        Assert.Contains("beside.txt", seen);
    }

    /// <summary>A handler that declines lets the failure through unchanged.</summary>
    [Theory]
    [InlineData(Search.Walk)]
    [InlineData(Search.WalkAsync)]
    [InlineData(Search.Glob)]
    public void A_handler_that_declines_fails_the_walk(Search search)
    {
        (FakeFileSystem fs, MemoryNode full) = TreeWithADirectory();
        FakePlatformOps ops = new(fs) { DirectoryOpenFault = OutOfHandlesAt(full) };
        using Dir root = Dir.OpenThrough(ops, "/tree", AmbientAuthority.Acquire());
        Exception? offered = null;
        WalkOptions options = new()
        {
            OnError = (_, exception) =>
            {
                offered = exception;
                return false;
            },
        };

        CapIOException thrown = Assert.ThrowsAny<CapIOException>(() => Run(root, search, options));

        Assert.Same(offered, thrown);
    }

    /// <summary>
    /// The names that are not directories to enter are still yielded and skipped without the
    /// handler hearing of them: a link not followed, one followed that leads out of the tree,
    /// one that never arrives, a Windows reparse point that redirects.
    /// </summary>
    [Theory]
    [InlineData(Search.Walk, false)]
    [InlineData(Search.Walk, true)]
    [InlineData(Search.WalkAsync, false)]
    [InlineData(Search.WalkAsync, true)]
    [InlineData(Search.Glob, false)]
    [InlineData(Search.Glob, true)]
    public void A_name_that_is_not_a_directory_to_enter_is_still_skipped_silently(Search search, bool followSymlinks)
    {
        FakeFileSystem fs = TreeWithAnUnclassifiedLink();
        _ = fs.AddFile("/elsewhere/outside.txt");
        MemoryNode[] links =
        [
            fs.AddSymbolicLink("/tree/out", "../elsewhere"),
            fs.AddSymbolicLink("/tree/loop", "loop"),
            fs.AddSymbolicLink("/tree/dangling", "nothing"),
            fs.AddOpaqueReparsePoint("/tree/redirect", 0xA000_0123),
        ];
        foreach (MemoryNode link in links)
        {
            link.EntryType = CapFileType.Unknown;
        }

        FakePlatformOps ops = new(fs);
        using Dir root = Dir.OpenThrough(ops, "/tree", AmbientAuthority.Acquire());
        List<string> offered = [];
        WalkOptions options = new()
        {
            FollowSymlinks = followSymlinks,
            OnError = (entry, _) =>
            {
                offered.Add(entry.Name);
                return true;
            },
        };

        List<string> seen = [.. Run(root, search, options).Select(e => e.Name)];

        Assert.Empty(offered);
        Assert.Subset(seen.ToHashSet(), new HashSet<string> { "out", "loop", "dangling", "redirect" });
        Assert.DoesNotContain("outside.txt", seen);
    }

    /// <summary>A file at the top, and a directory holding one beside it.</summary>
    private static (FakeFileSystem FileSystem, MemoryNode Directory) TreeWithADirectory()
    {
        FakeFileSystem fs = new();
        _ = fs.AddFile("/tree/top.txt");
        _ = fs.AddFile("/tree/full/inside.txt");
        return (fs, fs.Find("/tree/full")!);
    }

    private static Func<MemoryNode, CapErrorCategory> OutOfHandlesAt(MemoryNode directory) =>
        node => node == directory ? CapErrorCategory.OutOfHandles : CapErrorCategory.None;

    /// <summary>
    /// A directory holding a file, and beside it a link to that directory whose kind neither
    /// the directory read nor the lookup after it will report.
    /// </summary>
    private static FakeFileSystem TreeWithAnUnclassifiedLink()
    {
        FakeFileSystem fs = new();
        _ = fs.AddFile("/tree/real/inside.txt");
        MemoryNode link = fs.AddSymbolicLink("/tree/link", "real");
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
