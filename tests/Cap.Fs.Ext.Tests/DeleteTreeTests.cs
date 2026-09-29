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

        _tree.Directory.DeleteTree("doomed", TestContext.Current.CancellationToken);

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

        _tree.Directory.DeleteTree(Path.Combine("a", "b", "doomed"), TestContext.Current.CancellationToken);

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

        _tree.Directory.DeleteTree("doomed", TestContext.Current.CancellationToken);

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

        Assert.ThrowsAny<IOException>(() => _tree.Directory.DeleteTree("doomed", TestContext.Current.CancellationToken));

        Assert.True(HostFile.Exists(Path.Combine(_tree.HostPath, "elsewhere", "precious.txt")));
        Assert.True(HostEntry.Exists(Path.Combine(_tree.HostPath, "doomed")));
    }

    /// <summary>A name holding a file is not removed as a tree.</summary>
    [Fact]
    public void A_file_at_the_named_place_is_refused()
    {
        Make("doomed");

        Assert.ThrowsAny<IOException>(() => _tree.Directory.DeleteTree("doomed", TestContext.Current.CancellationToken));

        Assert.True(HostFile.Exists(Path.Combine(_tree.HostPath, "doomed")));
    }

    /// <summary>A name holding nothing is reported as holding nothing.</summary>
    [Fact]
    public void A_missing_tree_is_reported()
    {
        Assert.Throws<DirectoryNotFoundException>(() => _tree.Directory.DeleteTree("absent", TestContext.Current.CancellationToken));
    }

    /// <summary>The reporting form answers false rather than building an exception.</summary>
    [Fact]
    public void The_reporting_form_answers_false_for_a_missing_tree()
    {
        Assert.False(_tree.Directory.TryDeleteTree("absent", TestContext.Current.CancellationToken));

        Make("present", "leaf.txt");
        Assert.True(_tree.Directory.TryDeleteTree("present", TestContext.Current.CancellationToken));
    }

    /// <summary>A path that climbs out of the handle's authority is refused.</summary>
    [Fact]
    public void A_path_that_climbs_out_is_refused()
    {
        Assert.Throws<SandboxEscapeException>(
            () => _tree.Directory.DeleteTree(Path.Combine("..", "elsewhere"), TestContext.Current.CancellationToken));
    }

    /// <summary>A path that climbs and descends again, staying inside, names the tree it reaches.</summary>
    [Fact]
    public void A_path_that_climbs_and_stays_inside_removes_the_tree_it_names()
    {
        Make("a", "doomed", "leaf.txt");
        Make("a", "b", "kept.txt");

        _tree.Directory.DeleteTree("a/b/../doomed", TestContext.Current.CancellationToken);

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

        CapIOException thrown = Assert.ThrowsAny<CapIOException>(() => _tree.Directory.DeleteTree("a/b/..", TestContext.Current.CancellationToken));
        Assert.Equal(CapErrorKind.InvalidArgument, thrown.Kind);
        Assert.False(_tree.Directory.TryDeleteTree("a/..", TestContext.Current.CancellationToken));
        Assert.Throws<SandboxEscapeException>(() => _tree.Directory.TryDeleteTree("a/../..", TestContext.Current.CancellationToken));

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

        _tree.Directory.DeleteTreeContents(TestContext.Current.CancellationToken);

        Assert.True(HostDirectory.Exists(_tree.HostPath));
        Assert.Empty(HostDirectory.GetFileSystemEntries(_tree.HostPath));
    }

    /// <summary>The asynchronous removal removes a tree as the synchronous one does.</summary>
    [Fact]
    public async Task An_asynchronous_removal_removes_the_tree()
    {
        Make("doomed", "a", "b", "leaf.txt");
        Make("doomed", "top.txt");
        Make("kept.txt");

        await _tree.Directory.DeleteTreeAsync("doomed", TestContext.Current.CancellationToken);

        Assert.False(HostDirectory.Exists(Path.Combine(_tree.HostPath, "doomed")));
        Assert.True(HostFile.Exists(Path.Combine(_tree.HostPath, "kept.txt")));
    }

    /// <summary>The asynchronous reporting form answers as the synchronous one does.</summary>
    [Fact]
    public async Task The_asynchronous_reporting_form_answers_whether_the_tree_went()
    {
        Make("doomed", "leaf.txt");

        Assert.True(await _tree.Directory.TryDeleteTreeAsync("doomed", TestContext.Current.CancellationToken));
        Assert.False(await _tree.Directory.TryDeleteTreeAsync("doomed", TestContext.Current.CancellationToken));
        Assert.False(HostDirectory.Exists(Path.Combine(_tree.HostPath, "doomed")));
    }

    /// <summary>Emptying a directory asynchronously leaves the directory.</summary>
    [Fact]
    public async Task Emptying_a_directory_asynchronously_leaves_the_directory_itself()
    {
        Make("a", "b", "leaf.txt");
        Make("top.txt");

        await _tree.Directory.DeleteTreeContentsAsync(TestContext.Current.CancellationToken);

        Assert.True(HostDirectory.Exists(_tree.HostPath));
        Assert.Empty(HostDirectory.GetFileSystemEntries(_tree.HostPath));
    }

    /// <summary>
    /// Every form, asked to stop before it starts, throws and removes nothing — the form that
    /// answers false for a failure included, since being told to stop is not a failure.
    /// </summary>
    [Theory]
    [InlineData("DeleteTree")]
    [InlineData("TryDeleteTree")]
    [InlineData("DeleteTreeContents")]
    [InlineData("DeleteTreeAsync")]
    [InlineData("TryDeleteTreeAsync")]
    [InlineData("DeleteTreeContentsAsync")]
    public async Task A_removal_already_cancelled_removes_nothing(string form)
    {
        Make("doomed", "a", "leaf.txt");
        Make("doomed", "top.txt");
        CancellationToken cancelled = new(canceled: true);
        Dir dir = _tree.Directory;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(form switch
        {
            "DeleteTree" => () => Task.Run(() => dir.DeleteTree("doomed", cancelled), TestContext.Current.CancellationToken),
            "TryDeleteTree" => () => Task.Run(() => dir.TryDeleteTree("doomed", cancelled), TestContext.Current.CancellationToken),
            "DeleteTreeContents" => () => Task.Run(() => dir.DeleteTreeContents(cancelled), TestContext.Current.CancellationToken),
            "DeleteTreeAsync" => () => dir.DeleteTreeAsync("doomed", cancelled),
            "TryDeleteTreeAsync" => () => dir.TryDeleteTreeAsync("doomed", cancelled),
            _ => () => dir.DeleteTreeContentsAsync(cancelled),
        });

        Assert.True(HostFile.Exists(Path.Combine(_tree.HostPath, "doomed", "a", "leaf.txt")));
        Assert.True(HostFile.Exists(Path.Combine(_tree.HostPath, "doomed", "top.txt")));
    }

    /// <summary>
    /// A removal stopped part of the way through throws, leaves what it had not reached, and
    /// does not answer false in place of throwing.
    /// </summary>
    /// <remarks>
    /// Through a wrapped handle, whose log shows the first removal as it happens, so the
    /// signal arrives at an exact point rather than at whatever point a timer lands on.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_removal_cancelled_part_way_leaves_the_rest(bool reporting, bool asynchronous)
    {
        for (int i = 0; i < 4; i++)
        {
            Make("doomed", $"file{i}.txt");
        }

        using CancellationTokenSource cancel = new();
        RecordingDir wrapped = new(_tree.Directory, [], ".", call =>
        {
            if (call.Contains("DeleteFile(", StringComparison.Ordinal))
            {
                cancel.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>((reporting, asynchronous) switch
        {
            (false, false) => () => Task.Run(() => wrapped.DeleteTree("doomed", cancel.Token), TestContext.Current.CancellationToken),
            (true, false) => () => Task.Run(() => wrapped.TryDeleteTree("doomed", cancel.Token), TestContext.Current.CancellationToken),
            (false, true) => () => wrapped.DeleteTreeAsync("doomed", cancel.Token),
            (true, true) => () => wrapped.TryDeleteTreeAsync("doomed", cancel.Token),
        });

        string doomed = Path.Combine(_tree.HostPath, "doomed");
        Assert.True(HostDirectory.Exists(doomed));
        Assert.Equal(3, HostDirectory.GetFileSystemEntries(doomed).Length);
    }

    /// <summary>Creates a file, and whatever directories it needs, under the scratch tree.</summary>
    private void Make(params string[] parts)
    {
        string path = Path.Combine([_tree.HostPath, .. parts]);
        HostDirectory.CreateDirectory(Path.GetDirectoryName(path)!);
        HostFile.WriteAllText(path, "contents");
    }
}
