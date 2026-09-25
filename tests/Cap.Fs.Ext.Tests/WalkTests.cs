using Cap.Primitives;
using Cap.Std;

namespace Cap.Fs.Ext.Tests;

/// <summary>
/// Walking a tree by descending through handles.
/// </summary>
/// <remarks>
/// <para>
/// Three properties carry most of the weight here, and none of them is "the walk finds the
/// files". A walk that returned the right names while quietly following a link out of the
/// tree, or while holding a handle open per level of a tree built to be deep, would pass any
/// test written about its results.
/// </para>
/// <para>
/// So what is asserted is where it refuses to go — through a link, unless asked; back into a
/// directory it is already inside; below the depth it was given — and that the handles it
/// hands out belong to it. The trees are built with ambient <c>System.IO</c>, because a test
/// that arranged them through the capability API would be arranging only the shapes that API
/// already agreed to.
/// </para>
/// </remarks>
public sealed class WalkTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>Everything in the tree is reached, each with the depth it sits at.</summary>
    [Fact]
    public void Every_entry_is_reached_with_its_depth()
    {
        Make("top.txt");
        Make("a", "one.txt");
        Make("a", "b", "two.txt");

        Dictionary<string, int> found = _tree.Directory.Walk().ToDictionary(e => e.Name, e => e.Depth);

        Assert.Equal(
            new Dictionary<string, int>
            {
                ["top.txt"] = 1,
                ["a"] = 1,
                ["one.txt"] = 2,
                ["b"] = 2,
                ["two.txt"] = 3,
            },
            found);
    }

    /// <summary>A directory is reported before the things inside it.</summary>
    /// <remarks>
    /// Parents first is what makes a walk usable for building something as it goes — a copy
    /// cannot create a file before the directory holding it — so the order is part of what is
    /// promised rather than an accident of the implementation.
    /// </remarks>
    [Fact]
    public void A_directory_is_reported_before_its_contents()
    {
        Make("a", "b", "deep.txt");

        List<string> order = [.. _tree.Directory.Walk().Select(e => e.Name)];

        Assert.True(order.IndexOf("a") < order.IndexOf("b"));
        Assert.True(order.IndexOf("b") < order.IndexOf("deep.txt"));
    }

    /// <summary>An entry carries a name and a handle, and no path.</summary>
    /// <remarks>
    /// The property the whole type exists for. Asserted about the surface rather than about a
    /// result, because a member that answered "where is this?" would be used, and the string
    /// it produced would be resolved by something with the process's own privileges.
    /// </remarks>
    [Fact]
    public void An_entry_offers_no_path()
    {
        string[] members = [.. typeof(WalkEntry).GetProperties().Select(p => p.Name)];

        Assert.DoesNotContain("Path", members);
        Assert.DoesNotContain("FullName", members);
        Assert.Contains("Name", members);
        Assert.Contains("Directory", members);
    }

    /// <summary>A link is reported as a link and is not descended into.</summary>
    [Fact]
    public void A_link_is_not_followed_by_default()
    {
        Make("outside", "secret.txt");
        Link("inside", "outside");

        List<WalkEntry> entries = [.. _tree.Directory.Walk().Where(e => e.Name == "inside")];

        Assert.Single(entries);
        Assert.Equal(CapFileType.Symlink, entries[0].Type);
        Assert.Equal(1, _tree.Directory.Walk().Count(e => e.Name == "secret.txt"));
    }

    /// <summary>Asked to follow links, the walk goes through one.</summary>
    [Fact]
    public void A_link_is_followed_when_asked()
    {
        Make("outside", "secret.txt");
        Link("inside", "outside");

        WalkOptions options = new() { FollowSymlinks = true };
        int seen = _tree.Directory.Walk(options).Count(e => e.Name == "secret.txt");

        // Once under the real directory and once through the link, because the link is a
        // second way to reach the same file rather than a copy of it.
        Assert.Equal(2, seen);
    }

    /// <summary>
    /// The directories a walk enters without following links carry the starting handle's
    /// policy, not the stricter one their opens were resolved under.
    /// </summary>
    /// <remarks>
    /// Refusing links on the way down is the walk's own promise. The handles it hands out are
    /// the caller's to act through, and one that refused links only because it happened to be
    /// deep in the tree would make the same call succeed or fail depending on where the entry
    /// was found.
    /// </remarks>
    [Fact]
    public void The_directories_entered_keep_the_starting_handles_policy()
    {
        Make("a", "b", "deep.txt");

        List<SymlinkPolicy> policies = [.. _tree.Directory.Walk().Select(e => e.Directory.SymlinkPolicy)];

        Assert.Equal(SymlinkPolicy.FollowWithinSandbox, _tree.Directory.SymlinkPolicy);
        Assert.All(policies, policy => Assert.Equal(SymlinkPolicy.FollowWithinSandbox, policy));
    }

    /// <summary>A link that makes a cycle stops the walk going round it.</summary>
    /// <remarks>
    /// A link pointing at a directory above it turns a finite filesystem into an infinite
    /// tree, and the only way to notice is to remember what has already been entered. Without
    /// the check this test does not fail — it never finishes.
    /// </remarks>
    [Fact]
    public void A_cycle_of_links_does_not_make_the_walk_endless()
    {
        Make("a", "b", "leaf.txt");
        Link(Path.Combine("a", "b", "back"), Path.Combine("..", ".."));

        WalkOptions options = new() { FollowSymlinks = true, MaxDepth = 64 };
        List<WalkEntry> entries = [.. _tree.Directory.Walk(options)];

        Assert.Contains(entries, e => e.Name == "back");
        Assert.Contains(entries, e => e.Name == "leaf.txt");

        // Each name appears a bounded number of times: the cycle was noticed rather than
        // followed until the depth limit turned it into a failure.
        Assert.True(entries.Count < 32, $"The walk produced {entries.Count} entries from a tree of four.");
    }

    /// <summary>A tree deeper than the limit stops the walk rather than being cut short.</summary>
    [Fact]
    public void A_tree_deeper_than_the_limit_is_refused()
    {
        string path = _tree.HostPath;
        for (int i = 0; i < 12; i++)
        {
            path = Path.Combine(path, "level");
            HostDirectory.CreateDirectory(path);
        }

        WalkOptions options = new() { MaxDepth = 4 };

        Assert.Throws<CapIOException>(() => _tree.Directory.Walk(options).ToList());
    }

    /// <summary>
    /// A tree far deeper than the limit is refused rather than exhausting anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The limit is what does the work: the walk stops at it rather than descending until it
    /// runs out of handles, and it stops the same way whether the tree is one level past the
    /// limit or a thousand. Two thousand is as deep as the set-up can build here, because the
    /// path it would have to name grows by a level each time and the platform will not accept
    /// one much longer — which is itself a reason the walk cannot rely on paths to notice how
    /// deep it has gone.
    /// </para>
    /// <para>
    /// The tree is taken apart from the bottom up afterwards. It is deeper than anything the
    /// library will remove for the same reason it is deeper than anything the library will
    /// walk, so leaving it for the usual cleanup would leave it on the disk.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_tree_far_deeper_than_the_limit_is_refused()
    {
        List<string> levels = [];
        string path = _tree.HostPath;
        for (int i = 0; i < 2000; i++)
        {
            path = Path.Combine(path, "d");
            HostDirectory.CreateDirectory(path);
            levels.Add(path);
        }

        try
        {
            Assert.Throws<CapIOException>(() => _tree.Directory.Walk().ToList());
        }
        finally
        {
            for (int i = levels.Count - 1; i >= 0; i--)
            {
                HostDirectory.Delete(levels[i]);
            }
        }
    }

    /// <summary>Hidden names are left out, and hidden directories are not entered.</summary>
    [Fact]
    public void Hidden_entries_are_left_out_when_asked()
    {
        Make(".hidden", "buried.txt");
        Make("plain.txt");

        WalkOptions options = new() { SkipHidden = true };
        List<string> names = [.. _tree.Directory.Walk(options).Select(e => e.Name)];

        Assert.Equal(["plain.txt"], names);
    }

    /// <summary>An entry's handle is closed once the walk has left the directory.</summary>
    /// <remarks>
    /// The rule the type documents, asserted rather than assumed. A caller who collects
    /// entries and uses them later is relying on something this walk deliberately does not
    /// offer, and finding out by exception is better than finding out by acting on a handle
    /// that has been reused.
    /// </remarks>
    [Fact]
    public void An_entry_kept_past_its_turn_no_longer_opens_anything()
    {
        Make("a", "one.txt");

        List<WalkEntry> collected = [.. _tree.Directory.Walk()];
        WalkEntry nested = collected.Single(e => e.Name == "one.txt");

        Assert.Throws<ObjectDisposedException>(() => nested.GetMetadata());
    }

    /// <summary>The walk reads through the handle, so a restricted handle restricts it.</summary>
    /// <remarks>
    /// Asking for links to be followed cannot widen what the handle was granted. A component
    /// handed a handle that refuses links keeps refusing them, whatever options the code
    /// holding it passes.
    /// </remarks>
    [Fact]
    public void Following_links_cannot_widen_what_the_handle_grants()
    {
        Make("outside", "secret.txt");
        Link("inside", "outside");

        using Dir strict = _tree.Directory.Restrict(SymlinkPolicy.Deny);
        WalkOptions options = new() { FollowSymlinks = true };

        Assert.Equal(1, strict.Walk(options).Count(e => e.Name == "secret.txt"));
    }

    /// <summary>The asynchronous walk reaches the same entries.</summary>
    [Fact]
    public async Task The_asynchronous_walk_reaches_the_same_entries()
    {
        Make("top.txt");
        Make("a", "b", "two.txt");

        List<string> synchronous = [.. _tree.Directory.Walk().Select(e => e.Name).Order()];

        List<string> asynchronous = [];
        await foreach (WalkEntry entry in _tree.Directory.WalkAsync(
            cancellationToken: TestContext.Current.CancellationToken))
        {
            asynchronous.Add(entry.Name);
        }

        Assert.Equal(synchronous, asynchronous.Order());
    }

    /// <summary>A walk with no depth left to give is refused before it starts.</summary>
    [Fact]
    public void A_depth_below_one_is_refused()
    {
        WalkOptions options = new() { MaxDepth = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => _tree.Directory.Walk(options));
    }

    /// <summary>Creates a file, and whatever directories it needs, under the scratch tree.</summary>
    private void Make(params string[] parts)
    {
        string path = Path.Combine([_tree.HostPath, .. parts]);
        HostDirectory.CreateDirectory(Path.GetDirectoryName(path)!);
        HostFile.WriteAllText(path, "contents");
    }

    /// <summary>Creates a symbolic link under the scratch tree.</summary>
    private void Link(string name, string target) =>
        HostDirectory.CreateSymbolicLink(Path.Combine(_tree.HostPath, name), target);
}
