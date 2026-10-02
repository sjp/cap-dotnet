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
    public void A_gap_written_after_truncating_a_large_file_to_nothing_reads_as_zeroes(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        using Dir root = fs.OpenRoot();
        byte[] large = new byte[1024 * 1024];
        Array.Fill(large, (byte)0xFF);

        using CapFile file = root.OpenFile("large.bin", FileMode.CreateNew, FileAccess.ReadWrite);
        file.Write(large, 0);
        file.SetLength(0);
        file.Write([1, 2, 3], 10);

        byte[] buffer = new byte[13];
        Assert.Equal(13, file.Read(buffer, 0));
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3], buffer);
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void Extending_a_large_file_shrunk_to_a_few_bytes_exposes_zeroes_past_them(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        using Dir root = fs.OpenRoot();
        byte[] large = new byte[1024 * 1024];
        Array.Fill(large, (byte)0xFF);

        using CapFile file = root.OpenFile("large.bin", FileMode.CreateNew, FileAccess.ReadWrite);
        file.Write(large, 0);
        file.SetLength(3);
        file.SetLength(8192);

        byte[] buffer = new byte[8192];
        Assert.Equal(8192, file.Read(buffer, 0));
        Assert.Equal([0xFF, 0xFF, 0xFF], buffer[..3]);
        Assert.All(buffer[3..], b => Assert.Equal(0, b));
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

    /// <summary>
    /// Linux refuses a rename that may not replace onto a name that is there, even when it names
    /// the object being moved; Windows treats the move as done. Allowed to replace, both do nothing.
    /// </summary>
    [Theory]
    [InlineData(CapPathSyntax.Unix, "plain")]
    [InlineData(CapPathSyntax.Unix, "twin")]
    [InlineData(CapPathSyntax.Windows, "plain")]
    public void A_rename_onto_the_same_object_is_refused_only_under_Unix_rules_and_only_without_replacing(
        CapPathSyntax syntax, string to)
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = syntax });
        fs.AddFile("plain", "p");
        fs.AddHardLink("twin", "plain");

        using Dir root = fs.OpenRoot();
        if (syntax == CapPathSyntax.Unix)
        {
            CapIOException taken = Assert.ThrowsAny<CapIOException>(() => root.Rename("plain", root, to));
            Assert.Equal(CapErrorKind.AlreadyExists, taken.Kind);
        }
        else
        {
            root.Rename("plain", root, to);
        }

        root.Rename("plain", root, to, replaceExisting: true);

        Assert.Equal(["plain", "twin"], fs.GetEntries().Order(StringComparer.Ordinal));
        Assert.Equal("p", fs.ReadAllText(to));
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
    public void A_write_whose_end_is_past_the_largest_offset_is_refused_as_too_large(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        fs.AddFile("big.bin", [1, 2]);

        using Dir root = fs.OpenRoot();
        using CapFile file = root.OpenFile("big.bin", FileMode.Open, FileAccess.ReadWrite);

        Assert.Equal(CapErrorKind.Other, CapIOException.KindOf(Assert.ThrowsAny<IOException>(() => file.Write([1, 2, 3, 4], long.MaxValue - 2))));

        using (Stream stream = file.AsStream())
        {
            stream.Position = long.MaxValue - 2;
            Assert.Equal(CapErrorKind.Other, CapIOException.KindOf(Assert.ThrowsAny<IOException>(() => stream.Write([1, 2, 3, 4]))));
        }

        Assert.Equal(2, file.Length);
        Assert.Equal(2, fs.UsedBytes);
        Assert.Equal([1, 2], fs.ReadAllBytes("big.bin"));
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
        Assert.Equal(10, fs.UsedBytes);

        file.Dispose();
        Assert.Equal(0, fs.UsedBytes);
    }

    /// <summary>
    /// Every handle counts from its open until it is disposed, a stream's copy of a file's
    /// handle included, so a test can show that nothing was left open.
    /// </summary>
    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void Open_handles_are_counted_until_each_is_disposed(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        fs.AddFile("sub/file.txt", "x");
        Assert.Equal(0, fs.OpenHandleCount);

        Dir root = fs.OpenRoot();
        Dir sub = root.OpenDir("sub");
        CapFile file = sub.OpenFile("file.txt", FileMode.Open, FileAccess.Read);
        Stream stream = file.AsStream();
        Assert.Equal(4, fs.OpenHandleCount);

        _ = root.ReadAllBytes("sub/file.txt");
        _ = root.EnumerateEntries().Count();
        Assert.Equal(4, fs.OpenHandleCount);

        file.Dispose();
        Assert.Equal(3, fs.OpenHandleCount);
        stream.Dispose();
        Assert.Equal(2, fs.OpenHandleCount);
        sub.Dispose();
        root.Dispose();
        Assert.Equal(0, fs.OpenHandleCount);
    }

    /// <summary>
    /// Two handles on a file whose name is removed: disposing one leaves the file readable
    /// through the other, and disposing that one leaves nothing open.
    /// </summary>
    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_removed_file_is_counted_open_until_its_last_handle_is_disposed(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        fs.AddFile("doomed.txt", "still here");

        using (Dir root = fs.OpenRoot())
        {
            CapFile first = root.OpenFile("doomed.txt", FileMode.Open, FileAccess.Read);
            CapFile second = root.OpenFile("doomed.txt", FileMode.Open, FileAccess.Read);
            root.DeleteFile("doomed.txt");
            Assert.Equal(3, fs.OpenHandleCount);

            first.Dispose();
            Assert.Equal(2, fs.OpenHandleCount);
            byte[] buffer = new byte[10];
            Assert.Equal(10, second.Read(buffer, 0));
            Assert.Equal("still here", Encoding.UTF8.GetString(buffer));

            second.Dispose();
            Assert.Equal(1, fs.OpenHandleCount);
        }

        Assert.Equal(0, fs.OpenHandleCount);
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

    /// <summary>
    /// Linux's <c>utimensat</c> with both times omitted returns before touching anything, so
    /// the change time stays put (checked on Linux arm64 with a probe that set neither time on
    /// a file a second after creating it). Setting either time is a change and stamps it.
    /// </summary>
    [Fact]
    public void Setting_times_that_leaves_both_as_they_are_changes_no_time()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { TimeProvider = clock });
        fs.AddFile("dir/f.txt", "x");
        DateTimeOffset created = clock.GetUtcNow();

        using Dir root = fs.OpenRoot();
        using Dir dir = root.OpenDir("dir");
        clock.Advance(TimeSpan.FromMinutes(1));

        root.SetTimes("dir/f.txt");
        dir.SetTimes();
        using (CapFile file = dir.OpenFile("f.txt", FileMode.Open, FileAccess.Write))
        {
            file.SetTimes();
        }

        foreach (CapMetadata untouched in new[] { root.GetMetadata("dir/f.txt"), root.GetMetadata("dir") })
        {
            Assert.Equal(created, untouched.LastAccessTime);
            Assert.Equal(created, untouched.LastWriteTime);
            Assert.Equal(created, untouched.ChangeTime);
        }

        root.SetTimes("dir/f.txt", lastWrite: CapFileTime.Now);
        CapMetadata set = root.GetMetadata("dir/f.txt");

        Assert.Equal(created, set.LastAccessTime);
        Assert.Equal(clock.GetUtcNow(), set.LastWriteTime);
        Assert.Equal(clock.GetUtcNow(), set.ChangeTime);
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
    public void An_exclusive_creation_on_a_link_under_deny_is_refused_as_taken(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        fs.AddFile("target", "t");
        fs.AddSymbolicLink("link", "target");
        fs.AddSymbolicLink("dangling", "nowhere");

        using Dir root = fs.OpenRoot(SymlinkPolicy.Deny);

        Assert.Equal(CapErrorKind.AlreadyExists, CapIOException.KindOf(Assert.ThrowsAny<IOException>(() => root.CreateNewFile("link"))));
        Assert.Equal(CapErrorKind.AlreadyExists, CapIOException.KindOf(Assert.ThrowsAny<IOException>(() => root.CreateNewFile("dangling"))));
        Assert.Equal(["dangling", "link", "target"], fs.GetEntries());
        Assert.Equal("t", fs.ReadAllText("target"));
        Assert.False(fs.Exists("nowhere"));
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_name_longer_than_linux_stores_in_bytes_is_refused_by_every_single_name_operation(ResolutionBackend resolution)
    {
        // 200 characters, 400 UTF-8 bytes: within the parser's character limit, past NAME_MAX.
        string name = new('é', 200);
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        fs.AddFile("existing", "x");
        using Dir root = fs.OpenRoot();

        Assert.Equal(CapErrorKind.NameTooLong, CapIOException.KindOf(Assert.Throws<PathTooLongException>(() => root.WriteAllBytes(name, [1]))));
        Assert.Equal(CapErrorKind.NameTooLong, CapIOException.KindOf(Assert.Throws<PathTooLongException>(() => root.CreateDir(name))));
        Assert.Equal(CapErrorKind.NameTooLong, CapIOException.KindOf(Assert.Throws<PathTooLongException>(() => root.CreateSymlink(name, "existing"))));
        Assert.Equal(CapErrorKind.NameTooLong, CapIOException.KindOf(Assert.Throws<PathTooLongException>(() => root.CreateHardLink("existing", root, name))));
        Assert.Equal(CapErrorKind.NameTooLong, CapIOException.KindOf(Assert.Throws<PathTooLongException>(() => root.Rename("existing", root, name))));
        Assert.Equal(CapErrorKind.NameTooLong, CapIOException.KindOf(Assert.Throws<PathTooLongException>(() => root.Rename(name, root, "moved"))));
        Assert.Equal(["existing"], fs.GetEntries());

        // 255 bytes exactly is still a name.
        string longest = new string('é', 127) + "a";
        root.WriteAllBytes(longest, [1]);
        Assert.True(fs.Exists(longest));
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void Under_windows_rules_a_name_is_measured_in_utf16_units(ResolutionBackend resolution)
    {
        string name = new('é', 200);
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { Resolution = resolution, PathSyntax = CapPathSyntax.Windows });
        using Dir root = fs.OpenRoot();

        root.WriteAllBytes(name, [1]);
        root.CreateDir(name + "d").Dispose();
        root.Rename(name, root, name + "r");

        Assert.Equal([name + "d", name + "r"], fs.GetEntries());
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

        _ = source.CopyTo(destination, cancellationToken: TestContext.Current.CancellationToken);
        root.WriteAllBytesAtomic("dst/atomic.txt", [1]);
        string[] found = [.. root.Glob("src/**/*.txt").Select(entry => entry.Name).Order(StringComparer.Ordinal)];

        Assert.Equal("b", fs.ReadAllText("dst/nested/b.txt"));
        Assert.Equal([1], fs.ReadAllBytes("dst/atomic.txt"));
        Assert.Equal(["a.txt", "b.txt"], found);
    }
}
