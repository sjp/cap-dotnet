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
        Assert.Contains(".: OpenDir(a, True)", root.Log);
        Assert.Contains("./a: OpenDir(b, True)", root.Log);
        Assert.Contains("./a: OpenDir(empty, True)", root.Log);
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

        Assert.Contains("./a/b: OpenFile(two.txt, Open, False)", root.Log);
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
    public void A_walk_through_the_interface_fails_on_an_unreadable_directory()
    {
        Tree();
        _fs.SetUnreadable("a/b");
        using RecordingDir root = Root();

        Assert.Throws<UnauthorizedAccessException>(() => root.Walk().ToList());
        Assert.Throws<UnauthorizedAccessException>(() => root.Glob("**/*.txt").ToList());

        List<string> skipped = [];
        WalkOptions options = new()
        {
            OnError = (entry, _) =>
            {
                skipped.Add(entry.Name);
                return true;
            },
        };
        List<string> names = [.. root.Walk(options).Select(e => e.Name)];

        Assert.Equal(["b"], skipped);
        Assert.Contains("b", names);
        Assert.DoesNotContain("two.txt", names);
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
        // the directory that reached: describe what the name holds, create the scratch name
        // exclusively, write it, give it the replaced file's permissions, commit it, move it
        // onto the name, commit the directory.
        Assert.Equal(".: OpenDir(out/, False)", root.Log[0]);
        string created = Assert.Single(root.Log, call => call.StartsWith("./out: TryOpenFile(", StringComparison.Ordinal));
        Assert.EndsWith(", CreateNew)", created, StringComparison.Ordinal);
        string scratch = created["./out: TryOpenFile(".Length..created.IndexOf(',', StringComparison.Ordinal)];
        string carried = Assert.Single(
            root.Log, call => call.StartsWith($"./out/{scratch}: SetPermissions(", StringComparison.Ordinal));

        List<string> expected =
        [
            ".: OpenDir(out/, False)",
            "./out: TryGetMetadata(report.txt)",
            created,
            $"./out/{scratch}: Write(3, 0)",
            carried,
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

    /// <summary>
    /// Asked not to keep the replaced file's permissions, an atomic write neither describes the
    /// name nor sets any permissions.
    /// </summary>
    [Fact]
    public void An_atomic_write_through_the_interface_that_keeps_no_permissions_does_not_look_at_the_name()
    {
        _fs.AddFile("report.txt", "old");
        using RecordingDir root = Root();

        root.WriteAllTextAtomic("report.txt", "new", new AtomicWriteOptions { PreservePermissions = false });

        Assert.Equal("new", _fs.ReadAllText("report.txt"));
        Assert.DoesNotContain(root.Log, call => call.Contains("TryGetMetadata(", StringComparison.Ordinal));
        Assert.DoesNotContain(root.Log, call => call.Contains("SetPermissions(", StringComparison.Ordinal));
    }

    /// <summary>
    /// Through the interface the replaced file's mode reaches the published file, which is
    /// created as any file is and given the mode once its contents are written.
    /// </summary>
    [Fact]
    public void An_atomic_write_through_the_interface_keeps_the_replaced_files_mode()
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Unix });
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        fs.AddFile("report.txt", "old");
        fs.SetUnixMode("report.txt", Private);
        using RecordingDir root = new(fs.OpenRoot());

        root.WriteAllTextAtomic("report.txt", "new");

        Assert.Equal("new", fs.ReadAllText("report.txt"));
        Assert.True(root.GetMetadata("report.txt").Permissions.TryGetUnixMode(out UnixFileMode mode));
        Assert.Equal(Private, mode);
        Assert.Contains(root.Log, call => call.EndsWith($": SetPermissions({Private})", StringComparison.Ordinal));
    }

    /// <summary>
    /// Under Windows rules the replaced file's attribute flags are what is carried.
    /// </summary>
    [Fact]
    public void An_atomic_write_under_windows_rules_keeps_the_replaced_files_attributes()
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Windows });
        fs.AddFile("report.txt", "old");
        fs.SetAttributes("report.txt", FileAttributes.Hidden);
        using Dir root = fs.OpenRoot();

        root.WriteAllTextAtomic("report.txt", "new");

        Assert.Equal("new", fs.ReadAllText("report.txt"));
        Assert.True(root.GetMetadata("report.txt").Permissions.TryGetWindowsAttributes(out FileAttributes attributes));
        Assert.True(attributes.HasFlag(FileAttributes.Hidden), attributes.ToString());
    }

    /// <summary>
    /// Under Windows rules a read-only file cannot be replaced by moving another onto it, so
    /// the publish is refused and the file and its contents are left as they were.
    /// </summary>
    [Fact]
    public void An_atomic_write_under_windows_rules_is_refused_by_a_read_only_file()
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Windows });
        fs.AddFile("report.txt", "old");
        fs.SetAttributes("report.txt", FileAttributes.ReadOnly);
        using Dir root = fs.OpenRoot();

        Exception? refused = Record.Exception(() => root.WriteAllTextAtomic("report.txt", "new"));

        Assert.True(refused is UnauthorizedAccessException or IOException, refused?.ToString());
        Assert.Equal("old", fs.ReadAllText("report.txt"));
        Assert.Equal(["report.txt"], fs.GetEntries());
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

    /// <summary>
    /// A streamed publish through the interface takes the same steps as the one-call form:
    /// create the scratch name exclusively, commit it, move it onto the name, commit the
    /// directory.
    /// </summary>
    [Fact]
    public void A_streamed_atomic_write_through_the_interface_writes_a_scratch_name_then_moves_it()
    {
        _fs.AddFile("out/report.txt", "old");
        using RecordingDir root = Root();

        using (AtomicFile publish = root.OpenAtomicWrite("out/report.txt"))
        {
            publish.Stream.Write("new"u8);
            publish.Commit();
        }

        Assert.Equal("new", _fs.ReadAllText("out/report.txt"));
        Assert.Equal(["report.txt"], _fs.GetEntries("out"));
        string created = Assert.Single(root.Log, call => call.StartsWith("./out: TryOpenFile(", StringComparison.Ordinal));
        Assert.EndsWith(", CreateNew)", created, StringComparison.Ordinal);
        string scratch = created["./out: TryOpenFile(".Length..created.IndexOf(',', StringComparison.Ordinal)];
        Assert.Equal(
            [$"./out/{scratch}: Flush(True)", $"./out: Rename({scratch}, [./out], report.txt, True)", "./out: Flush(True)"],
            root.Log[^3..]);
    }

    /// <summary>
    /// A streamed publish disposed without a commit through the interface removes its scratch
    /// name and never moves anything.
    /// </summary>
    [Fact]
    public void An_abandoned_streamed_atomic_write_through_the_interface_removes_its_scratch_name()
    {
        _fs.AddFile("report.txt", "old");
        using RecordingDir root = Root();

        using (AtomicFile publish = root.OpenAtomicWrite("report.txt"))
        {
            publish.Stream.Write("new"u8);
        }

        Assert.Equal("old", _fs.ReadAllText("report.txt"));
        Assert.Equal(["report.txt"], _fs.GetEntries());
        Assert.StartsWith(".: TryDeleteFile(", root.Log[^1], StringComparison.Ordinal);
        Assert.DoesNotContain(root.Log, call => call.Contains("Rename(", StringComparison.Ordinal));
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

        CopyReport report = source.CopyTo(destination, cancellationToken: TestContext.Current.CancellationToken);

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
    public async Task An_asynchronous_copy_through_the_interface_reads_and_writes_asynchronously()
    {
        Tree();
        _fs.AddDirectory("copy");
        using RecordingDir source = new(_fs.OpenRoot("a", SymlinkPolicy.FollowWithinSandbox));
        using RecordingDir destination = new(_fs.OpenRoot("copy", SymlinkPolicy.FollowWithinSandbox), [], "dest");

        CopyReport report = await source.CopyToAsync(
            destination, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Files);
        Assert.Equal(2, report.Directories);
        Assert.Equal("one", _fs.ReadAllText("copy/one.txt"));
        Assert.Equal("two", _fs.ReadAllText("copy/b/two.txt"));
        Assert.Equal(["b", "empty", "one.txt"], _fs.GetEntries("copy"));

        // Directories are read, and contents written, in the forms that do not hold the thread;
        // the names are still made one component at a time in the directory that holds them.
        Assert.Contains(".: EnumerateEntriesAsync()", source.Log);
        Assert.Contains("./b: EnumerateEntriesAsync()", source.Log);
        Assert.DoesNotContain(source.Log, call => call.EndsWith(": EnumerateEntries()", StringComparison.Ordinal));
        Assert.Contains("dest/one.txt: WriteAsync(3, 0)", destination.Log);
        Assert.Contains("dest/b/two.txt: WriteAsync(3, 0)", destination.Log);
        Assert.DoesNotContain(destination.Log, call => call.Contains(": Write(", StringComparison.Ordinal));
        Assert.Contains(".: OpenFile(one.txt, Open, True)", source.Log);
        AssertEveryNameIsOneComponent(source);
        AssertEveryNameIsOneComponent(destination);
    }

    [Fact]
    public void A_copy_through_the_interface_opens_each_entry_refusing_links()
    {
        Tree();
        _fs.AddDirectory("copy");
        using RecordingDir source = new(_fs.OpenRoot("a", SymlinkPolicy.FollowWithinSandbox));
        using RecordingDir destination = new(_fs.OpenRoot("copy", SymlinkPolicy.FollowWithinSandbox), [], "dest");

        _ = source.CopyTo(destination, cancellationToken: TestContext.Current.CancellationToken);

        // A name described as a directory or a file may be a link by the time it is opened, so
        // the open itself refuses one rather than following it under the handle's policy.
        Assert.Contains(".: OpenDir(b, True)", source.Log);
        Assert.Contains(".: OpenDir(empty, True)", source.Log);
        Assert.Contains(".: OpenFile(one.txt, Open, True)", source.Log);
        Assert.Contains("./b: OpenFile(two.txt, Open, True)", source.Log);
        Assert.DoesNotContain(source.Log, call =>
            call.Contains(": OpenDir(", StringComparison.Ordinal) && call.EndsWith(", False)", StringComparison.Ordinal));
        Assert.DoesNotContain(source.Log, call =>
            call.Contains(": OpenFile(", StringComparison.Ordinal) && call.EndsWith(", False)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_copy_through_the_interface_that_replaces_files_moves_a_scratch_copy_into_place()
    {
        _fs.AddFile("src/data.txt", "new");
        _fs.AddFile("dst/data.txt", "old");
        using RecordingDir source = new(_fs.OpenRoot("src", SymlinkPolicy.FollowWithinSandbox));
        using RecordingDir destination = new(_fs.OpenRoot("dst", SymlinkPolicy.FollowWithinSandbox), [], "dest");

        _ = source.CopyTo(destination, new CopyOptions { Overwrite = true }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("new", _fs.ReadAllText("dst/data.txt"));
        Assert.Contains(destination.Log, call =>
            call.StartsWith("dest: Rename(", StringComparison.Ordinal) &&
            call.EndsWith(", [dest], data.txt, True)", StringComparison.Ordinal));
        Assert.DoesNotContain(destination.Log, call => call.Contains("OpenFile(data.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void A_copy_that_must_preserve_permissions_writes_them_through_a_wrapped_destination()
    {
        _fs.AddFile("src/data.txt", "x");
        _fs.AddDirectory("src/inner");
        _fs.AddDirectory("dst");
        using RecordingDir source = new(_fs.OpenRoot("src", SymlinkPolicy.FollowWithinSandbox));
        using RecordingDir destination = new(_fs.OpenRoot("dst", SymlinkPolicy.FollowWithinSandbox), [], "dest");

        CopyReport report = source.CopyTo(destination, new CopyOptions { PreservePermissions = true }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Files);
        Assert.Contains(destination.Log, call => call.StartsWith("dest/inner: SetPermissions(", StringComparison.Ordinal));
        Assert.Contains(destination.Log, call => call.Contains(": SetPermissions(", StringComparison.Ordinal) && !call.StartsWith("dest/inner", StringComparison.Ordinal));
    }

    /// <summary>
    /// A copied directory is closed to everyone but its owner while it is filled, and given
    /// the source's permissions only once its contents are in, so that a source directory its
    /// owner cannot write to does not make a copy that cannot be filled.
    /// </summary>
    [Fact]
    public void A_copied_directory_is_owner_only_while_filled_and_given_the_source_permissions_last()
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Unix });
        const UnixFileMode Locked =
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        fs.AddFile("src/inner/data.txt", "x");
        fs.SetUnixMode("src/inner", Locked);
        fs.AddDirectory("dst");
        using Dir source = fs.OpenRoot("src", SymlinkPolicy.FollowWithinSandbox);
        using RecordingDir destination = new(fs.OpenRoot("dst", SymlinkPolicy.FollowWithinSandbox), [], "dest");

        _ = source.CopyTo(destination, new CopyOptions { PreservePermissions = true }, cancellationToken: TestContext.Current.CancellationToken);

        List<string> log = destination.Log;
        int guarded = log.IndexOf($"dest/inner: SetPermissions({UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute})");
        int created = log.FindIndex(call => call.StartsWith("dest/inner: CreateNewFile(data.txt", StringComparison.Ordinal));
        int finished = log.LastIndexOf($"dest/inner: SetPermissions({Locked})");
        Assert.True(guarded >= 0 && guarded < created && created < finished, string.Join(Environment.NewLine, log));
        Assert.True(destination.GetMetadata("inner").Permissions.TryGetUnixMode(out UnixFileMode copied));
        Assert.Equal(Locked, copied);
        Assert.Equal("x", fs.ReadAllText("dst/inner/data.txt"));
    }

    [Fact]
    public void A_copy_from_a_wrapper_into_a_handle_preserves_permissions()
    {
        _fs.AddFile("src/data.txt", "x");
        _fs.AddDirectory("dst");
        using RecordingDir source = new(_fs.OpenRoot("src", SymlinkPolicy.FollowWithinSandbox));
        using Dir destination = _fs.OpenRoot("dst", SymlinkPolicy.FollowWithinSandbox);

        CopyReport report = source.CopyTo(destination, new CopyOptions { PreservePermissions = true }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Files);
        Assert.Equal("x", _fs.ReadAllText("dst/data.txt"));
    }

    [Fact]
    public void Removing_a_tree_through_the_interface_descends_by_handle_and_removes_by_name()
    {
        Tree();
        using RecordingDir root = Root();

        root.DeleteTree("a", TestContext.Current.CancellationToken);

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

        root.DeleteTree("doomed", TestContext.Current.CancellationToken);

        Assert.False(_fs.Exists("doomed"));
        Assert.Equal("p", _fs.ReadAllText("keep/precious.txt"));
    }

    [Fact]
    public void Removing_a_tree_through_the_interface_refuses_what_is_not_a_directory()
    {
        Tree();
        _fs.AddSymbolicLink("link", "a", targetIsDirectory: true);
        using RecordingDir root = Root();

        Assert.Equal(CapErrorKind.NotADirectory, Assert.Throws<CapIOException>(() => root.DeleteTree("top.txt", TestContext.Current.CancellationToken)).Kind);
        Assert.Equal(CapErrorKind.SymbolicLink, Assert.Throws<CapIOException>(() => root.DeleteTree("link", TestContext.Current.CancellationToken)).Kind);
        _ = Assert.Throws<DirectoryNotFoundException>(() => root.DeleteTree("missing", TestContext.Current.CancellationToken));
        Assert.False(root.TryDeleteTree("top.txt", TestContext.Current.CancellationToken));
        Assert.False(root.TryDeleteTree("missing", TestContext.Current.CancellationToken));

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

        _ = Assert.Throws<UnauthorizedAccessException>(() => root.DeleteTree("a", TestContext.Current.CancellationToken));

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

        a.DeleteTreeContents(TestContext.Current.CancellationToken);

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
