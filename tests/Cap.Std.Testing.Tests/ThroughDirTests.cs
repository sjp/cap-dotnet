using System.Text;
using Cap.Fs.Ext;
using Cap.Primitives;
using Microsoft.Extensions.Time.Testing;

namespace Cap.Std.Testing.Tests;

/// <summary>
/// What code under test does through a <see cref="Dir"/> on an in-memory filesystem: read,
/// write, list, rename, remove, describe, link and make scratch space, down both resolution
/// strategies.
/// </summary>
public sealed class ThroughDirTests
{
    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_root_is_a_real_handle_on_the_tree(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        fs.AddFile("config/app.json", """{ "x": 1 }""");

        using Dir root = fs.OpenRoot();
        using Dir config = root.OpenDir("config");

        Assert.Equal(ResolutionBackend.InMemory, root.Backend);
        Assert.Equal(ResolutionBackend.InMemory, config.Backend);
        Assert.Equal(SymlinkPolicy.FollowWithinSandbox, root.SymlinkPolicy);
        Assert.Equal("""{ "x": 1 }""", config.ReadAllText("app.json"));
        Assert.Throws<NotSupportedException>(() => root.UnsafeGetHandle());
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void What_is_written_through_a_handle_is_in_the_tree(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        fs.AddDirectory("out");

        using (Dir root = fs.OpenRoot())
        {
            root.WriteAllBytes("out/report.txt", Encoding.UTF8.GetBytes("done"));
            using Dir nested = root.CreateDir("out/nested");
            nested.WriteAllBytes("deep.bin", [9, 8, 7]);
        }

        Assert.Equal("done", fs.ReadAllText("out/report.txt"));
        Assert.Equal([9, 8, 7], fs.ReadAllBytes("out/nested/deep.bin"));
        Assert.Equal(["nested", "report.txt"], fs.GetEntries("out"));
        Assert.Equal(7, fs.UsedBytes);
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_file_handle_reads_writes_and_resizes_at_offsets(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        using Dir root = fs.OpenRoot();

        using (CapFile file = root.OpenFile("data.bin", FileMode.CreateNew, FileAccess.ReadWrite))
        {
            file.Write([1, 2, 3, 4], 0);
            file.Write([5], 6);
            Assert.Equal(7, file.Length);

            byte[] buffer = new byte[7];
            Assert.Equal(7, file.Read(buffer, 0));
            Assert.Equal([1, 2, 3, 4, 0, 0, 5], buffer);

            file.SetLength(2);
            Assert.Equal(2, file.GetMetadata().Length);
        }

        using (CapFile file = root.OpenFile("data.bin", FileMode.Open, FileAccess.ReadWrite))
        using (Stream stream = file.AsStream())
        {
            stream.Seek(0, SeekOrigin.End);
            stream.Write([6, 7]);
            stream.Position = 0;
            byte[] all = new byte[4];
            stream.ReadExactly(all);
            Assert.Equal([1, 2, 6, 7], all);
        }

        using (CapFile appending = root.OpenFile("data.bin", FileMode.Append, FileAccess.Write))
        {
            appending.Write([8], 0);
        }

        Assert.Equal([1, 2, 6, 7, 8], fs.ReadAllBytes("data.bin"));
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public async Task A_file_handle_reads_and_writes_asynchronously(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        using Dir root = fs.OpenRoot();

        await root.WriteAllBytesAsync("async.txt", Encoding.UTF8.GetBytes("later"), TestContext.Current.CancellationToken);

        Assert.Equal("later", await root.ReadAllTextAsync("async.txt", TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void Entries_are_listed_with_their_kind(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        fs.AddFile("dir/file.txt");
        fs.AddDirectory("dir/sub");
        fs.AddSymbolicLink("dir/link", "file.txt");

        using Dir root = fs.OpenRoot();
        using Dir dir = root.OpenDir("dir");
        Dictionary<string, CapFileType> entries = dir.EnumerateEntries().ToDictionary(entry => entry.Name, entry => entry.Type);

        Assert.Equal(CapFileType.File, entries["file.txt"]);
        Assert.Equal(CapFileType.Directory, entries["sub"]);
        Assert.Equal(CapFileType.Symlink, entries["link"]);
        Assert.Equal(3, entries.Count);
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_rename_moves_the_object_and_keeps_its_identity(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        fs.AddFile("a/one.txt", "1");
        fs.AddFile("b/two.txt", "2");

        using Dir root = fs.OpenRoot();
        CapFileId before = root.GetMetadata("a/one.txt").FileId;

        root.Rename("a/one.txt", root, "b/moved.txt");

        Assert.False(fs.Exists("a/one.txt"));
        Assert.Equal("1", fs.ReadAllText("b/moved.txt"));
        Assert.Equal(before, root.GetMetadata("b/moved.txt").FileId);

        CapIOException taken = Assert.ThrowsAny<CapIOException>(() => root.Rename("b/moved.txt", root, "b/two.txt"));
        Assert.Equal(CapErrorKind.AlreadyExists, taken.Kind);

        root.Rename("b/moved.txt", root, "b/two.txt", replaceExisting: true);
        Assert.Equal("1", fs.ReadAllText("b/two.txt"));
        Assert.Equal(["two.txt"], fs.GetEntries("b"));

        CapIOException beneathItself = Assert.ThrowsAny<CapIOException>(() => root.Rename("b", root, "b/inner"));
        Assert.Equal(CapErrorKind.InvalidArgument, beneathItself.Kind);
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_directory_that_is_not_empty_is_not_removed(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        fs.AddFile("full/file.txt", "x");
        fs.AddDirectory("empty");

        using Dir root = fs.OpenRoot();

        CapIOException notEmpty = Assert.ThrowsAny<CapIOException>(() => root.DeleteDir("full"));
        Assert.Equal(CapErrorKind.NotEmpty, notEmpty.Kind);

        root.DeleteFile("full/file.txt");
        root.DeleteDir("full");
        root.DeleteDir("empty");

        Assert.Empty(fs.GetEntries());
        Assert.Throws<FileNotFoundException>(() => root.DeleteFile("missing"));
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_removed_file_stays_usable_through_an_open_handle(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        fs.AddFile("doomed.txt", "still here");

        using Dir root = fs.OpenRoot();
        using CapFile file = root.OpenFile("doomed.txt", FileMode.Open, FileAccess.Read);
        root.DeleteFile("doomed.txt");

        byte[] buffer = new byte[10];
        Assert.Equal(10, file.Read(buffer, 0));
        Assert.Equal("still here", Encoding.UTF8.GetString(buffer));
        Assert.Equal(0, file.GetMetadata().LinkCount);
        Assert.Equal(0, fs.UsedBytes);
    }

    [Fact]
    public void Times_come_from_the_given_clock_and_a_read_changes_none_of_them()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { TimeProvider = clock });

        using Dir root = fs.OpenRoot();
        root.WriteAllBytes("stamped.txt", [1]);
        DateTimeOffset created = clock.GetUtcNow();

        clock.Advance(TimeSpan.FromHours(1));
        _ = root.ReadAllBytes("stamped.txt");
        CapMetadata afterRead = root.GetMetadata("stamped.txt");

        Assert.Equal(created, afterRead.CreationTime);
        Assert.Equal(created, afterRead.LastWriteTime);
        Assert.Equal(created, afterRead.LastAccessTime);

        root.WriteAllBytes("stamped.txt", [2]);
        CapMetadata afterWrite = root.GetMetadata("stamped.txt");

        Assert.Equal(clock.GetUtcNow(), afterWrite.LastWriteTime);
        Assert.Equal(clock.GetUtcNow(), afterWrite.ChangeTime);
        Assert.Equal(created, afterWrite.CreationTime);
    }

    [Fact]
    public void Without_a_clock_every_time_is_the_same_instant()
    {
        InMemoryFileSystem fs = new();
        using Dir root = fs.OpenRoot();
        root.WriteAllBytes("a.txt", [1]);

        Assert.Equal(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), root.GetMetadata("a.txt").LastWriteTime);
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void Links_are_made_read_and_followed_within_the_tree(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        fs.AddFile("real/file.txt", "through the link");

        using Dir root = fs.OpenRoot();
        root.CreateDirSymlink("alias", "real");
        root.CreateSymlink("real/shortcut", "file.txt");
        root.CreateHardLink("real/file.txt", root, "second-name.txt");

        Assert.Equal("real", root.ReadLink("alias"));
        Assert.Equal("real", fs.GetSymbolicLinkTarget("alias"));
        Assert.Equal("through the link", root.ReadAllText("alias/shortcut"));
        Assert.Equal(CapFileType.Symlink, root.GetMetadata("alias").Type);
        Assert.Equal(CapFileType.Directory, root.GetMetadata("alias", followLink: true).Type);
        Assert.Equal(2, root.GetMetadata("second-name.txt").LinkCount);

        using Dir denying = fs.OpenRoot(SymlinkPolicy.Deny);
        Assert.ThrowsAny<IOException>(() => denying.ReadAllText("alias/shortcut"));
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void Scratch_directories_and_files_work_beneath_a_root(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        using Dir root = fs.OpenRoot();

        string name;
        using (CapTempDir temp = CapTempDir.NewIn(root))
        {
            name = temp.Name;
            temp.Directory.WriteAllBytes("scratch.bin", [1, 2]);
            Assert.True(fs.Exists($"{name}/scratch.bin"));
        }

        Assert.False(fs.Exists(name));

        using (CapTempFile named = CapTempFile.New(root))
        {
            Assert.True(named.HasName);
            named.File.Write([3], 0);
            Assert.True(fs.Exists(named.Name!));
        }

        Assert.Empty(fs.GetEntries());

        using CapTempFile anonymous = CapTempFile.NewAnonymous(root);
        anonymous.File.Write([4, 5], 0);
        Assert.Equal(2, anonymous.File.Length);
        Assert.Empty(fs.GetEntries());
    }

    [Fact]
    public void Several_roots_may_be_open_at_once_on_different_directories()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("tenants/a/data.txt", "a");
        fs.AddFile("tenants/b/data.txt", "b");

        using Dir a = fs.OpenRoot("tenants/a");
        using Dir b = fs.OpenRoot("tenants/b");
        using Dir whole = fs.OpenRoot();

        a.WriteAllBytes("new.txt", [1]);

        Assert.Equal("a", a.ReadAllText("data.txt"));
        Assert.Equal("b", b.ReadAllText("data.txt"));
        Assert.True(whole.Exists("tenants/a/new.txt"));
        Assert.False(b.Exists("new.txt"));
        Assert.Throws<SandboxEscapeException>(() => a.ReadAllText("../b/data.txt"));
        Assert.Throws<DirectoryNotFoundException>(() => fs.OpenRoot("tenants/c"));
        Assert.Throws<DirectoryNotFoundException>(() => fs.OpenRoot("tenants/a/data.txt"));
    }

    [Fact]
    public void A_handle_on_one_filesystem_cannot_be_combined_with_one_on_another()
    {
        InMemoryFileSystem first = new();
        InMemoryFileSystem second = new();
        first.AddFile("file.txt", "x");

        using Dir one = first.OpenRoot();
        using Dir two = second.OpenRoot();

        CapIOException crossed = Assert.ThrowsAny<CapIOException>(() => one.Rename("file.txt", two, "file.txt"));
        Assert.Equal(CapErrorKind.CrossDevice, crossed.Kind);
        Assert.NotEqual(one.GetMetadata().FileId, two.GetMetadata().FileId);
    }

    [Fact]
    public void The_convenience_layer_works_over_an_in_memory_tree()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("src/a.txt", "a");
        fs.AddFile("src/nested/b.txt", "b");
        fs.AddDirectory("dst");

        using Dir root = fs.OpenRoot();
        using Dir source = root.OpenDir("src");
        using Dir destination = root.OpenDir("dst");

        _ = source.CopyTo(destination);
        root.WriteAllBytesAtomic("dst/atomic.txt", [1]);
        string[] found = [.. root.Glob("src/**/*.txt").Select(entry => entry.Name).Order(StringComparer.Ordinal)];

        Assert.Equal("b", fs.ReadAllText("dst/nested/b.txt"));
        Assert.Equal([1], fs.ReadAllBytes("dst/atomic.txt"));
        Assert.Equal(["a.txt", "b.txt"], found);
    }
}
