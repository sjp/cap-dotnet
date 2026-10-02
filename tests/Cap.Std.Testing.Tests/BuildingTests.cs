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
}
