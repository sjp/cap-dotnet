using Cap.Fs.Ext;
using Cap.Primitives;

namespace Cap.Std.Testing.Tests;

/// <summary>
/// A filesystem told to follow Windows rules does so on whatever machine runs the test.
/// </summary>
public sealed class WindowsRulesTests
{
    private static InMemoryFileSystem Windows(ResolutionBackend resolution = ResolutionBackend.PortableWalk) =>
        new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Windows, Resolution = resolution });

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_backslash_separates_components(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Windows(resolution);
        fs.AddFile("dir/sub/file.txt", "x");

        using Dir root = fs.OpenRoot();

        Assert.Equal("x", root.ReadAllText(@"dir\sub\file.txt"));
        Assert.Equal("x", root.ReadAllText(@"dir/sub\file.txt"));
        root.WriteAllBytes(@"dir\sub\new.txt", [1]);
        Assert.True(fs.Exists("dir/sub/new.txt"));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("aux.txt")]
    [InlineData("a:b")]
    [InlineData(@"\rooted")]
    [InlineData(@"C:\absolute")]
    [InlineData("C:drive-relative")]
    [InlineData(@"\\server\share\file")]
    public void A_name_that_reaches_outside_under_windows_rules_is_refused_as_an_escape(string path)
    {
        InMemoryFileSystem fs = Windows();
        using Dir root = fs.OpenRoot();

        Assert.Throws<SandboxEscapeException>(() => root.WriteAllBytes(path, [1]));
        Assert.Empty(fs.GetEntries());
    }

    [Theory]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("a*b")]
    public void A_name_windows_would_change_or_cannot_hold_is_refused(string name)
    {
        InMemoryFileSystem fs = Windows();
        using Dir root = fs.OpenRoot();

        Assert.Throws<ArgumentException>(() => root.WriteAllBytes(name, [1]));
        Assert.Empty(fs.GetEntries());
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_link_with_a_windows_rooted_target_is_refused_as_an_escape(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Windows(resolution);
        fs.AddSymbolicLink("system", @"C:\Windows", targetIsDirectory: true);
        fs.AddSymbolicLink("relative", @"..\outside");

        using Dir root = fs.OpenRoot();

        Assert.Throws<SandboxEscapeException>(() => root.OpenDir("system"));
        Assert.Throws<SandboxEscapeException>(() => root.ReadAllBytes(@"relative\file"));
        Assert.Throws<SandboxEscapeException>(() => root.CreateDirSymlink("again", @"D:\data"));
    }

    /// <summary>
    /// A link whose target names a character device is refused as an escape when it is
    /// followed, and one whose target is merely a name the platform would not keep as written
    /// is refused as a link that cannot be followed.
    /// </summary>
    /// <remarks>
    /// Both targets are stored, because what a link holds is data and a link can be made by
    /// anything able to write in the subtree. The difference is where each one leads. A device
    /// name reaches the device wherever it appears, so following it leaves the subtree just as
    /// surely as an absolute target does, and it is reported and logged as the escape it is. A
    /// trailing dot or a character the platform reinterprets leads nowhere at all: the name is
    /// unusable rather than elsewhere, so it is an ordinary refusal — and not a complaint about
    /// the caller's own argument, which named the link and was perfectly good.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_link_whose_target_windows_reads_as_something_else_is_refused_when_followed(
        ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Windows(resolution);
        fs.AddFile("plain/marker", "x");
        fs.AddSymbolicLink("to-a-device", "COM9");
        fs.AddSymbolicLink("to-a-stripped-name", @"plain\marker.");
        fs.AddSymbolicLink("to-a-pattern", "pl*n");

        using Dir root = fs.OpenRoot();

        Assert.Throws<SandboxEscapeException>(() => root.ReadAllBytes("to-a-device"));

        foreach (string link in new[] { "to-a-stripped-name", "to-a-pattern" })
        {
            CapIOException refused = Assert.ThrowsAny<CapIOException>(() => root.ReadAllBytes(link));
            Assert.NotEqual(CapErrorKind.Escaped, refused.Kind);
        }
    }

    /// <summary>
    /// Windows asks what kind of object a name holds before it asks whether the name is free,
    /// so a directory is refused as one where Linux would call the name taken. A directory
    /// removal of a file link is refused as not a directory, as on Linux.
    /// </summary>
    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_refusal_is_reported_by_the_kind_windows_finds_before_the_name(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Windows(resolution);
        fs.AddDirectory("inner");
        fs.AddDirectory("empty");
        fs.AddFile("plain", "p");
        fs.AddSymbolicLink("link", "plain");

        using Dir root = fs.OpenRoot();

        CapIOException created = Assert.ThrowsAny<CapIOException>(() => root.CreateNewFile("inner").Dispose());
        Assert.Equal(CapErrorKind.IsADirectory, created.Kind);
        CapIOException linked = Assert.ThrowsAny<CapIOException>(() => root.CreateHardLink("inner", root, "empty"));
        Assert.Equal(CapErrorKind.IsADirectory, linked.Kind);
        CapIOException removed = Assert.ThrowsAny<CapIOException>(() => root.DeleteDir("link"));
        Assert.Equal(CapErrorKind.NotADirectory, removed.Kind);
        CapIOException symlinked = Assert.ThrowsAny<CapIOException>(() => root.CreateSymlink("inner", "plain"));
        Assert.Equal(CapErrorKind.IsADirectory, symlinked.Kind);
    }

    /// <summary>
    /// The replacing rename Windows is asked for replaces an empty directory with a directory,
    /// as Linux does, and refuses one that is not empty as not empty.
    /// </summary>
    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_replacing_rename_replaces_an_empty_directory(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Windows(resolution);
        fs.AddFile("inner/marker", "m");
        fs.AddDirectory("empty");
        fs.AddFile("full/child", "c");

        using Dir root = fs.OpenRoot();

        CapIOException refused = Assert.ThrowsAny<CapIOException>(
            () => root.Rename("empty", root, "full", replaceExisting: true));
        Assert.Equal(CapErrorKind.NotEmpty, refused.Kind);

        root.Rename("inner", root, "empty", replaceExisting: true);
        Assert.Equal("m", root.ReadAllText("empty/marker"));
        Assert.False(root.Exists("inner"));
    }

    [Fact]
    public void Names_are_found_under_any_case_and_keep_the_case_they_were_made_with()
    {
        InMemoryFileSystem fs = Windows();
        fs.AddFile("Config/App.json", "x");

        using Dir root = fs.OpenRoot();

        Assert.Equal("x", root.ReadAllText("config/app.JSON"));
        Assert.True(fs.Exists("CONFIG/APP.JSON"));
        Assert.Equal(["Config"], root.EnumerateEntries().Select(entry => entry.Name));

        CapIOException taken = Assert.ThrowsAny<CapIOException>(() => root.CreateNewFile("config/APP.json").Dispose());
        Assert.Equal(CapErrorKind.AlreadyExists, taken.Kind);
    }

    /// <summary>
    /// Whether or not the rename may replace, as NTFS and the default macOS volume allow; see
    /// <c>DirMutationTests</c> for the same case against the host.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_rename_can_change_only_the_case_of_a_name(bool replaceExisting)
    {
        InMemoryFileSystem fs = Windows();
        fs.AddFile("readme.txt", "x");

        using Dir root = fs.OpenRoot();
        root.Rename("readme.txt", root, "README.txt", replaceExisting);

        Assert.Equal(["README.txt"], fs.GetEntries());
        Assert.Equal("x", fs.ReadAllText("README.txt"));
    }

    [Fact]
    public void Case_sensitivity_can_be_chosen_apart_from_the_syntax()
    {
        InMemoryFileSystem unixIgnoringCase = new(new InMemoryFileSystemOptions
        {
            PathSyntax = CapPathSyntax.Unix,
            CaseSensitive = false,
        });
        InMemoryFileSystem windowsMatchingCase = new(new InMemoryFileSystemOptions
        {
            PathSyntax = CapPathSyntax.Windows,
            CaseSensitive = true,
        });

        unixIgnoringCase.AddFile("a.txt", "x");
        windowsMatchingCase.AddFile("a.txt", "lower");
        windowsMatchingCase.AddFile("A.txt", "upper");

        Assert.False(unixIgnoringCase.CaseSensitive);
        Assert.True(unixIgnoringCase.Exists("A.TXT"));
        Assert.Equal("upper", windowsMatchingCase.ReadAllText("A.txt"));
        Assert.Equal(["A.txt", "a.txt"], windowsMatchingCase.GetEntries());
    }

    [Fact]
    public void Names_that_differ_only_in_case_are_different_under_unix_rules()
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Unix });
        using Dir root = fs.OpenRoot();

        root.WriteAllBytes("a.txt", [1]);
        root.WriteAllBytes("A.txt", [2]);

        Assert.True(fs.CaseSensitive);
        Assert.Equal(["A.txt", "a.txt"], fs.GetEntries());
        Assert.Throws<FileNotFoundException>(() => root.ReadAllBytes("A.TXT"));
    }

    [Fact]
    public void The_read_only_attribute_refuses_removal_and_writing_until_cleared()
    {
        InMemoryFileSystem fs = Windows();
        fs.AddFile("locked.txt", "x");
        fs.SetAttributes("locked.txt", FileAttributes.ReadOnly);

        using Dir root = fs.OpenRoot();

        Assert.Throws<UnauthorizedAccessException>(() => root.DeleteFile("locked.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => root.WriteAllBytes("locked.txt", [1]));
        Assert.Equal("x", root.ReadAllText("locked.txt"));
        Assert.True(root.GetMetadata("locked.txt").Permissions.TryGetWindowsAttributes(out FileAttributes attributes));
        Assert.True((attributes & FileAttributes.ReadOnly) != 0);
        Assert.False(root.GetMetadata("locked.txt").Permissions.TryGetUnixMode(out _));

        fs.SetAttributes("locked.txt", FileAttributes.Normal);
        root.DeleteFile("locked.txt");
        Assert.False(fs.Exists("locked.txt"));
    }

    [Fact]
    public void Removing_a_tree_clears_the_read_only_attribute_on_the_way()
    {
        InMemoryFileSystem fs = Windows();
        fs.AddFile("tree/locked.txt", "x");
        fs.SetAttributes("tree/locked.txt", FileAttributes.ReadOnly);

        using Dir root = fs.OpenRoot();
        root.DeleteTree("tree", TestContext.Current.CancellationToken);

        Assert.Empty(fs.GetEntries());
    }

    [Fact]
    public void A_walk_that_skips_hidden_entries_reads_the_hidden_attribute()
    {
        InMemoryFileSystem fs = Windows();
        fs.AddFile("shown.txt");
        fs.AddFile("hidden.txt");
        fs.SetAttributes("hidden.txt", FileAttributes.Hidden);

        using Dir root = fs.OpenRoot();
        string[] seen = [.. root.Walk(new WalkOptions { SkipHidden = true }).Select(entry => entry.Name)];

        Assert.Equal(["shown.txt"], seen);
    }

    [Fact]
    public void A_pattern_is_divided_as_the_handle_divides_a_path()
    {
        InMemoryFileSystem fs = Windows();
        fs.AddFile("logs/2024/a.log");
        fs.AddFile("logs/2025/b.log");

        using Dir root = fs.OpenRoot();
        string[] found = [.. root.Glob(@"logs\*\*.log").Select(entry => entry.Name).Order(StringComparer.Ordinal)];

        Assert.Equal(["a.log", "b.log"], found);
    }

    /// <summary>
    /// A time before Windows file times begin is refused as an argument, and changes nothing,
    /// as it is on Windows.
    /// </summary>
    [Fact]
    public void An_instant_before_1601_is_refused_and_changes_nothing()
    {
        InMemoryFileSystem fs = Windows();
        fs.AddFile("file", "x");
        CapFileTime tooEarly = CapFileTime.At(new DateTimeOffset(1500, 1, 1, 0, 0, 0, TimeSpan.Zero));
        DateTimeOffset written = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        using Dir root = fs.OpenRoot();
        root.SetTimes("file", lastWrite: CapFileTime.At(written));

        Assert.Throws<ArgumentOutOfRangeException>(() => root.SetTimes("file", CapFileTime.At(written), tooEarly));
        Assert.Throws<ArgumentOutOfRangeException>(() => root.SetTimes("file", lastAccess: tooEarly));
        Assert.Equal(written, root.GetMetadata("file").LastWriteTime);

        root.SetTimes("file", lastWrite: CapFileTime.At(new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    /// <summary>A file held with <see cref="FileShare.None"/> refuses every second open, as on Windows.</summary>
    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_file_held_sharing_nothing_refuses_any_second_open(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Windows(resolution);
        fs.AddFile("open.txt", "x");

        using Dir root = fs.OpenRoot();
        using CapFile held = root.OpenFile("open.txt", FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Throws<UnauthorizedAccessException>(() => root.OpenFile("open.txt", FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        Assert.Throws<UnauthorizedAccessException>(() => root.OpenFile("open.txt", FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        Assert.Throws<UnauthorizedAccessException>(() => root.ReadAllBytes("open.txt"));
    }

    /// <summary>
    /// A file held with the default <see cref="FileShare.Read"/> admits another reader and
    /// refuses a writer, and a truncating open it refuses leaves the contents alone.
    /// </summary>
    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_file_held_sharing_reading_admits_a_reader_and_refuses_a_writer(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Windows(resolution);
        fs.AddFile("open.txt", "kept");

        using Dir root = fs.OpenRoot();
        using CapFile held = root.OpenFile("open.txt", FileMode.Open, FileAccess.Read, FileShare.Read);

        root.OpenFile("open.txt", FileMode.Open, FileAccess.Read, FileShare.ReadWrite).Dispose();
        Assert.Equal("kept", root.ReadAllText("open.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => root.OpenFile("open.txt", FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        Assert.Throws<UnauthorizedAccessException>(() => root.WriteAllText("open.txt", "replaced"));
        Assert.Equal("kept", fs.ReadAllText("open.txt"));
    }

    /// <summary>
    /// A second open must share what the first holds as well as ask only for what the first
    /// shares: a reader that will not share writing is refused while a writer holds the file.
    /// </summary>
    [Fact]
    public void A_second_open_that_does_not_share_what_the_first_holds_is_refused()
    {
        InMemoryFileSystem fs = Windows();
        fs.AddFile("open.txt", "x");

        using Dir root = fs.OpenRoot();
        using CapFile writer = root.OpenFile("open.txt", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        Assert.Throws<UnauthorizedAccessException>(() => root.OpenFile("open.txt", FileMode.Open, FileAccess.Read, FileShare.Read));
        root.OpenFile("open.txt", FileMode.Open, FileAccess.Read, FileShare.ReadWrite).Dispose();
    }

    /// <summary>
    /// A file held open without <see cref="FileShare.Delete"/> cannot be removed, renamed, or
    /// replaced by a rename, and each refusal leaves it where it was.
    /// </summary>
    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_file_held_without_delete_sharing_cannot_be_removed_renamed_or_replaced(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Windows(resolution);
        fs.AddFile("open.txt", "held");
        fs.AddFile("other.txt", "other");

        using Dir root = fs.OpenRoot();
        using CapFile held = root.OpenFile("open.txt", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.Throws<UnauthorizedAccessException>(() => root.DeleteFile("open.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => root.Rename("open.txt", root, "moved.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => root.Rename("other.txt", root, "open.txt", replaceExisting: true));

        Assert.Equal(["open.txt", "other.txt"], fs.GetEntries());
        Assert.Equal("held", fs.ReadAllText("open.txt"));
        Assert.Equal("other", fs.ReadAllText("other.txt"));
    }

    /// <summary>
    /// A file held open with <see cref="FileShare.Delete"/> can be removed, as on Windows,
    /// and stays readable through the handle that holds it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_file_held_with_delete_sharing_can_be_removed_and_stays_readable(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Windows(resolution);
        fs.AddFile("doomed.txt", "still here");
        fs.AddFile("moving.txt", "moved");

        using Dir root = fs.OpenRoot();
        using CapFile doomed = root.OpenFile("doomed.txt", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using CapFile moving = root.OpenFile("moving.txt", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        root.DeleteFile("doomed.txt");
        root.Rename("moving.txt", root, "moved.txt");

        byte[] buffer = new byte[10];
        Assert.Equal(10, doomed.Read(buffer, 0));
        Assert.Equal("still here"u8.ToArray(), buffer);
        Assert.Equal(["moved.txt"], fs.GetEntries());
    }

    /// <summary>
    /// Closing the handle that held a file lifts its refusals at once, and so does closing
    /// the last copy of it: a stream taken from a file is a copy of its handle, and holds the
    /// file as the handle did.
    /// </summary>
    [Fact]
    public void Closing_every_handle_on_a_file_lifts_its_sharing_refusals()
    {
        InMemoryFileSystem fs = Windows();
        fs.AddFile("open.txt", "x");

        using Dir root = fs.OpenRoot();
        CapFile held = root.OpenFile("open.txt", FileMode.Open, FileAccess.Read, FileShare.None);
        Stream stream = held.AsStream();

        held.Dispose();
        Assert.Throws<UnauthorizedAccessException>(() => root.ReadAllBytes("open.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => root.DeleteFile("open.txt"));

        stream.Dispose();
        Assert.Equal("x"u8.ToArray(), root.ReadAllBytes("open.txt"));
        root.DeleteFile("open.txt");
        Assert.Empty(fs.GetEntries());
    }

    /// <summary>
    /// A copy made to only append still holds the file for writing, as a narrowed duplicate
    /// does on Windows, where sharing belongs to the open rather than to each handle.
    /// </summary>
    [Fact]
    public void An_appending_copy_holds_the_file_as_the_open_it_came_from()
    {
        InMemoryFileSystem fs = Windows();
        fs.AddFile("log.txt");

        using Dir root = fs.OpenRoot();
        CapFile writer = root.OpenFile("log.txt", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        writer.IsAppending = true;
        using Stream appending = writer.AsStream();
        writer.Dispose();

        Assert.Throws<UnauthorizedAccessException>(() => root.OpenFile("log.txt", FileMode.Open, FileAccess.Read, FileShare.Read));
        root.OpenFile("log.txt", FileMode.Open, FileAccess.Read, FileShare.ReadWrite).Dispose();
    }

    /// <summary>
    /// A directory held open can still be removed and renamed, since the Windows backend opens
    /// every directory sharing everything.
    /// </summary>
    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void A_directory_held_open_can_still_be_renamed_and_removed(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Windows(resolution);
        fs.AddDirectory("held");

        using Dir root = fs.OpenRoot();
        using Dir held = root.OpenDir("held");

        root.Rename("held", root, "moved");
        root.DeleteDir("moved");

        Assert.Empty(fs.GetEntries());
    }

    /// <summary>
    /// Under Unix rules sharing is ignored, as Linux ignores it: every refusal the Windows
    /// rules give above is allowed.
    /// </summary>
    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void Under_unix_rules_sharing_is_ignored(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        fs.AddFile("open.txt", "x");
        fs.AddFile("other.txt", "other");
        fs.AddFile("doomed.txt", "gone");

        using Dir root = fs.OpenRoot();
        using CapFile held = root.OpenFile("open.txt", FileMode.Open, FileAccess.Read, FileShare.None);
        using CapFile doomed = root.OpenFile("doomed.txt", FileMode.Open, FileAccess.Read, FileShare.None);

        root.OpenFile("open.txt", FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();
        root.Rename("open.txt", root, "moved.txt");
        root.Rename("other.txt", root, "moved.txt", replaceExisting: true);
        root.DeleteFile("doomed.txt");

        Assert.Equal(["moved.txt"], fs.GetEntries());
        Assert.Equal("other", fs.ReadAllText("moved.txt"));
    }
}
