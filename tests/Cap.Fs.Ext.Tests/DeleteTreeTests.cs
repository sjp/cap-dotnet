using Cap.Std;

namespace Cap.Fs.Ext.Tests;

/// <summary>
/// Removing a directory and everything inside it.
/// </summary>
/// <remarks>
/// <para>
/// The operation most often found to be wrong in libraries that do this, and the failure is
/// never visible in a test about ordinary trees: a removal that joins each name onto a path
/// deletes the right files every time until something replaces a directory in the middle with
/// a link, at which point it deletes somebody else's. So the trees here are not ordinary. They
/// hold links aimed out of the sandbox, links in place of directories, and objects that are
/// neither, and what is asserted is as much about what survives outside the tree as about what
/// is gone inside it.
/// </para>
/// <para>
/// A removal racing a link planted while it runs belongs to the concurrency suite rather than
/// here; what these cover is a link that is already there when the removal starts, which is
/// the case an attacker can arrange without winning any race at all.
/// </para>
/// </remarks>
public sealed class DeleteTreeTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>A nested tree goes, and so does the directory it was under.</summary>
    [Fact]
    public void A_nested_tree_is_removed_entirely()
    {
        Make("doomed", "a", "b", "leaf.txt");
        Make("doomed", "top.txt");
        Make("kept.txt");

        _tree.Directory.DeleteTree("doomed");

        Assert.False(HostDirectory.Exists(Path.Combine(_tree.HostPath, "doomed")));
        Assert.True(HostFile.Exists(Path.Combine(_tree.HostPath, "kept.txt")));
    }

    /// <summary>A tree named by a path of several components is removed.</summary>
    /// <remarks>
    /// Everything ahead of the last component is resolved once, and the removal then works
    /// against the directory that resolution reached — so a path is accepted without the
    /// removal ever being aimed by one.
    /// </remarks>
    [Fact]
    public void A_tree_named_by_a_longer_path_is_removed()
    {
        Make("a", "b", "doomed", "leaf.txt");

        _tree.Directory.DeleteTree(Path.Combine("a", "b", "doomed"));

        Assert.False(HostDirectory.Exists(Path.Combine(_tree.HostPath, "a", "b", "doomed")));
        Assert.True(HostDirectory.Exists(Path.Combine(_tree.HostPath, "a", "b")));
    }

    /// <summary>
    /// A link inside the tree is unlinked, and what it points at is left alone.
    /// </summary>
    /// <remarks>
    /// The property the whole design is for. The link names a directory outside the sandbox
    /// entirely; a removal that followed it would empty that directory using the caller's own
    /// privileges, and the tree being removed would look exactly the same afterwards either
    /// way.
    /// </remarks>
    [Fact]
    public void A_link_inside_the_tree_is_removed_without_following_it()
    {
        Make("elsewhere", "precious.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "doomed"));
        HostDirectory.CreateSymbolicLink(
            Path.Combine(_tree.HostPath, "doomed", "escape"), Path.Combine("..", "elsewhere"));

        _tree.Directory.DeleteTree("doomed");

        Assert.False(HostDirectory.Exists(Path.Combine(_tree.HostPath, "doomed")));
        Assert.True(HostFile.Exists(Path.Combine(_tree.HostPath, "elsewhere", "precious.txt")));
    }

    /// <summary>A name holding a link is not removed as a tree.</summary>
    /// <remarks>
    /// Removing a tree is a request about a directory. A link pointing at one is not a
    /// directory, and treating it as one would let a link planted at the name redirect the
    /// removal to whatever it pointed at.
    /// </remarks>
    [Fact]
    public void A_link_at_the_named_place_is_refused()
    {
        Make("elsewhere", "precious.txt");
        HostDirectory.CreateSymbolicLink(Path.Combine(_tree.HostPath, "doomed"), "elsewhere");

        Assert.ThrowsAny<IOException>(() => _tree.Directory.DeleteTree("doomed"));

        Assert.True(HostFile.Exists(Path.Combine(_tree.HostPath, "elsewhere", "precious.txt")));
        Assert.True(HostEntry.Exists(Path.Combine(_tree.HostPath, "doomed")));
    }

    /// <summary>A name holding a file is not removed as a tree.</summary>
    [Fact]
    public void A_file_at_the_named_place_is_refused()
    {
        Make("doomed");

        Assert.ThrowsAny<IOException>(() => _tree.Directory.DeleteTree("doomed"));

        Assert.True(HostFile.Exists(Path.Combine(_tree.HostPath, "doomed")));
    }

    /// <summary>A name holding nothing is reported as holding nothing.</summary>
    [Fact]
    public void A_missing_tree_is_reported()
    {
        Assert.Throws<DirectoryNotFoundException>(() => _tree.Directory.DeleteTree("absent"));
    }

    /// <summary>The reporting form answers false rather than building an exception.</summary>
    [Fact]
    public void The_reporting_form_answers_false_for_a_missing_tree()
    {
        Assert.False(_tree.Directory.TryDeleteTree("absent"));

        Make("present", "leaf.txt");
        Assert.True(_tree.Directory.TryDeleteTree("present"));
    }

    /// <summary>A path that climbs out of the handle's authority is refused.</summary>
    [Fact]
    public void A_path_that_climbs_out_is_refused()
    {
        Assert.Throws<SandboxEscapeException>(
            () => _tree.Directory.DeleteTree(Path.Combine("..", "elsewhere")));
    }

    /// <summary>A path that climbs and descends again, staying inside, names the tree it reaches.</summary>
    [Fact]
    public void A_path_that_climbs_and_stays_inside_removes_the_tree_it_names()
    {
        Make("a", "doomed", "leaf.txt");
        Make("a", "b", "kept.txt");

        _tree.Directory.DeleteTree("a/b/../doomed");

        Assert.False(HostDirectory.Exists(Path.Combine(_tree.HostPath, "a", "doomed")));
        Assert.True(HostFile.Exists(Path.Combine(_tree.HostPath, "a", "b", "kept.txt")));
    }

    /// <summary>
    /// A path ending in <c>..</c> is refused, and the directory it names is left whole.
    /// </summary>
    /// <remarks>
    /// <c>a/..</c> names the handle's own directory, by where it sits rather than by a name in
    /// its parent. Removing it would empty the whole tree the caller holds on the strength of
    /// a path that never spelled that out, so there is no name here for a removal to act on.
    /// </remarks>
    [Fact]
    public void A_path_ending_in_a_climb_removes_nothing()
    {
        Make("a", "b", "leaf.txt");

        CapIOException thrown = Assert.ThrowsAny<CapIOException>(() => _tree.Directory.DeleteTree("a/b/.."));
        Assert.Equal(CapErrorKind.InvalidArgument, thrown.Kind);
        Assert.False(_tree.Directory.TryDeleteTree("a/.."));
        Assert.Throws<SandboxEscapeException>(() => _tree.Directory.TryDeleteTree("a/../.."));

        Assert.True(HostFile.Exists(Path.Combine(_tree.HostPath, "a", "b", "leaf.txt")));
    }

    /// <summary>Emptying a directory leaves the directory.</summary>
    /// <remarks>
    /// The form a caller holding the root of a sandbox needs: there is no handle above that
    /// root for a name to be used against, and there is not meant to be one.
    /// </remarks>
    [Fact]
    public void Emptying_a_directory_leaves_the_directory_itself()
    {
        Make("a", "b", "leaf.txt");
        Make("top.txt");

        _tree.Directory.DeleteTreeContents();

        Assert.True(HostDirectory.Exists(_tree.HostPath));
        Assert.Empty(HostDirectory.GetFileSystemEntries(_tree.HostPath));
    }

    /// <summary>Creates a file, and whatever directories it needs, under the scratch tree.</summary>
    private void Make(params string[] parts)
    {
        string path = Path.Combine([_tree.HostPath, .. parts]);
        HostDirectory.CreateDirectory(Path.GetDirectoryName(path)!);
        HostFile.WriteAllText(path, "contents");
    }
}
