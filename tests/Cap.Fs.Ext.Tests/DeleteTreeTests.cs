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

    /// <summary>
    /// A directory this process may not read is still removed when it is empty, at the top of
    /// the removal and inside it.
    /// </summary>
    /// <remarks>
    /// Removing an empty directory asks nothing of the directory, only of the one holding it,
    /// which is why <c>rm -rf</c> removes one. Not being able to open it is a reason not to
    /// look inside, not a reason to leave it.
    /// </remarks>
    [Fact]
    public void An_unreadable_empty_directory_is_removed()
    {
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "sealed"));
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "tree", "sealed"));
        Make("tree", "g.txt");

        using (Seal("sealed"))
        using (Seal(Path.Combine("tree", "sealed")))
        {
            _tree.Directory.DeleteTree("sealed", TestContext.Current.CancellationToken);
            _tree.Directory.DeleteTree("tree", TestContext.Current.CancellationToken);
        }

        Assert.Empty(HostDirectory.GetFileSystemEntries(_tree.HostPath));
    }

    /// <summary>
    /// A directory inside the tree this process may not read, holding something, stops it
    /// going; the failure is the refusal to open it, named by where it is, and the rest of the
    /// tree is removed.
    /// </summary>
    /// <remarks>
    /// What the removal goes on to meet is "not empty", on the directory and then on each one
    /// above it, and reporting that blamed the tree for a concurrent writer that was never
    /// there. The refusal is the cause, and its place in the tree is what the caller needs to
    /// go and fix it.
    /// </remarks>
    [Fact]
    public void An_unreadable_subdirectory_is_reported_as_permission_denied_after_the_rest_is_removed()
    {
        Make("tree", "g.txt");
        Make("tree", "inner", "sealed", "f.txt");
        Make("tree", "inner", "h.txt");

        using (Seal(Path.Combine("tree", "inner", "sealed")))
        {
            UnauthorizedAccessException named = Assert.Throws<UnauthorizedAccessException>(
                () => _tree.Directory.DeleteTree("tree", TestContext.Current.CancellationToken));
            Assert.Contains($"'{Path.Combine("tree", "inner", "sealed")}'", named.Message);

            using Dir opened = _tree.Directory.OpenDir("tree");
            UnauthorizedAccessException emptying = Assert.Throws<UnauthorizedAccessException>(
                () => opened.DeleteTreeContents(TestContext.Current.CancellationToken));
            Assert.Contains($"'{Path.Combine("inner", "sealed")}'", emptying.Message);

            Assert.False(_tree.Directory.TryDeleteTree("tree", TestContext.Current.CancellationToken));
        }

        string tree = Path.Combine(_tree.HostPath, "tree");
        Assert.Equal(["inner"], HostDirectory.GetFileSystemEntries(tree).Select(Path.GetFileName));
        Assert.Equal(["sealed"], HostDirectory.GetFileSystemEntries(Path.Combine(tree, "inner")).Select(Path.GetFileName));
        Assert.True(HostFile.Exists(Path.Combine(tree, "inner", "sealed", "f.txt")));
    }

    /// <summary>
    /// Takes every permission off a directory in the scratch tree until disposed, or skips the
    /// test where that would not stop this process reading it.
    /// </summary>
    private Sealed Seal(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Making a directory unreadable here needs a security descriptor this test does not build.");
        }

        if (HostTree.InMemory)
        {
            Assert.Skip("The filesystem held in memory does not act on mode bits; InterfaceHandleTests covers it with SetUnreadable.");
        }

        if (Environment.IsPrivilegedProcess)
        {
            Assert.Skip("A privileged process reads a directory whatever its mode says.");
        }

        string path = Path.Combine(_tree.HostPath, name);
        HostFile.SetUnixFileMode(path, UnixFileMode.None);
        return new Sealed(path);
    }

    /// <summary>Gives a sealed directory its permissions back, if it is still there, so the scratch tree can be removed.</summary>
    private readonly struct Sealed(string path) : IDisposable
    {
        public void Dispose()
        {
            if (HostDirectory.Exists(path))
            {
                HostFile.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    /// <summary>Creates a file, and whatever directories it needs, under the scratch tree.</summary>
    private void Make(params string[] parts)
    {
        string path = Path.Combine([_tree.HostPath, .. parts]);
        HostDirectory.CreateDirectory(Path.GetDirectoryName(path)!);
        HostFile.WriteAllText(path, "contents");
    }
}
