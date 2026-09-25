using System.Text;
using Cap.Primitives;
using Cap.Std;
using Cap.Std.Testing;

namespace Cap.Fs.Ext.Tests;

/// <summary>
/// The helpers driven through an <see cref="IDir"/> that is not a <see cref="Dir"/>.
/// </summary>
/// <remarks>
/// <para>
/// Code that takes the interface, so that it can be handed a stub or a wrapper, keeps the
/// convenience layer. Given something other than a <see cref="Dir"/>, each helper works
/// through the interface's own members. These tests hand it a wrapper that forwards to a real
/// handle on an in-memory filesystem and logs every call, and check both the result and the
/// calls the helper made to get there.
/// </para>
/// <para>
/// The property the calls are checked for is the one the helpers hold on a real handle: a
/// tree is worked through one handle per directory, and every name used against a handle is
/// a single component. Nothing is ever named by a path built from the pieces.
/// </para>
/// </remarks>
public sealed class InterfaceHandleTests
{
    private readonly InMemoryFileSystem _fs = new();

    private RecordingDir Root() => new(_fs.OpenRoot());

    private void Tree()
    {
        _fs.AddFile("top.txt", "top");
        _fs.AddFile("a/one.txt", "one");
        _fs.AddFile("a/b/two.txt", "two");
        _fs.AddDirectory("a/empty");
    }

    /// <summary>The name arguments a helper passed, excluding the calls that take none.</summary>
    private static IEnumerable<string> NamesUsed(IEnumerable<string> log) =>
        log.Select(call => call[(call.IndexOf('(') + 1)..^1])
           .SelectMany(arguments => arguments.Split(", "))
           .Where(argument => argument.Length > 0 && argument is not ("True" or "False") && !argument.StartsWith('['));

    private static void AssertEveryNameIsOneComponent(RecordingDir root)
    {
        foreach (string name in NamesUsed(root.Log))
        {
            Assert.DoesNotContain('/', name);
            Assert.DoesNotContain('\\', name);
        }
    }

    [Fact]
    public void A_walk_through_the_interface_finds_what_a_walk_through_the_handle_finds()
    {
        Tree();
        using Dir direct = _fs.OpenRoot();
        using RecordingDir wrapped = Root();

        List<(string, int, CapFileType)> expected = [.. direct.Walk().Select(e => (e.Name, e.Depth, e.Type)).Order()];
        List<(string, int, CapFileType)> actual = [.. wrapped.Walk().Select(e => (e.Name, e.Depth, e.Type)).Order()];

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void A_walk_through_the_interface_descends_by_single_names_refusing_links()
    {
        Tree();
        using RecordingDir root = Root();

        _ = root.Walk().ToList();

        // Each directory is read once, through a handle of its own, and entered from its
        // parent by name with links refused at that name.
        Assert.Equal(
            [".: EnumerateEntries()", "./a: EnumerateEntries()", "./a/b: EnumerateEntries()", "./a/empty: EnumerateEntries()"],
            root.Log.Where(call => call.EndsWith("EnumerateEntries()", StringComparison.Ordinal)).Order());
        Assert.Contains(".: TryOpenDir(a, True)", root.Log);
        Assert.Contains("./a: TryOpenDir(b, True)", root.Log);
        Assert.Contains("./a: TryOpenDir(empty, True)", root.Log);
        AssertEveryNameIsOneComponent(root);
    }

    [Fact]
    public void Each_walk_entry_opens_through_the_directory_it_was_found_in()
    {
        Tree();
        using RecordingDir root = Root();

        foreach (WalkEntry entry in root.Walk())
        {
            Assert.IsType<RecordingDir>(entry.Directory);
            if (entry.Name == "two.txt")
            {
                using ICapFile file = entry.OpenFile();
                Assert.IsType<RecordingFile>(file);
                Assert.Equal(3, file.Length);
            }
        }

        Assert.Contains("./a/b: OpenFile(two.txt, Open)", root.Log);
    }

    [Fact]
    public async Task An_asynchronous_walk_through_the_interface_finds_the_same_entries()
    {
        Tree();
        using RecordingDir root = Root();

        List<string> synchronous = [.. root.Walk().Select(e => e.Name).Order()];
        List<string> asynchronous = [];
        await foreach (WalkEntry entry in root.WalkAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            asynchronous.Add(entry.Name);
        }

        Assert.Equal(synchronous, asynchronous.Order());
        Assert.Contains("./a/b: EnumerateEntriesAsync()", root.Log);
    }

    [Fact]
    public void A_walk_through_the_interface_that_does_not_follow_links_does_not_enter_one()
    {
        _fs.AddFile("inside/secret.txt", "s");
        _fs.AddSymbolicLink("link", "inside", targetIsDirectory: true);
        using RecordingDir root = Root();

        List<string> names = [.. root.Walk().Select(e => e.Name)];

        Assert.Single(names, "secret.txt");
        Assert.Contains("link", names);
    }

    [Fact]
    public void A_pattern_search_through_the_interface_reads_only_the_directories_it_needs()
    {
        Tree();
        _fs.AddFile("other/deep/x.txt", "x");
        using RecordingDir root = Root();

        List<string> found = [.. root.Glob("a/*.txt").Select(e => e.Name)];

        Assert.Equal(["one.txt"], found);
        Assert.DoesNotContain(root.Log, call => call.StartsWith("./other", StringComparison.Ordinal));
        AssertEveryNameIsOneComponent(root);
    }

    [Fact]
    public void The_predicates_ask_the_interface_to_describe_the_name()
    {
        Tree();
        _fs.AddSymbolicLink("link", "top.txt");
        using RecordingDir root = Root();

        Assert.True(root.IsDir("a"));
        Assert.True(root.IsFile("top.txt"));
        Assert.True(root.IsSymlink("link"));
        Assert.False(root.IsFile("missing"));
        Assert.Equal(
            [".: TryGetMetadata(a)", ".: TryGetMetadata(top.txt)", ".: TryGetMetadata(link)", ".: TryGetMetadata(missing)"],
            root.Log);
    }

    [Theory]
    [InlineData(Durability.FileAndDirectory)]
    [InlineData(Durability.File)]
    [InlineData(Durability.None)]
    public void An_atomic_write_through_the_interface_writes_a_scratch_name_then_moves_it(Durability durability)
    {
        _fs.AddFile("out/report.txt", "old");
        using RecordingDir root = Root();

        root.WriteAllTextAtomic("out/report.txt", "new", durability);

        Assert.Equal("new", _fs.ReadAllText("out/report.txt"));
        Assert.Equal(["report.txt"], _fs.GetEntries("out"));

        // The path ahead of the name is resolved once, and everything else happens against
        // the directory that reached: create the scratch name exclusively, write it, commit it,
        // move it onto the name, commit the directory.
        Assert.Equal(".: OpenDir(out/, False)", root.Log[0]);
        string created = Assert.Single(root.Log, call => call.StartsWith("./out: TryOpenFile(", StringComparison.Ordinal));
        Assert.EndsWith(", CreateNew)", created, StringComparison.Ordinal);
        string scratch = created["./out: TryOpenFile(".Length..created.IndexOf(',', StringComparison.Ordinal)];

        List<string> expected =
        [
            ".: OpenDir(out/, False)",
            created,
            $"./out/{scratch}: Write(3, 0)",
        ];
        if (durability != Durability.None)
        {
            expected.Add($"./out/{scratch}: Flush(True)");
        }

        expected.Add($"./out: Rename({scratch}, [./out], report.txt, True)");
        if (durability == Durability.FileAndDirectory)
        {
            expected.Add("./out: Flush(True)");
        }

        Assert.Equal(expected, root.Log);
    }

    [Fact]
    public async Task An_asynchronous_atomic_write_through_the_interface_writes_asynchronously()
    {
        using RecordingDir root = Root();

        await root.WriteAllBytesAtomicAsync(
            "report.bin", new byte[] { 1, 2, 3 }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], _fs.ReadAllBytes("report.bin"));
        Assert.Contains(root.Log, call => call.EndsWith(": WriteAsync(3, 0)", StringComparison.Ordinal));
        Assert.Equal(".: Flush(True)", root.Log[^1]);
    }

    [Fact]
    public void A_failed_atomic_write_through_the_interface_removes_its_scratch_name()
    {
        _fs.AddDirectory("report.txt");
        using RecordingDir root = Root();

        _ = Assert.ThrowsAny<IOException>(() => root.WriteAllTextAtomic("report.txt", "new"));

        Assert.Equal(["report.txt"], _fs.GetEntries());
        Assert.Contains(root.Log, call => call.StartsWith(".: TryDeleteFile(", StringComparison.Ordinal));
    }

    [Fact]
    public void A_copy_through_the_interface_reproduces_the_tree()
    {
        Tree();
        _fs.AddDirectory("copy");
        using RecordingDir source = new(_fs.OpenRoot("a", SymlinkPolicy.FollowWithinSandbox));
        using RecordingDir destination = new(_fs.OpenRoot("copy", SymlinkPolicy.FollowWithinSandbox), [], "dest");

        CopyReport report = source.CopyTo(destination);

        Assert.Equal(2, report.Files);
        Assert.Equal(2, report.Directories);
        Assert.Equal("one", _fs.ReadAllText("copy/one.txt"));
        Assert.Equal("two", _fs.ReadAllText("copy/b/two.txt"));
        Assert.Equal(["b", "empty", "one.txt"], _fs.GetEntries("copy"));

        // Every directory is read through its own handle and made through its parent's; every
        // file is made by name in the directory that will hold it.
        Assert.Contains("dest: CreateDir(b)", destination.Log);
        Assert.Contains("dest: CreateDir(empty)", destination.Log);
        Assert.Contains("dest: CreateNewFile(one.txt)", destination.Log);
        Assert.Contains("dest/b: CreateNewFile(two.txt)", destination.Log);
        Assert.Contains("./b: EnumerateEntries()", source.Log);
        AssertEveryNameIsOneComponent(source);
        AssertEveryNameIsOneComponent(destination);
    }

    [Fact]
    public void A_copy_through_the_interface_that_replaces_files_moves_a_scratch_copy_into_place()
    {
        _fs.AddFile("src/data.txt", "new");
        _fs.AddFile("dst/data.txt", "old");
        using RecordingDir source = new(_fs.OpenRoot("src", SymlinkPolicy.FollowWithinSandbox));
        using RecordingDir destination = new(_fs.OpenRoot("dst", SymlinkPolicy.FollowWithinSandbox), [], "dest");

        _ = source.CopyTo(destination, new CopyOptions { Overwrite = true });

        Assert.Equal("new", _fs.ReadAllText("dst/data.txt"));
        Assert.Contains(destination.Log, call =>
            call.StartsWith("dest: Rename(", StringComparison.Ordinal) &&
            call.EndsWith(", [dest], data.txt, True)", StringComparison.Ordinal));
        Assert.DoesNotContain(destination.Log, call => call.Contains("OpenFile(data.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void A_copy_that_must_preserve_permissions_is_refused_by_a_destination_that_cannot_take_them()
    {
        _fs.AddFile("src/data.txt", "x");
        _fs.AddDirectory("dst");
        using RecordingDir source = new(_fs.OpenRoot("src", SymlinkPolicy.FollowWithinSandbox));
        using RecordingDir destination = new(_fs.OpenRoot("dst", SymlinkPolicy.FollowWithinSandbox));

        CapIOException refused = Assert.Throws<CapIOException>(
            () => source.CopyTo(destination, new CopyOptions { PreservePermissions = true }));

        Assert.Equal(CapErrorKind.NotSupported, refused.Kind);
    }

    [Fact]
    public void A_copy_from_a_wrapper_into_a_handle_preserves_permissions()
    {
        _fs.AddFile("src/data.txt", "x");
        _fs.AddDirectory("dst");
        using RecordingDir source = new(_fs.OpenRoot("src", SymlinkPolicy.FollowWithinSandbox));
        using Dir destination = _fs.OpenRoot("dst", SymlinkPolicy.FollowWithinSandbox);

        CopyReport report = source.CopyTo(destination, new CopyOptions { PreservePermissions = true });

        Assert.Equal(1, report.Files);
        Assert.Equal("x", _fs.ReadAllText("dst/data.txt"));
    }

    [Fact]
    public void Removing_a_tree_through_the_interface_descends_by_handle_and_removes_by_name()
    {
        Tree();
        using RecordingDir root = Root();

        root.DeleteTree("a");

        Assert.Equal(["top.txt"], _fs.GetEntries());
        Assert.Equal(".: Restrict(Deny)", root.Log[0]);
        Assert.Contains(".: TryOpenDir(a, True)", root.Log);
        Assert.Contains("./a: TryOpenDir(b, True)", root.Log);
        Assert.Contains("./a/b: TryDeleteFile(two.txt)", root.Log);
        Assert.Contains("./a: TryDeleteDir(b)", root.Log);
        Assert.Equal(".: TryDeleteDir(a)", root.Log[^1]);
        AssertEveryNameIsOneComponent(root);
    }

    [Fact]
    public void Removing_a_tree_through_the_interface_removes_a_link_inside_it_and_not_its_target()
    {
        _fs.AddFile("keep/precious.txt", "p");
        _fs.AddDirectory("doomed");
        _fs.AddSymbolicLink("doomed/link", "../keep", targetIsDirectory: true);
        using RecordingDir root = Root();

        root.DeleteTree("doomed");

        Assert.False(_fs.Exists("doomed"));
        Assert.Equal("p", _fs.ReadAllText("keep/precious.txt"));
    }

    [Fact]
    public void Removing_a_tree_through_the_interface_refuses_what_is_not_a_directory()
    {
        Tree();
        _fs.AddSymbolicLink("link", "a", targetIsDirectory: true);
        using RecordingDir root = Root();

        Assert.Equal(CapErrorKind.NotADirectory, Assert.Throws<CapIOException>(() => root.DeleteTree("top.txt")).Kind);
        Assert.Equal(CapErrorKind.SymbolicLink, Assert.Throws<CapIOException>(() => root.DeleteTree("link")).Kind);
        _ = Assert.Throws<DirectoryNotFoundException>(() => root.DeleteTree("missing"));
        Assert.False(root.TryDeleteTree("top.txt"));
        Assert.False(root.TryDeleteTree("missing"));

        Assert.True(_fs.Exists("top.txt"));
        Assert.True(_fs.Exists("link"));
        Assert.True(_fs.Exists("a/b/two.txt"));
    }

    [Fact]
    public void A_failure_part_way_through_a_removal_through_the_interface_is_reported_after_the_rest_is_removed()
    {
        Tree();
        _fs.SetUndeletable("a/one.txt");
        using RecordingDir root = Root();

        _ = Assert.Throws<UnauthorizedAccessException>(() => root.DeleteTree("a"));

        Assert.True(_fs.Exists("a/one.txt"));
        Assert.False(_fs.Exists("a/b"));
        Assert.False(_fs.Exists("a/empty"));
    }

    [Fact]
    public void Emptying_a_directory_through_the_interface_leaves_the_directory()
    {
        Tree();
        using RecordingDir root = Root();
        using IDir a = root.OpenDir("a");

        a.DeleteTreeContents();

        Assert.True(_fs.Exists("a"));
        Assert.Empty(_fs.GetEntries("a"));
        Assert.True(_fs.Exists("top.txt"));
        Assert.Contains("./a: Restrict(Deny)", root.Log);
    }

    [Fact]
    public void A_walk_over_a_handle_hands_out_handles_of_the_concrete_types()
    {
        Tree();
        using Dir root = _fs.OpenRoot();

        foreach (WalkEntry entry in root.Walk())
        {
            _ = Assert.IsType<Dir>(entry.Directory);
            _ = Assert.IsType<DirEntry>(entry.Entry);
            if (entry.Type == CapFileType.File)
            {
                using ICapFile file = entry.OpenFile();
                _ = Assert.IsType<CapFile>(file);
            }
        }
    }

    [Fact]
    public void Text_reaches_the_file_as_the_handle_form_writes_it()
    {
        using RecordingDir root = Root();

        root.WriteAllTextAtomic("note.txt", "naïve");

        Assert.Equal(Encoding.UTF8.GetBytes("naïve"), _fs.ReadAllBytes("note.txt"));
    }
}
