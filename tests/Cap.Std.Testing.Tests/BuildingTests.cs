using Cap.Fs.Ext;
using Cap.Primitives;
using Microsoft.Extensions.Time.Testing;

namespace Cap.Std.Testing.Tests;

/// <summary>
/// Building a tree before a test and inspecting it afterwards, without a handle.
/// </summary>
public sealed class BuildingTests
{
    [Fact]
    public void A_built_tree_can_be_inspected_as_it_was_built()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("config/app.json", """{ "x": 1 }""");
        fs.AddDirectory("data");
        fs.AddSymbolicLink("data/latest", "../config");
        fs.AddFile("/data/blob.bin", [1, 2, 3]);

        Assert.True(fs.Exists("config/app.json"));
        Assert.True(fs.Exists("/config"));
        Assert.False(fs.Exists("config/other.json"));
        Assert.Equal("""{ "x": 1 }""", fs.ReadAllText("config/app.json"));
        Assert.Equal([1, 2, 3], fs.ReadAllBytes("data/blob.bin"));
        Assert.Equal(["config", "data"], fs.GetEntries());
        Assert.Equal(["blob.bin", "latest"], fs.GetEntries("data"));
        Assert.Equal("../config", fs.GetSymbolicLinkTarget("data/latest"));
    }

    [Fact]
    public void A_build_path_does_not_follow_links()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("real/file.txt", "x");
        fs.AddSymbolicLink("alias", "real");

        Assert.False(fs.Exists("alias/file.txt"));
        Assert.Throws<IOException>(() => fs.AddFile("alias/new.txt", "y"));
    }

    [Fact]
    public void A_name_that_is_taken_is_not_built_over()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("a.txt", "one");
        fs.AddDirectory("dir");

        Assert.Throws<IOException>(() => fs.AddFile("a.txt", "two"));
        Assert.Throws<IOException>(() => fs.AddDirectory("a.txt"));
        fs.AddDirectory("dir");
        Assert.Equal("one", fs.ReadAllText("a.txt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("a/../b")]
    [InlineData("./a")]
    public void A_build_path_that_names_nothing_or_climbs_is_refused(string path)
    {
        InMemoryFileSystem fs = new();
        Assert.Throws<ArgumentException>(() => fs.AddFile(path, "x"));
    }

    [Fact]
    public void A_name_the_handles_would_refuse_cannot_be_built()
    {
        InMemoryFileSystem windows = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Windows });
        Assert.Throws<ArgumentException>(() => windows.AddFile("CON", "x"));
        Assert.Throws<ArgumentException>(() => windows.AddFile("trailing.", "x"));
        Assert.Throws<ArgumentException>(() => windows.AddFile(@"a\b", "x"));

        InMemoryFileSystem unix = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Unix });
        unix.AddFile(@"a\b", "x");
        Assert.Equal([@"a\b"], unix.GetEntries());

        // 200 characters is 400 UTF-8 bytes, past what Linux stores in a name, but only 200
        // UTF-16 units, which Windows stores.
        string name = new('é', 200);
        Assert.Throws<ArgumentException>(() => unix.AddFile(name, "x"));
        Assert.Throws<ArgumentException>(() => unix.AddDirectory("d/" + name));
        Assert.Throws<ArgumentException>(() => unix.AddSymbolicLink(name, "a"));
        Assert.Throws<ArgumentException>(() => unix.AddHardLink(name, @"a\b"));
        Assert.Equal([@"a\b"], unix.GetEntries());

        windows.AddFile(name, "x");
        Assert.Equal([name], windows.GetEntries());
    }

    [Fact]
    public void A_hard_link_is_a_second_name_for_the_same_file()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("one.txt", "shared");
        fs.AddHardLink("two.txt", "one.txt");

        using Dir root = fs.OpenRoot();
        CapMetadata one = root.GetMetadata("one.txt");
        CapMetadata two = root.GetMetadata("two.txt");

        Assert.True(one.IsSameFileAs(two));
        Assert.Equal(2, one.LinkCount);

        fs.AddDirectory("dir");
        Assert.Throws<IOException>(() => fs.AddHardLink("dir-link", "dir"));
    }

    [Fact]
    public void Times_and_permissions_set_while_building_are_reported_through_a_handle()
    {
        DateTimeOffset written = new(2020, 5, 6, 7, 8, 9, TimeSpan.Zero);
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Unix });
        fs.AddFile("a.txt", "x");
        fs.SetTimes("a.txt", lastWrite: written);
        fs.SetUnixMode("a.txt", UnixFileMode.UserRead);

        using Dir root = fs.OpenRoot();
        CapMetadata metadata = root.GetMetadata("a.txt");

        Assert.Equal(written, metadata.LastWriteTime);
        Assert.True(metadata.Permissions.TryGetUnixMode(out UnixFileMode mode));
        Assert.Equal(UnixFileMode.UserRead, mode);
        Assert.False(metadata.Permissions.TryGetWindowsAttributes(out _));
        Assert.Throws<InvalidOperationException>(() => fs.SetAttributes("a.txt", FileAttributes.ReadOnly));
    }

    [Fact]
    public void Writing_creates_a_file_or_rewrites_one_in_place()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { TimeProvider = clock });
        fs.WriteAllText("new/a.txt", "first");
        fs.AddHardLink("b.txt", "new/a.txt");
        CapMetadata before = fs.GetMetadata("new/a.txt");
        Assert.Equal(5, fs.UsedBytes);

        clock.Advance(TimeSpan.FromMinutes(1));
        fs.WriteAllBytes("/new/a.txt", [1, 2]);

        CapMetadata after = fs.GetMetadata("new/a.txt");
        Assert.True(after.IsSameFileAs(before));
        Assert.Equal([1, 2], fs.ReadAllBytes("b.txt"));
        Assert.Equal(clock.GetUtcNow(), after.LastWriteTime);
        Assert.Equal(clock.GetUtcNow(), after.ChangeTime);
        Assert.Equal(before.CreationTime, after.CreationTime);
        Assert.Equal(2, fs.UsedBytes);

        fs.AddSymbolicLink("link", "b.txt");
        Assert.Throws<IOException>(() => fs.WriteAllText("new", "x"));
        Assert.Throws<IOException>(() => fs.WriteAllText("link", "x"));
        Assert.Throws<IOException>(() => fs.WriteAllText("b.txt/c", "x"));
        Assert.Throws<ArgumentException>(() => fs.WriteAllText("", "x"));
    }

    [Fact]
    public void A_rewrite_is_seen_through_a_handle_already_open()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("a.txt", "old");

        using Dir root = fs.OpenRoot();
        using CapFile file = root.OpenFile("a.txt", FileMode.Open, FileAccess.Read);
        fs.WriteAllText("a.txt", "new!");

        Assert.Equal(4, file.GetMetadata().Length);
    }

    [Fact]
    public void Removing_a_file_keeps_its_other_names_and_its_open_handles()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("a.txt", "abc");
        fs.AddHardLink("b.txt", "a.txt");
        fs.AddFile("c.txt", "xy");
        fs.AddSymbolicLink("link", "c.txt");
        fs.SetUndeletable("c.txt");
        using Dir root = fs.OpenRoot();
        using CapFile open = root.OpenFile("c.txt", FileMode.Open, FileAccess.Read);

        fs.RemoveFile("a.txt");
        fs.RemoveFile("link");
        fs.RemoveFile("c.txt");

        Assert.Equal(["b.txt"], fs.GetEntries());
        Assert.Equal(1, fs.GetMetadata("b.txt").LinkCount);
        Assert.Equal(5, fs.UsedBytes);
        Assert.Equal(0, open.GetMetadata().LinkCount);
        open.Dispose();
        Assert.Equal(3, fs.UsedBytes);
        Assert.Throws<IOException>(() => fs.RemoveFile("a.txt"));

        fs.AddDirectory("dir");
        Assert.Throws<IOException>(() => fs.RemoveFile("dir"));
    }

    [Fact]
    public void Removing_a_directory_refuses_one_with_entries_unless_asked_to_empty_it()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("tree/sub/a.txt", "abc");
        fs.AddFile("keep.txt", "k");
        fs.AddHardLink("tree/sub/b.txt", "keep.txt");
        fs.AddDirectory("empty");
        fs.AddSymbolicLink("to-tree", "tree");

        fs.RemoveDirectory("empty");
        Assert.Throws<IOException>(() => fs.RemoveDirectory("tree"));
        Assert.Throws<IOException>(() => fs.RemoveDirectory("to-tree", recursive: true));
        Assert.Throws<IOException>(() => fs.RemoveDirectory("keep.txt"));
        Assert.Throws<IOException>(() => fs.RemoveDirectory("missing"));
        Assert.Throws<ArgumentException>(() => fs.RemoveDirectory("", recursive: true));

        fs.RemoveDirectory("tree", recursive: true);

        Assert.Equal(["keep.txt", "to-tree"], fs.GetEntries());
        Assert.Equal(1, fs.GetMetadata("keep.txt").LinkCount);
        Assert.Equal(1, fs.UsedBytes);
    }

    [Fact]
    public void Metadata_read_without_a_handle_matches_what_a_handle_reports()
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Unix });
        fs.AddFile("dir/a.txt", "abc");
        fs.AddDirectory("dir/sub");
        fs.AddSymbolicLink("link", "dir/a.txt");
        fs.SetUnixMode("", UnixFileMode.UserRead | UnixFileMode.UserExecute);

        using Dir root = fs.OpenRoot();
        foreach (string path in new[] { "dir/a.txt", "dir", "link" })
        {
            CapMetadata expected = root.GetMetadata(path);
            CapMetadata actual = fs.GetMetadata(path);
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.Length, actual.Length);
            Assert.Equal(expected.LinkCount, actual.LinkCount);
            Assert.Equal(expected.LastWriteTime, actual.LastWriteTime);
            Assert.True(expected.IsSameFileAs(actual));
        }

        Assert.Equal(3, fs.GetMetadata("dir").LinkCount);
        Assert.Equal(CapFileType.Symlink, fs.GetMetadata("link").Type);
        Assert.True(fs.GetMetadata("/").Permissions.TryGetUnixMode(out UnixFileMode mode));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserExecute, mode);
        Assert.True(fs.GetMetadata("").IsSameFileAs(root.GetMetadata()));
        Assert.Throws<IOException>(() => fs.GetMetadata("missing"));
    }

    [Fact]
    public void A_snapshot_holds_every_name_and_what_it_named()
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Unix });
        fs.AddFile("b/c.txt", "text");
        fs.AddSymbolicLink("a-link", "b/c.txt");
        fs.AddHardLink("b/d.txt", "b/c.txt");

        InMemorySnapshot snapshot = fs.Snapshot();
        fs.WriteAllText("b/c.txt", "changed afterwards");

        Assert.Equal(["a-link", "b", "b/c.txt", "b/d.txt"], snapshot.Keys);
        Assert.Equal("text"u8.ToArray(), snapshot["b/c.txt"].Contents.ToArray());
        Assert.Equal(CapFileType.Directory, snapshot["/b"].Type);
        Assert.Equal("b/c.txt", snapshot["a-link"].LinkTarget);
        Assert.Null(snapshot["b/c.txt"].LinkTarget);
        Assert.True(snapshot["b/c.txt"].Metadata.IsSameFileAs(snapshot["b/d.txt"].Metadata));
        Assert.Equal(2, snapshot["b/d.txt"].Metadata.LinkCount);
        Assert.False(snapshot.ContainsKey("b/missing"));
        Assert.Throws<KeyNotFoundException>(() => snapshot["b/missing"]);
    }

    [Fact]
    public void A_snapshot_of_a_case_insensitive_tree_finds_a_path_under_any_spelling()
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { CaseSensitive = false });
        fs.AddFile("Dir/File.txt", "x");

        InMemorySnapshot snapshot = fs.Snapshot();

        Assert.Equal(["Dir", "Dir/File.txt"], snapshot.Keys);
        Assert.True(snapshot.ContainsKey("dir/file.TXT"));
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_diff_reports_exactly_what_the_handles_changed(ResolutionBackend resolution)
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions
        {
            Resolution = resolution,
            PathSyntax = CapPathSyntax.Unix,
            TimeProvider = clock,
        });
        fs.AddFile("data/keep.txt", "keep");
        fs.AddFile("data/edit.txt", "old");
        fs.AddFile("data/gone.txt", "gone");
        fs.AddFile("data/old-name.txt", "moved");
        fs.AddFile("other/untouched.txt", "same");

        using Dir root = fs.OpenRoot();
        InMemorySnapshot before = fs.Snapshot();
        clock.Advance(TimeSpan.FromMinutes(1));

        _ = root.ReadAllText("data/keep.txt");
        Assert.True(before.Diff(fs.Snapshot()).IsEmpty);

        root.WriteAllText("data/edit.txt", "new");
        root.DeleteFile("data/gone.txt");
        root.Rename("data/old-name.txt", root, "data/new-name.txt");
        root.WriteAllText("data/added.txt", "added");

        InMemorySnapshotDiff diff = before.Diff(fs.Snapshot());

        Assert.Equal(["data/added.txt", "data/new-name.txt"], diff.Added);
        Assert.Equal(["data/gone.txt", "data/old-name.txt"], diff.Removed);
        Assert.Equal(["data", "data/edit.txt"], diff.Changed);
        Assert.False(diff.IsEmpty);
        Assert.Contains("data/gone.txt", diff.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_diff_ignores_the_access_time_and_nothing_else()
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Unix });
        fs.AddFile("a.txt", "x");
        fs.AddFile("b.txt", "x");
        fs.AddFile("c.txt", "x");
        InMemorySnapshot before = fs.Snapshot();

        fs.SetTimes("a.txt", lastAccess: new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        fs.SetUnixMode("b.txt", UnixFileMode.UserRead);
        fs.WriteAllText("c.txt", "y");

        InMemorySnapshotDiff diff = before.Diff(fs.Snapshot());
        Assert.Equal(["b.txt", "c.txt"], diff.Changed);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Equal("No changes.", before.Diff(before).ToString());
    }

    [Fact]
    public void Options_that_are_not_a_resolution_strategy_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new InMemoryFileSystem(new InMemoryFileSystemOptions { Resolution = ResolutionBackend.InMemory }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new InMemoryFileSystem(new InMemoryFileSystemOptions { PathSyntax = (CapPathSyntax)42 }));
    }

    // --- kinds other than files, directories and links -------------------------------------------

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_special_file_is_reported_as_its_kind_and_described_as_itself(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        fs.AddSpecialFile("dev/pipe", CapFileType.Fifo);
        fs.AddSpecialFile("dev/sock", CapFileType.Socket);
        fs.AddSpecialFile("dev/tty", CapFileType.CharDevice);
        fs.AddSpecialFile("dev/disk", CapFileType.BlockDevice);

        using Dir root = fs.OpenRoot();
        using Dir dev = root.OpenDir("dev");

        Dictionary<string, CapFileType> listed = dev.EnumerateEntries().ToDictionary(entry => entry.Name, entry => entry.Type);
        Assert.Equal(CapFileType.Fifo, listed["pipe"]);
        Assert.Equal(CapFileType.Socket, listed["sock"]);
        Assert.Equal(CapFileType.CharDevice, listed["tty"]);
        Assert.Equal(CapFileType.BlockDevice, listed["disk"]);
        Assert.Equal(CapFileType.Fifo, dev.GetMetadata("pipe").Type);
        Assert.Equal(CapFileType.Socket, fs.GetMetadata("dev/sock").Type);
        Assert.Equal(CapFileType.Fifo, fs.Snapshot()["dev/pipe"].Type);

        // A name like any other: renamed and removed as itself, and nothing beneath it.
        root.Rename("dev/pipe", root, "dev/renamed");
        Assert.Equal(CapFileType.Fifo, dev.GetMetadata("renamed").Type);
        Assert.Throws<CapIOException>(() => root.OpenFile("dev/renamed/x"));
        root.DeleteFile("dev/renamed");
        Assert.False(fs.Exists("dev/renamed"));
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_fifo_reads_as_empty_and_refuses_a_writer_and_the_rest_refuse_every_open(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        fs.AddSpecialFile("pipe", CapFileType.Fifo);
        fs.AddSpecialFile("sock", CapFileType.Socket);
        fs.AddSpecialFile("tty", CapFileType.CharDevice);
        fs.ReadOnly = true;

        using Dir root = fs.OpenRoot();

        Assert.Equal(string.Empty, root.ReadAllText("pipe"));
        Assert.Equal(
            CapErrorKind.NotSupported,
            Assert.Throws<CapIOException>(() => root.OpenFile("pipe", FileMode.Open, FileAccess.Write)).Kind);
        foreach (string name in new[] { "sock", "tty" })
        {
            Assert.Equal(CapErrorKind.NotSupported, Assert.Throws<CapIOException>(() => root.OpenFile(name)).Kind);
            Assert.Equal(CapErrorKind.NotSupported, Assert.Throws<CapIOException>(() => root.OpenAny(name)).Kind);
        }

        fs.ReadOnly = false;
        fs.SetUnreadable("pipe");
        Assert.Throws<UnauthorizedAccessException>(() => root.ReadAllText("pipe"));
    }

    [Fact]
    public void A_walk_lists_a_pipe_and_a_copy_refuses_or_skips_it()
    {
        InMemoryFileSystem fs = Resolutions.Create(ResolutionBackend.PortableWalk);
        fs.AddFile("source/kept.txt", "kept");
        fs.AddSpecialFile("source/pipe", CapFileType.Fifo);
        fs.AddDirectory("destination");

        using Dir source = fs.OpenRoot("source");
        using Dir destination = fs.OpenRoot("destination");

        Assert.Equal(
            [("kept.txt", CapFileType.File), ("pipe", CapFileType.Fifo)],
            source.Walk().Select(entry => (entry.Name, entry.Type)).Order());
        Assert.Throws<CapIOException>(
            () => source.CopyTo(destination, cancellationToken: TestContext.Current.CancellationToken));

        CopyReport report = source.CopyTo(
            destination,
            new CopyOptions { OtherKinds = CopyAction.Skip, Overwrite = true },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Skipped);
        Assert.Equal(["kept.txt"], fs.GetEntries("destination"));
    }

    [Fact]
    public void Only_the_four_special_kinds_can_be_planted_and_only_under_unix_rules()
    {
        InMemoryFileSystem unix = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Unix });
        foreach (CapFileType type in new[] { CapFileType.Unknown, CapFileType.File, CapFileType.Directory, CapFileType.Symlink, CapFileType.ReparsePoint, (CapFileType)99 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => unix.AddSpecialFile("x", type));
        }

        unix.AddSpecialFile("x", CapFileType.Fifo);
        Assert.Throws<IOException>(() => unix.AddSpecialFile("x", CapFileType.Socket));

        InMemoryFileSystem windows = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Windows });
        Assert.Throws<InvalidOperationException>(() => windows.AddSpecialFile("x", CapFileType.Fifo));
        Assert.Throws<InvalidOperationException>(() => unix.AddJunction("j", @"C:\elsewhere"));
        Assert.Throws<InvalidOperationException>(() => unix.AddReparsePoint("r", WciLinkTag));
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_junction_is_a_directory_link_that_is_never_followed_beneath_a_handle(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Windows(resolution);
        fs.AddFile("inside/file.txt", "x");
        fs.AddJunction("jn", @"C:\inside");

        using Dir root = fs.OpenRoot();

        CapMetadata metadata = root.GetMetadata("jn");
        Assert.Equal(CapFileType.Symlink, metadata.Type);
        Assert.Equal(FileAttributes.Directory | FileAttributes.ReparsePoint, AttributesOf(metadata) & (FileAttributes.Directory | FileAttributes.ReparsePoint));
        Assert.Equal(@"C:\inside", fs.GetSymbolicLinkTarget("jn"));
        Assert.Equal(@"C:\inside", root.ReadLink("jn"));

        // Rooted, so refused as an escape even though it names a directory inside.
        Assert.Throws<SandboxEscapeException>(() => root.ReadAllText(@"jn\file.txt"));
        Assert.Throws<SandboxEscapeException>(() => root.OpenDir("jn"));

        // Removed and replaced as the link it is, leaving what it named alone.
        root.WriteAllTextAtomic("jn", "published");
        Assert.Equal("published", fs.ReadAllText("jn"));
        Assert.Equal("x", fs.ReadAllText("inside/file.txt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("a\0b")]
    public void A_junction_target_that_is_not_rooted_is_refused(string target)
    {
        InMemoryFileSystem fs = Windows(ResolutionBackend.PortableWalk);
        Assert.Throws<ArgumentException>(() => fs.AddJunction("jn", target.Replace("\\0", "\0", StringComparison.Ordinal)));
        Assert.False(fs.Exists("jn"));
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_redirecting_reparse_point_is_refused_as_an_escape_and_named_as_itself(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Windows(resolution);
        fs.AddReparsePoint("container", WciLinkTag);
        fs.AddReparsePoint("share", DfsTag, isDirectory: true);

        using Dir root = fs.OpenRoot();

        Assert.Equal(CapFileType.ReparsePoint, root.GetMetadata("container").Type);
        Assert.Equal(CapFileType.ReparsePoint, root.EnumerateEntries().Single(entry => entry.Name == "share").Type);
        Assert.Throws<SandboxEscapeException>(() => root.ReadAllText("container"));
        Assert.Throws<SandboxEscapeException>(() => root.OpenAny("container"));
        Assert.Throws<SandboxEscapeException>(() => root.OpenDir("share"));
        Assert.Throws<SandboxEscapeException>(() => root.ReadAllText(@"share\x"));

        root.Rename("container", root, "moved");
        root.DeleteFile("moved");
        Assert.Equal(["share"], fs.GetEntries());
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_reparse_point_that_only_names_its_filter_is_the_file_or_directory_it_is(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Windows(resolution);
        fs.AddReparsePoint("compressed.exe", WofTag);
        fs.AddReparsePoint("placeholder", CloudTag, isDirectory: true);
        fs.AddFile("placeholder/inner.txt", "inner");

        using Dir root = fs.OpenRoot();

        Assert.Equal(CapFileType.File, root.GetMetadata("compressed.exe").Type);
        Assert.Equal(string.Empty, root.ReadAllText("compressed.exe"));
        Assert.Equal(CapFileType.Directory, root.GetMetadata("placeholder").Type);
        Assert.Equal("inner", root.ReadAllText(@"placeholder\inner.txt"));
        Assert.True((AttributesOf(root.GetMetadata("placeholder")) & FileAttributes.ReparsePoint) != 0);
    }

    [Fact]
    public void A_link_tag_is_refused_as_a_reparse_point()
    {
        InMemoryFileSystem fs = Windows(ResolutionBackend.PortableWalk);
        Assert.Throws<ArgumentException>(() => fs.AddReparsePoint("j", 0xA0000003));
        Assert.Throws<ArgumentException>(() => fs.AddReparsePoint("l", 0xA000000C));
        Assert.Empty(fs.GetEntries());
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_directory_that_hides_kinds_is_listed_by_looking_each_name_up(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        fs.AddFile("data/a.txt", "a");
        fs.AddDirectory("data/b");
        fs.SetDirectoryHidesEntryKinds("data");
        fs.AddSymbolicLink("data/c", "a.txt");

        using Dir data = fs.OpenRoot("data");

        // Each name is looked up as it is listed, an entry added after the setting included.
        Assert.Equal(
            [("a.txt", CapFileType.File), ("b", CapFileType.Directory), ("c", CapFileType.Symlink)],
            data.EnumerateEntries().Select(entry => (entry.Name, entry.Type)).Order());

        // One removed between the read and the lookup is reported as of no known kind.
        List<(string, CapFileType)> seen = [];
        foreach (DirEntry entry in data.EnumerateEntries())
        {
            seen.Add((entry.Name, entry.Type));
            foreach (string name in new[] { "a.txt", "b", "c" }.Where(name => name != entry.Name && fs.Exists("data/" + name)))
            {
                if (seen.Count == 1)
                {
                    if (name == "b")
                    {
                        fs.RemoveDirectory("data/b");
                    }
                    else
                    {
                        fs.RemoveFile("data/" + name);
                    }
                }
            }
        }

        Assert.Equal(3, seen.Count);
        Assert.Equal(2, seen.Count(pair => pair.Item2 == CapFileType.Unknown));

        fs.SetDirectoryHidesEntryKinds("data", hides: false);
        Assert.Throws<IOException>(() => fs.SetDirectoryHidesEntryKinds("missing"));
        fs.AddFile("data/file", "x");
        Assert.Throws<IOException>(() => fs.SetDirectoryHidesEntryKinds("data/file"));
        fs.SetDirectoryHidesEntryKinds("/");
    }

    /// <summary>A Windows Container Isolation link, which stands for another object.</summary>
    private const uint WciLinkTag = 0xA0000027;

    /// <summary>A distributed file system link, which redirects without the name-surrogate bit.</summary>
    private const uint DfsTag = 0x8000000A;

    /// <summary>A file compressed with <c>compact /exe</c>, served by its filter.</summary>
    private const uint WofTag = 0x80000017;

    /// <summary>A cloud files placeholder, served by its filter.</summary>
    private const uint CloudTag = 0x9000001A;

    private static FileAttributes AttributesOf(CapMetadata metadata)
    {
        Assert.True(metadata.Permissions.TryGetWindowsAttributes(out FileAttributes attributes));
        return attributes;
    }

    private static InMemoryFileSystem Windows(ResolutionBackend resolution) =>
        new(new InMemoryFileSystemOptions { Resolution = resolution, PathSyntax = CapPathSyntax.Windows });
}
