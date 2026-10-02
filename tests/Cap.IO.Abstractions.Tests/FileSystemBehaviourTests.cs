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
    public void A_random_file_name_is_a_bare_name()
    {
        string name = Fs.Path.GetRandomFileName();

        Assert.NotEqual(name, Fs.Path.GetRandomFileName());
        Assert.Equal(name, Fs.Path.GetFileName(name));
        Assert.Equal(12, name.Length);
        Assert.Equal('.', name[8]);
        Assert.False(Fs.File.Exists(P(name)));
        Assert.False(Fs.Directory.Exists(P(name)));

        Fs.File.WriteAllText(P(name), "hello");
        Assert.Equal("hello", Fs.File.ReadAllText(P(name)));
    }

    [Fact]
    public void A_rooted_part_joined_onto_the_root_is_reachable()
    {
        MockDiffers("a path beginning with two separators does not name the path with one.");
        Fs.File.WriteAllText(P("a.txt"), "hello");
        string joined = Fs.Path.Join(Root, Fs.Path.DirectorySeparatorChar + "a.txt");

        Assert.True(Fs.File.Exists(joined));
        Assert.Equal("hello", Fs.File.ReadAllText(joined));
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

        IOException ex = Assert.ThrowsAny<IOException>(() => Fs.Directory.CreateDirectory(P("a")));
        Assert.IsNotType<DirectoryNotFoundException>(ex);
        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Creating_a_directory_where_a_file_is_through_a_trailing_dot_fails()
    {
        Fs.File.WriteAllText(P("a"), "file");

        IOException ex = Assert.ThrowsAny<IOException>(() => Fs.Directory.CreateDirectory(P("a", ".")));
        Assert.IsNotType<DirectoryNotFoundException>(ex);
        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Creating_a_directory_beneath_a_file_throws_directory_not_found()
    {
        MockDiffers("creating a directory beneath a file does not throw.");
        Fs.File.WriteAllText(P("a"), "file");

        Assert.Throws<DirectoryNotFoundException>(() => Fs.Directory.CreateDirectory(P("a", "sub")));
    }

    [Fact]
    public void Creating_a_directory_where_a_link_to_a_file_is_fails()
    {
        TestLinks.Require(_fixture.SupportsLinks);
        Fs.File.WriteAllText(P("a.txt"), "file");
        Fs.File.CreateSymbolicLink(P("link"), "a.txt");

        IOException ex = Assert.ThrowsAny<IOException>(() => Fs.Directory.CreateDirectory(P("link")));
        Assert.IsNotType<DirectoryNotFoundException>(ex);
        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Creating_a_directory_where_a_dangling_link_is_fails()
    {
        TestLinks.Require(_fixture.SupportsLinks);
        MockDiffers("a link to a missing target cannot be created.");
        Fs.File.CreateSymbolicLink(P("link"), "missing");

        IOException ex = Assert.ThrowsAny<IOException>(() => Fs.Directory.CreateDirectory(P("link")));
        Assert.IsNotType<DirectoryNotFoundException>(ex);
        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
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
    public void Moving_a_link_to_a_directory_as_a_file_is_refused()
    {
        TestLinks.Require(_fixture.SupportsLinks);
        MockDiffers("a link to a directory is moved as a file.");
        Fs.Directory.CreateDirectory(P("d"));
        Fs.Directory.CreateSymbolicLink(P("dl"), "d");

        Assert.Throws<FileNotFoundException>(() => Fs.File.Move(P("dl"), P("moved")));
        Assert.True(Fs.Directory.Exists(P("dl")));
        Assert.False(Fs.Directory.Exists(P("moved")));
    }

    [Fact]
    public void Moving_a_dangling_link_moves_the_link()
    {
        TestLinks.Require(_fixture.SupportsLinks);
        MockDiffers("a link to a missing target cannot be created.");
        Fs.File.CreateSymbolicLink(P("link"), "missing");

        Fs.File.Move(P("link"), P("moved"));

        Assert.Null(Fs.FileInfo.New(P("link")).LinkTarget);
        Assert.Equal("missing", Fs.FileInfo.New(P("moved")).LinkTarget);
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
    public void Copying_into_a_missing_directory_names_the_destination()
    {
        Fs.File.WriteAllText(P("a.txt"), "one");

        DirectoryNotFoundException e = Assert.Throws<DirectoryNotFoundException>(
            () => Fs.File.Copy(P("a.txt"), P("missing", "b.txt")));

        Assert.Contains(P("missing", "b.txt"), e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Copying_a_missing_file_names_the_source()
    {
        FileNotFoundException e = Assert.Throws<FileNotFoundException>(
            () => Fs.File.Copy(P("a.txt"), P("missing", "b.txt")));

        Assert.Contains(P("a.txt"), e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Copying_onto_a_directory_names_the_destination()
    {
        MockDiffers("copying a file onto a directory with overwrite: true does not throw.");
        Fs.File.WriteAllText(P("a.txt"), "one");
        Fs.Directory.CreateDirectory(P("d"));

        UnauthorizedAccessException denied = Assert.Throws<UnauthorizedAccessException>(
            () => Fs.File.Copy(P("a.txt"), P("d"), overwrite: true));
        IOException taken = Assert.ThrowsAny<IOException>(() => Fs.File.Copy(P("a.txt"), P("d")));

        Assert.Contains(P("d"), denied.Message, StringComparison.Ordinal);
        Assert.Contains(P("d"), taken.Message, StringComparison.Ordinal);
        Assert.Contains("directory", taken.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Copying_onto_an_existing_file_names_the_destination()
    {
        Fs.File.WriteAllText(P("a.txt"), "one");
        Fs.File.WriteAllText(P("b.txt"), "two");

        IOException e = Assert.ThrowsAny<IOException>(() => Fs.File.Copy(P("a.txt"), P("b.txt")));

        Assert.Contains(P("b.txt"), e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Moving_into_a_missing_directory_names_the_destination()
    {
        Fs.File.WriteAllText(P("a.txt"), "one");

        DirectoryNotFoundException e = Assert.Throws<DirectoryNotFoundException>(
            () => Fs.File.Move(P("a.txt"), P("missing", "b.txt")));

        Assert.Contains(P("missing", "b.txt"), e.Message, StringComparison.Ordinal);
        Assert.True(Fs.File.Exists(P("a.txt")));
    }

    [Fact]
    public void Moving_onto_an_existing_file_names_the_destination()
    {
        MockDiffers("the message for moving onto an existing file names no path.");
        Fs.File.WriteAllText(P("a.txt"), "one");
        Fs.File.WriteAllText(P("b.txt"), "two");

        IOException e = Assert.ThrowsAny<IOException>(() => Fs.File.Move(P("a.txt"), P("b.txt")));

        Assert.Contains(P("b.txt"), e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Moving_a_missing_directory_names_the_source()
    {
        DirectoryNotFoundException e = Assert.Throws<DirectoryNotFoundException>(
            () => Fs.Directory.Move(P("d"), P("missing", "e")));

        Assert.Contains(P("d"), e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(P("missing", "e"), e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Moving_a_directory_onto_a_taken_name_names_the_destination()
    {
        Fs.Directory.CreateDirectory(P("d"));
        Fs.Directory.CreateDirectory(P("e", "f"));

        IOException e = Assert.ThrowsAny<IOException>(() => Fs.Directory.Move(P("d"), P("e")));

        Assert.Contains(P("e"), e.Message, StringComparison.Ordinal);
        Assert.True(Fs.Directory.Exists(P("d")));
    }

    [Fact]
    public void Replacing_with_a_backup_in_a_missing_directory_names_the_backup()
    {
        Fs.File.WriteAllText(P("new.txt"), "new");
        Fs.File.WriteAllText(P("live.txt"), "old");

        DirectoryNotFoundException e = Assert.Throws<DirectoryNotFoundException>(
            () => Fs.File.Replace(P("new.txt"), P("live.txt"), P("missing", "backup.txt")));

        Assert.Contains(P("missing", "backup.txt"), e.Message, StringComparison.Ordinal);
        Assert.Equal("old", Fs.File.ReadAllText(P("live.txt")));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deleting_a_link_to_a_directory_removes_the_link_only(bool recursive)
    {
        TestLinks.Require(_fixture.SupportsLinks);
        Fs.Directory.CreateDirectory(P("d"));
        Fs.File.WriteAllText(P("d", "a.txt"), "kept");
        Fs.Directory.CreateSymbolicLink(P("dlink"), "d");

        Fs.Directory.Delete(P("dlink"), recursive);

        Assert.False(Fs.Directory.Exists(P("dlink")));
        Assert.True(Fs.Directory.Exists(P("d")));
        Assert.Equal("kept", Fs.File.ReadAllText(P("d", "a.txt")));
    }

    [Fact]
    public void Removing_a_file_as_a_directory_fails()
    {
        Fs.File.WriteAllText(P("a.txt"), "file");

        Assert.ThrowsAny<IOException>(() => Fs.Directory.Delete(P("a.txt")));
        Assert.True(Fs.File.Exists(P("a.txt")));
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

    [Theory]
    [InlineData("", ".hidden,a.txt,abc,b.log,noext,x.y.z", false)]
    [InlineData(".", ".hidden,a.txt,abc,b.log,noext,x.y.z", false)]
    [InlineData("*.*", ".hidden,a.txt,abc,b.log,noext,x.y.z", false)]
    [InlineData("?bc", "abc", false)]
    [InlineData("<abc", "", true)]
    [InlineData("a\\b", "a\\b", true)]
    public void Search_patterns_match_as_System_IO_matches(string pattern, string expected, bool unixOnly)
    {
        Assert.SkipWhen(unixOnly && OperatingSystem.IsWindows(), "\\ separates components on Windows, and \" < > cannot be in a name.");
        if (pattern is "." or "*.*")
        {
            MockDiffers("the search patterns . and *.* do not match every name.");
        }

        Fs.Directory.CreateDirectory(P("d"));
        foreach (string name in new[] { ".hidden", "a.txt", "abc", "b.log", "noext", "x.y.z" })
        {
            Fs.File.WriteAllText(P("d", name), string.Empty);
        }

        if (!OperatingSystem.IsWindows())
        {
            Fs.File.WriteAllText(P("d", "a\\b"), string.Empty);
        }

        string[] wanted = expected.Length == 0 ? [] : expected.Split(',');
        if (!OperatingSystem.IsWindows() && wanted.Length > 1)
        {
            wanted = [.. wanted.Append("a\\b").Order(StringComparer.Ordinal)];
        }

        Assert.Equal(wanted, Names(Fs.Directory.GetFiles(P("d"), pattern)));
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
    public void A_deferred_enumeration_keeps_the_directory_it_was_asked_for()
    {
        Fs.Directory.CreateDirectory(P("e1", "rel"));
        Fs.Directory.CreateDirectory(P("e2", "rel"));
        Fs.File.WriteAllText(P("e1", "rel", "in-e1"), string.Empty);
        Fs.File.WriteAllText(P("e2", "rel", "in-e2"), string.Empty);

        Fs.Directory.SetCurrentDirectory(P("e1"));
        IEnumerable<string> deferred = Fs.Directory.EnumerateFiles("rel");
        Fs.Directory.SetCurrentDirectory(P("e2"));

        Assert.Equal("in-e1", Fs.Path.GetFileName(Assert.Single(deferred)));
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
    public void File_info_name_is_the_last_segment_as_written()
    {
        MockDiffers("FileInfo.Name for a path ending in .. is the folded name.");
        Fs.Directory.CreateDirectory(P("d.txt", "e"));

        Assert.Equal("..", Fs.FileInfo.New(P("d.txt", "e", "..")).Name);
        Assert.Equal(".txt", Fs.FileInfo.New(P("d.txt", "e", "..")).Extension);
        Assert.Equal(".", Fs.FileInfo.New(P("d.txt", ".")).Name);
        Assert.Equal("d.txt", Fs.DirectoryInfo.New(P("d.txt", "e", "..")).Name);
        Assert.Equal("d.txt", Fs.DirectoryInfo.New(P("d.txt", ".")).Name);
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

    [Theory]
    [InlineData(FileMode.Create, FileOptions.None)]
    [InlineData(FileMode.Append, FileOptions.None)]
    [InlineData(FileMode.Create, FileOptions.Asynchronous)]
    [InlineData(FileMode.Append, FileOptions.Asynchronous)]
    public void A_stream_flushed_to_disk_keeps_its_writes(FileMode mode, FileOptions options)
    {
        Fs.File.WriteAllBytes(P("a.bin"), [1]);

        using (FileSystemStream stream = Fs.FileStream.New(P("a.bin"), mode, FileAccess.Write, FileShare.None, 4096, options))
        {
            stream.Write([2, 3]);
            stream.Flush(flushToDisk: true);
            stream.Write([4]);
            stream.Flush(flushToDisk: true);
        }

        Assert.Equal(mode == FileMode.Append ? [1, 2, 3, 4] : [2, 3, 4], Fs.File.ReadAllBytes(P("a.bin")));
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
    public void A_time_through_a_file_component_is_the_missing_time()
    {
        Fs.File.WriteAllText(P("a.txt"), "a");

        Assert.Equal(DateTime.FromFileTimeUtc(0), Fs.File.GetLastWriteTimeUtc(P("a.txt", "x")));
        Assert.Equal(DateTime.FromFileTimeUtc(0), Fs.FileInfo.New(P("a.txt", "x")).LastWriteTimeUtc);
    }

    [Fact]
    public void A_symbolic_link_is_read_through()
    {
        TestLinks.Require(_fixture.SupportsLinks);
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
