using System.IO.Abstractions;
using System.Text;

namespace Cap.IO.Abstractions.Tests;

/// <summary>
/// What code written against <see cref="IFileSystem"/> relies on, run against the adapter on
/// disk, the adapter in memory, and <c>MockFileSystem</c>.
/// </summary>
/// <remarks>
/// Each test states what <c>System.IO</c> does. Where <c>MockFileSystem</c> does something
/// else, the test says what with <see cref="MockDiffers"/> and is skipped for the mock, so the
/// list of such places is the skip list of <see cref="MockBehaviourTests"/>, and the package
/// documentation repeats it. Behaviour in which the adapter parts from <c>System.IO</c> on
/// purpose is tested in <see cref="DifferenceTests"/> instead.
/// </remarks>
public abstract class FileSystemBehaviourTests : IDisposable
{
    private readonly IFileSystemFixture _fixture;

    protected FileSystemBehaviourTests(IFileSystemFixture fixture)
    {
        _fixture = fixture;
        Fs = fixture.FileSystem;
        Root = Fs.Directory.GetCurrentDirectory();
    }

    protected IFileSystem Fs { get; }

    /// <summary>Where each test builds: the file system's starting current directory.</summary>
    protected string Root { get; }

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    protected string P(params string[] names) => Fs.Path.Combine([Root, .. names]);

    /// <summary>Skips the test for <c>MockFileSystem</c>, which does not do what <c>System.IO</c> does here.</summary>
    protected void MockDiffers(string how) =>
        Assert.SkipUnless(_fixture.Confined, $"MockFileSystem differs from System.IO here: {how}");

    [Fact]
    public void Text_written_is_read_back()
    {
        Fs.File.WriteAllText(P("a.txt"), "hello");

        Assert.Equal("hello", Fs.File.ReadAllText(P("a.txt")));
        Assert.True(Fs.File.Exists(P("a.txt")));
        Assert.False(Fs.Directory.Exists(P("a.txt")));
    }

    [Fact]
    public void Bytes_written_are_read_back()
    {
        Fs.File.WriteAllBytes(P("a.bin"), [1, 2, 3]);

        Assert.Equal([1, 2, 3], Fs.File.ReadAllBytes(P("a.bin")));
    }

    [Fact]
    public void Text_written_without_an_encoding_has_no_byte_order_mark()
    {
        Fs.File.WriteAllText(P("a.txt"), "é");

        Assert.Equal(Encoding.UTF8.GetBytes("é"), Fs.File.ReadAllBytes(P("a.txt")));
    }

    [Fact]
    public async Task Text_written_asynchronously_is_read_back()
    {
        await Fs.File.WriteAllTextAsync(P("a.txt"), "hello", TestContext.Current.CancellationToken);

        Assert.Equal("hello", await Fs.File.ReadAllTextAsync(P("a.txt"), TestContext.Current.CancellationToken));
        byte[] expected = "hello"u8.ToArray();
        Assert.Equal(expected, await Fs.File.ReadAllBytesAsync(P("a.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Appending_adds_to_the_end()
    {
        Fs.File.WriteAllText(P("a.txt"), "one");
        Fs.File.AppendAllText(P("a.txt"), "two");
        Fs.File.AppendAllLines(P("a.txt"), ["", "three"]);

        Assert.Equal(["onetwo", "three"], Fs.File.ReadAllLines(P("a.txt")));
    }

    [Fact]
    public void Appending_creates_a_missing_file()
    {
        Fs.File.AppendAllText(P("a.txt"), "one");

        Assert.Equal("one", Fs.File.ReadAllText(P("a.txt")));
    }

    [Fact]
    public void Lines_are_read_lazily_and_in_order()
    {
        Fs.File.WriteAllLines(P("a.txt"), ["x", "y", "z"]);

        Assert.Equal(["x", "y", "z"], Fs.File.ReadLines(P("a.txt")));
    }

    [Fact]
    public void Reading_a_missing_file_throws_file_not_found()
    {
        Assert.Throws<FileNotFoundException>(() => Fs.File.ReadAllText(P("missing.txt")));
    }

    [Fact]
    public void Reading_beneath_a_missing_directory_throws_directory_not_found()
    {
        MockDiffers("it throws FileNotFoundException for a file beneath a missing directory.");
        Assert.Throws<DirectoryNotFoundException>(() => Fs.File.ReadAllText(P("missing", "a.txt")));
    }

    [Fact]
    public void Opening_a_directory_as_a_file_is_refused_as_access()
    {
        MockDiffers("reading a directory as a file does not throw.");
        Fs.Directory.CreateDirectory(P("d"));

        Assert.Throws<UnauthorizedAccessException>(() => Fs.File.ReadAllText(P("d")));
    }

    [Fact]
    public void A_chain_of_directories_is_created()
    {
        Fs.Directory.CreateDirectory(P("a", "b", "c"));
        Fs.Directory.CreateDirectory(P("a", "b", "c"));

        Assert.True(Fs.Directory.Exists(P("a")));
        Assert.True(Fs.Directory.Exists(P("a", "b", "c")));
        Assert.False(Fs.File.Exists(P("a", "b")));
    }

    [Fact]
    public void Creating_a_directory_where_a_file_is_fails()
    {
        Fs.File.WriteAllText(P("a"), "file");

        Assert.ThrowsAny<IOException>(() => Fs.Directory.CreateDirectory(P("a")));
    }

    [Fact]
    public void Copying_copies_and_refuses_an_existing_destination()
    {
        Fs.File.WriteAllText(P("a.txt"), "one");
        Fs.File.WriteAllText(P("b.txt"), "two");

        Fs.File.Copy(P("a.txt"), P("c.txt"));
        Assert.ThrowsAny<IOException>(() => Fs.File.Copy(P("a.txt"), P("b.txt")));
        Fs.File.Copy(P("a.txt"), P("b.txt"), overwrite: true);

        Assert.Equal("one", Fs.File.ReadAllText(P("c.txt")));
        Assert.Equal("one", Fs.File.ReadAllText(P("b.txt")));
        Assert.Equal("one", Fs.File.ReadAllText(P("a.txt")));
    }

    [Fact]
    public void Copying_a_file_onto_itself_leaves_it_intact()
    {
        MockDiffers("copying a file onto itself with overwrite throws NullReferenceException.");
        Fs.File.WriteAllText(P("a.txt"), "one");

        Assert.ThrowsAny<IOException>(() => Fs.File.Copy(P("a.txt"), P("a.txt"), overwrite: true));
        Assert.Equal("one", Fs.File.ReadAllText(P("a.txt")));
    }

    [Fact]
    public void Moving_a_file_renames_it()
    {
        Fs.File.WriteAllText(P("a.txt"), "one");
        Fs.File.WriteAllText(P("b.txt"), "two");

        Fs.File.Move(P("a.txt"), P("c.txt"));
        Assert.ThrowsAny<IOException>(() => Fs.File.Move(P("c.txt"), P("b.txt")));
        Fs.File.Move(P("c.txt"), P("b.txt"), overwrite: true);

        Assert.False(Fs.File.Exists(P("a.txt")));
        Assert.False(Fs.File.Exists(P("c.txt")));
        Assert.Equal("one", Fs.File.ReadAllText(P("b.txt")));
    }

    [Fact]
    public void Moving_a_missing_file_throws_file_not_found()
    {
        Assert.Throws<FileNotFoundException>(() => Fs.File.Move(P("a.txt"), P("b.txt")));
    }

    [Fact]
    public void Replacing_keeps_a_backup()
    {
        Fs.File.WriteAllText(P("new.txt"), "new");
        Fs.File.WriteAllText(P("live.txt"), "old");

        Fs.File.Replace(P("new.txt"), P("live.txt"), P("backup.txt"));

        Assert.False(Fs.File.Exists(P("new.txt")));
        Assert.Equal("new", Fs.File.ReadAllText(P("live.txt")));
        Assert.Equal("old", Fs.File.ReadAllText(P("backup.txt")));
    }

    [Fact]
    public void Deleting_a_missing_file_is_not_an_error()
    {
        Fs.File.Delete(P("missing.txt"));
    }

    [Fact]
    public void Deleting_a_file_removes_it()
    {
        Fs.File.WriteAllText(P("a.txt"), "one");

        Fs.File.Delete(P("a.txt"));

        Assert.False(Fs.File.Exists(P("a.txt")));
    }

    [Fact]
    public void A_directory_with_entries_is_removed_only_recursively()
    {
        Fs.Directory.CreateDirectory(P("d", "e"));
        Fs.File.WriteAllText(P("d", "e", "a.txt"), "one");

        Assert.ThrowsAny<IOException>(() => Fs.Directory.Delete(P("d")));
        Fs.Directory.Delete(P("d"), recursive: true);

        Assert.False(Fs.Directory.Exists(P("d")));
    }

    [Fact]
    public void Removing_a_missing_directory_throws_directory_not_found()
    {
        Assert.Throws<DirectoryNotFoundException>(() => Fs.Directory.Delete(P("missing")));
    }

    [Fact]
    public void Moving_a_directory_moves_its_contents()
    {
        Fs.Directory.CreateDirectory(P("d"));
        Fs.File.WriteAllText(P("d", "a.txt"), "one");

        Fs.Directory.Move(P("d"), P("e"));

        Assert.False(Fs.Directory.Exists(P("d")));
        Assert.Equal("one", Fs.File.ReadAllText(P("e", "a.txt")));
    }

    [Fact]
    public void Files_are_found_by_pattern()
    {
        Fs.Directory.CreateDirectory(P("d", "sub"));
        Fs.File.WriteAllText(P("d", "a.txt"), string.Empty);
        Fs.File.WriteAllText(P("d", "b.log"), string.Empty);
        Fs.File.WriteAllText(P("d", "sub", "c.txt"), string.Empty);

        Assert.Equal(["a.txt"], Names(Fs.Directory.GetFiles(P("d"), "*.txt")));
        Assert.Equal(["a.txt", "c.txt"], Names(Fs.Directory.GetFiles(P("d"), "*.txt", SearchOption.AllDirectories)));
        Assert.Equal(["a.txt", "b.log"], Names(Fs.Directory.GetFiles(P("d"))));
        Assert.Equal(["sub"], Names(Fs.Directory.GetDirectories(P("d"))));
        Assert.Equal(["a.txt", "b.log", "sub"], Names(Fs.Directory.GetFileSystemEntries(P("d"))));
    }

    [Fact]
    public void Enumerated_paths_lead_back_to_the_entries()
    {
        Fs.Directory.CreateDirectory(P("d", "sub"));
        Fs.File.WriteAllText(P("d", "sub", "c.txt"), "found");

        string found = Assert.Single(Fs.Directory.EnumerateFiles(P("d"), "*", SearchOption.AllDirectories));

        Assert.Equal("found", Fs.File.ReadAllText(found));
    }

    [Fact]
    public void Enumerating_a_missing_directory_throws_directory_not_found()
    {
        Assert.Throws<DirectoryNotFoundException>(() => Fs.Directory.GetFiles(P("missing")));
    }

    [Fact]
    public void A_relative_path_is_taken_against_the_current_directory()
    {
        Fs.Directory.CreateDirectory(P("d"));
        Fs.Directory.SetCurrentDirectory(P("d"));

        Fs.File.WriteAllText("a.txt", "one");

        Assert.Equal("one", Fs.File.ReadAllText(P("d", "a.txt")));
        Assert.Equal(P("d"), Fs.Directory.GetCurrentDirectory());
        Assert.Equal(P("d", "a.txt"), Fs.Path.GetFullPath("a.txt"));
    }

    [Fact]
    public void Setting_a_missing_current_directory_throws_directory_not_found()
    {
        MockDiffers("a current directory that does not exist is accepted.");
        Assert.Throws<DirectoryNotFoundException>(() => Fs.Directory.SetCurrentDirectory(P("missing")));
    }

    [Fact]
    public void A_full_path_is_folded()
    {
        Assert.Equal(P("b", "c"), Fs.Path.GetFullPath(P("a", "..", "b", ".", "c")));
    }

    [Fact]
    public void File_info_describes_a_file()
    {
        Fs.Directory.CreateDirectory(P("d"));
        Fs.File.WriteAllText(P("d", "a.txt"), "hello");

        IFileInfo info = Fs.FileInfo.New(P("d", "a.txt"));

        Assert.True(info.Exists);
        Assert.Equal(5, info.Length);
        Assert.Equal("a.txt", info.Name);
        Assert.Equal(".txt", info.Extension);
        Assert.Equal(P("d", "a.txt"), info.FullName);
        Assert.Equal(P("d"), info.DirectoryName);
        Assert.Equal("d", info.Directory!.Name);
        Assert.False(info.Attributes.HasFlag(FileAttributes.Directory));
    }

    [Fact]
    public void File_info_is_refreshed_on_request()
    {
        IFileInfo info = Fs.FileInfo.New(P("a.txt"));
        Assert.False(info.Exists);

        Fs.File.WriteAllText(P("a.txt"), "hello");
        info.Refresh();

        Assert.True(info.Exists);
    }

    [Fact]
    public void Moving_through_file_info_points_it_at_the_new_name()
    {
        Fs.File.WriteAllText(P("a.txt"), "hello");
        IFileInfo info = Fs.FileInfo.New(P("a.txt"));

        info.MoveTo(P("b.txt"));

        Assert.Equal("b.txt", info.Name);
        Assert.True(info.Exists);
        Assert.False(Fs.File.Exists(P("a.txt")));
    }

    [Fact]
    public void Directory_info_lists_its_contents()
    {
        Fs.Directory.CreateDirectory(P("d", "sub"));
        Fs.File.WriteAllText(P("d", "a.txt"), "hello");

        IDirectoryInfo info = Fs.DirectoryInfo.New(P("d"));

        Assert.True(info.Exists);
        Assert.Equal("d", info.Name);
        Assert.Equal(["a.txt"], info.GetFiles().Select(f => f.Name));
        Assert.Equal(["sub"], info.GetDirectories().Select(f => f.Name));
        Assert.Equal("hello", Fs.File.ReadAllText(info.GetFiles().Single().FullName));
        Assert.True(info.Attributes.HasFlag(FileAttributes.Directory));
    }

    [Fact]
    public void A_directory_info_creates_its_subdirectories()
    {
        IDirectoryInfo info = Fs.DirectoryInfo.New(P("d"));
        info.Create();

        IDirectoryInfo sub = info.CreateSubdirectory("e");

        Assert.True(Fs.Directory.Exists(P("d", "e")));
        Assert.Equal(P("d", "e"), sub.FullName);
        Assert.Equal(P("d"), sub.Parent!.FullName);
    }

    [Fact]
    public void The_parent_of_a_path_is_named()
    {
        Assert.Equal(P("a"), Fs.Directory.GetParent(P("a", "b"))!.FullName);
    }

    [Fact]
    public void A_stream_reads_and_writes_at_its_position()
    {
        using (FileSystemStream stream = Fs.File.Create(P("a.bin")))
        {
            Assert.True(stream.CanWrite);
            Assert.True(stream.CanSeek);
            Assert.EndsWith("a.bin", stream.Name, StringComparison.Ordinal);
            stream.Write([1, 2, 3, 4]);
            stream.Position = 1;
            stream.Write([9]);
        }

        using FileSystemStream reading = Fs.File.OpenRead(P("a.bin"));
        byte[] buffer = new byte[4];
        reading.ReadExactly(buffer);

        Assert.Equal([1, 9, 3, 4], buffer);
        Assert.False(reading.CanWrite);
    }

    [Fact]
    public void Opening_to_create_new_refuses_an_existing_file()
    {
        Fs.File.WriteAllText(P("a.txt"), "one");

        Assert.ThrowsAny<IOException>(() => Fs.File.Open(P("a.txt"), FileMode.CreateNew).Dispose());
    }

    [Fact]
    public void A_stream_opened_by_the_factory_writes_the_file()
    {
        using (FileSystemStream stream = Fs.FileStream.New(P("a.bin"), FileMode.Create, FileAccess.Write))
        {
            stream.Write([7]);
        }

        Assert.Equal([7], Fs.File.ReadAllBytes(P("a.bin")));
    }

    [Fact]
    public void Write_times_are_set_and_read()
    {
        DateTime when = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Fs.File.WriteAllText(P("a.txt"), "one");

        Fs.File.SetLastWriteTimeUtc(P("a.txt"), when);

        Assert.Equal(when, Fs.File.GetLastWriteTimeUtc(P("a.txt")));
        Assert.Equal(when, Fs.FileInfo.New(P("a.txt")).LastWriteTimeUtc);
    }

    [Fact]
    public void A_missing_file_has_the_earliest_file_time()
    {
        Assert.Equal(DateTime.FromFileTimeUtc(0), Fs.File.GetLastWriteTimeUtc(P("missing.txt")));
    }

    [Fact]
    public void A_symbolic_link_is_read_through()
    {
        Assert.SkipUnless(_fixture.SupportsLinks, "This process cannot create symbolic links here.");
        MockDiffers("a relative link target is taken against the current directory rather than the link's own.");
        Fs.Directory.CreateDirectory(P("d"));
        Fs.File.WriteAllText(P("d", "a.txt"), "through");

        Fs.File.CreateSymbolicLink(P("d", "link.txt"), "a.txt");

        Assert.Equal("through", Fs.File.ReadAllText(P("d", "link.txt")));
        Assert.Equal("a.txt", Fs.FileInfo.New(P("d", "link.txt")).LinkTarget);
        Assert.Equal(P("d", "a.txt"), Fs.File.ResolveLinkTarget(P("d", "link.txt"), returnFinalTarget: false)!.FullName);
        Assert.True(Fs.File.GetAttributes(P("d", "link.txt")).HasFlag(FileAttributes.ReparsePoint));
    }

    private static string[] Names(IEnumerable<string> paths) =>
        [.. paths.Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];
}

public sealed class OnDiskBehaviourTests() : FileSystemBehaviourTests(new DiskFixture());

public sealed class InMemoryBehaviourTests() : FileSystemBehaviourTests(new MemoryFixture());

public sealed class MockBehaviourTests() : FileSystemBehaviourTests(new MockFixture());
