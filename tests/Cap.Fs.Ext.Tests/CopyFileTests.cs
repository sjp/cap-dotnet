using Cap.Primitives;
using Cap.Std;
using Cap.Std.Testing;

namespace Cap.Fs.Ext.Tests;

/// <summary>
/// Copying one file from beneath one handle to beneath another.
/// </summary>
/// <remarks>
/// Run against a filesystem held in memory, which resolves through the same code the disk
/// does, with one case on the disk for what only the host records.
/// </remarks>
public sealed class CopyFileTests : IDisposable
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>The contents arrive at the new name, and the count says how many bytes moved.</summary>
    [Fact]
    public void A_file_is_copied_to_a_new_name()
    {
        _fs.AddFile("from/data.txt", "contents");
        _fs.AddDirectory("to/inner");
        using Dir from = _fs.OpenRoot("from");
        using Dir to = _fs.OpenRoot("to");

        long copied = from.CopyFile("data.txt", to, "inner/copy.txt");

        Assert.Equal(8, copied);
        Assert.Equal("contents", _fs.ReadAllText("to/inner/copy.txt"));
        Assert.Equal("contents", _fs.ReadAllText("from/data.txt"));
    }

    /// <summary>
    /// A FIFO is refused as a source, with or without replacement, before anything is made at
    /// the destination.
    /// </summary>
    /// <remarks>
    /// It opens as a file does and has no contents of its own: a read of it waits on whatever
    /// is at the other end. A copy that read it would hang, or copy nothing and call the result
    /// a file.
    /// </remarks>
    [Fact]
    [NotInMemory("Needs a named pipe, which only the host's filesystem can hold.")]
    public void A_named_pipe_is_refused_as_a_source()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("There is no filesystem object of this kind on this platform.");
        }

        HostFile.CreateFifo(Path.Combine(_tree.HostPath, "pipe"));
        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

        foreach (bool overwrite in new[] { false, true })
        {
            CapIOException refused = Assert.Throws<CapIOException>(
                () => root.CopyFile("pipe", root, "copy", overwrite));
            Assert.Equal(CapErrorKind.NotSupported, refused.Kind);
            Assert.Equal(["pipe"], HostDirectory.GetFileSystemEntries(_tree.HostPath).Select(Path.GetFileName));
        }
    }

    /// <summary>Without replacement a taken name is refused and left as it was.</summary>
    [Fact]
    public void A_taken_name_is_refused_unless_replacement_is_asked_for()
    {
        _fs.AddFile("data.txt", "new");
        _fs.AddFile("copy.txt", "old");
        using Dir root = _fs.OpenRoot();

        _ = Assert.Throws<CapIOException>(() => root.CopyFile("data.txt", root, "copy.txt"));
        Assert.Equal("old", _fs.ReadAllText("copy.txt"));

        root.CopyFile("data.txt", root, "copy.txt", overwrite: true);
        Assert.Equal("new", _fs.ReadAllText("copy.txt"));
        Assert.Equal(["copy.txt", "data.txt"], _fs.GetEntries().Order(StringComparer.Ordinal));
    }

    /// <summary>A file copied onto itself with replacement keeps what it held.</summary>
    [Fact]
    public void A_file_copied_onto_itself_keeps_its_contents()
    {
        _fs.AddFile("data.txt", "same");
        using Dir root = _fs.OpenRoot();

        _ = Assert.Throws<CapIOException>(() => root.CopyFile("data.txt", root, "data.txt"));
        root.CopyFile("data.txt", root, "data.txt", overwrite: true);

        Assert.Equal("same", _fs.ReadAllText("data.txt"));
        Assert.Equal(["data.txt"], _fs.GetEntries());
    }

    /// <summary>
    /// Under Windows rules, where a file open without delete sharing cannot be replaced, a file
    /// copied onto itself with replacement still keeps what it held.
    /// </summary>
    [Fact]
    public void A_file_copied_onto_itself_under_Windows_rules_keeps_its_contents()
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Windows });
        fs.AddFile("data.txt", "same");
        using Dir root = fs.OpenRoot();

        root.CopyFile("data.txt", root, "data.txt", overwrite: true);

        Assert.Equal("same", fs.ReadAllText("data.txt"));
        Assert.Equal(["data.txt"], fs.GetEntries());
        Assert.Equal(1, fs.OpenHandleCount);
    }

    /// <summary>A directory at the destination is never replaced by a file.</summary>
    [Fact]
    public void A_directory_at_the_destination_is_refused()
    {
        _fs.AddFile("data.txt", "x");
        _fs.AddFile("folder/kept.txt", "kept");
        using Dir root = _fs.OpenRoot();

        _ = Assert.Throws<CapIOException>(() => root.CopyFile("data.txt", root, "folder", overwrite: true));
        _ = Assert.Throws<CapIOException>(() => root.CopyFile("folder", root, "elsewhere"));
        Assert.Equal("kept", _fs.ReadAllText("folder/kept.txt"));
    }

    /// <summary>
    /// A link at the source is followed while it stays inside; a link at the destination is
    /// replaced with replacement and makes the name taken without, and what it points at is
    /// never written.
    /// </summary>
    [Fact]
    public void Links_are_followed_at_the_source_and_never_written_through_at_the_destination()
    {
        _fs.AddFile("real.txt", "real");
        _fs.AddSymbolicLink("alias.txt", "real.txt");
        _fs.AddFile("victim.txt", "untouched");
        _fs.AddSymbolicLink("trap.txt", "victim.txt");
        using Dir root = _fs.OpenRoot();

        root.CopyFile("alias.txt", root, "copied.txt");
        Assert.Equal("real", _fs.ReadAllText("copied.txt"));

        _ = Assert.Throws<CapIOException>(() => root.CopyFile("real.txt", root, "trap.txt"));
        root.CopyFile("real.txt", root, "trap.txt", overwrite: true);

        Assert.Equal("untouched", _fs.ReadAllText("victim.txt"));
        Assert.Equal("real", _fs.ReadAllText("trap.txt"));
        Assert.Equal(CapFileType.File, root.GetMetadata("trap.txt").Type);

        using Dir strict = root.Restrict(SymlinkPolicy.Deny);
        _ = Assert.Throws<CapIOException>(() => strict.CopyFile("alias.txt", strict, "refused.txt"));
    }

    /// <summary>Neither end can reach outside the handle it is resolved against.</summary>
    [Fact]
    public void Neither_path_can_leave_its_handle()
    {
        _fs.AddFile("outside.txt", "secret");
        _fs.AddFile("inside/data.txt", "x");
        _fs.AddSymbolicLink("inside/escape.txt", "../outside.txt");
        using Dir root = _fs.OpenRoot("inside");

        _ = Assert.Throws<SandboxEscapeException>(() => root.CopyFile("../outside.txt", root, "stolen.txt"));
        _ = Assert.Throws<SandboxEscapeException>(() => root.CopyFile("escape.txt", root, "stolen.txt"));
        _ = Assert.Throws<SandboxEscapeException>(() => root.CopyFile("data.txt", root, "../planted.txt"));
        Assert.False(_fs.Exists("inside/stolen.txt"));
        Assert.False(_fs.Exists("planted.txt"));
    }

    /// <summary>The source's permissions travel with its contents.</summary>
    [Fact]
    public void Permissions_travel_with_the_contents()
    {
        _fs.AddFile("data.txt", "x");
        if (OperatingSystem.IsWindows())
        {
            _fs.SetAttributes("data.txt", FileAttributes.Hidden);
        }
        else
        {
            _fs.SetUnixMode("data.txt", UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        using Dir root = _fs.OpenRoot();
        root.CopyFile("data.txt", root, "copy.txt");
        root.CopyFile("data.txt", root, "replaced.txt", overwrite: true);

        string wanted = root.GetMetadata("data.txt").Permissions.ToString();
        Assert.Equal(wanted, root.GetMetadata("copy.txt").Permissions.ToString());
        Assert.Equal(wanted, root.GetMetadata("replaced.txt").Permissions.ToString());
    }

    /// <summary>A file can be copied from a tree in memory onto the disk.</summary>
    [Fact]
    public void A_file_is_copied_between_backends()
    {
        _fs.AddFile("data.txt", "from memory");
        using Dir memory = _fs.OpenRoot();
        using Dir disk = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

        long copied = memory.CopyFile("data.txt", disk, "arrived.txt");

        Assert.Equal(11, copied);
        Assert.Equal("from memory", HostFile.ReadAllText(Path.Combine(_tree.HostPath, "arrived.txt")));
    }

    /// <summary>Through wrappers that are not a Dir, the copy goes through the interfaces' members.</summary>
    [Fact]
    public void A_file_is_copied_through_wrapped_handles()
    {
        _fs.AddFile("data.txt", "wrapped");
        using RecordingDir root = new(_fs.OpenRoot(), [], "root");

        root.CopyFile("data.txt", root, "copy.txt", overwrite: true);

        Assert.Equal("wrapped", _fs.ReadAllText("copy.txt"));
        Assert.Contains(root.Log, call => call.StartsWith("root: Rename(", StringComparison.Ordinal));
    }

    /// <summary>
    /// With options, a file is copied by name and the report counts one file and its bytes.
    /// </summary>
    [Fact]
    public void A_single_file_is_copied_by_name_with_options()
    {
        _fs.AddFile("from/data.txt", "contents");
        _fs.AddDirectory("to/inner");
        using Dir from = _fs.OpenRoot("from");
        using Dir to = _fs.OpenRoot("to");

        CopyReport report = from.CopyFile(
            "data.txt", to, "inner/copy.txt", CopyOptions.Default, TestContext.Current.CancellationToken);

        Assert.Equal((0, 1, 0, 0, 8L), (report.Directories, report.Files, report.Symlinks, report.Skipped, report.Bytes));
        Assert.Equal("contents", _fs.ReadAllText("to/inner/copy.txt"));
    }

    /// <summary>
    /// With options and replacement, a link at the destination is replaced by the copy and
    /// what it pointed at is not written; without replacement the name is taken.
    /// </summary>
    [Fact]
    public void With_options_a_link_at_the_destination_is_replaced_not_written_through()
    {
        _fs.AddFile("data.txt", "new");
        _fs.AddFile("victim.txt", "untouched");
        _fs.AddSymbolicLink("trap.txt", "victim.txt");
        using Dir root = _fs.OpenRoot();

        _ = Assert.Throws<CapIOException>(() => root.CopyFile("data.txt", root, "trap.txt", CopyOptions.Default, TestContext.Current.CancellationToken));
        Assert.Equal(CapFileType.Symlink, root.GetMetadata("trap.txt").Type);

        root.CopyFile("data.txt", root, "trap.txt", new CopyOptions { Overwrite = true }, TestContext.Current.CancellationToken);

        Assert.Equal(CapFileType.File, root.GetMetadata("trap.txt").Type);
        Assert.Equal("new", _fs.ReadAllText("trap.txt"));
        Assert.Equal("untouched", _fs.ReadAllText("victim.txt"));
    }

    /// <summary>
    /// With options, a link at the source is not followed: it is refused, skipped or made
    /// again as <see cref="CopyOptions.Symlinks"/> says.
    /// </summary>
    [Fact]
    public void With_options_a_link_at_the_source_is_dealt_with_as_the_options_say()
    {
        _fs.AddFile("real.txt", "real");
        _fs.AddSymbolicLink("alias.txt", "real.txt");
        using Dir root = _fs.OpenRoot();

        CapIOException refused = Assert.Throws<CapIOException>(
            () => root.CopyFile("alias.txt", root, "copy.txt", CopyOptions.Default, TestContext.Current.CancellationToken));
        Assert.Equal(CapErrorKind.NotSupported, refused.Kind);
        Assert.False(_fs.Exists("copy.txt"));

        CopyReport skipped = root.CopyFile("alias.txt", root, "copy.txt", new CopyOptions { Symlinks = CopyAction.Skip }, TestContext.Current.CancellationToken);
        Assert.Equal(1, skipped.Skipped);
        Assert.False(_fs.Exists("copy.txt"));

        CopyReport relinked = root.CopyFile("alias.txt", root, "copy.txt", new CopyOptions { Symlinks = CopyAction.Recreate }, TestContext.Current.CancellationToken);
        Assert.Equal(1, relinked.Symlinks);
        Assert.Equal("real.txt", _fs.GetSymbolicLinkTarget("copy.txt"));

        _fs.AddFile("taken.txt", "taken");
        _ = Assert.Throws<CapIOException>(
            () => root.CopyFile("alias.txt", root, "taken.txt", new CopyOptions { Symlinks = CopyAction.Recreate }, TestContext.Current.CancellationToken));
        root.CopyFile("alias.txt", root, "taken.txt", new CopyOptions { Symlinks = CopyAction.Recreate, Overwrite = true }, TestContext.Current.CancellationToken);
        Assert.Equal("real.txt", _fs.GetSymbolicLinkTarget("taken.txt"));
        Assert.Equal("real", _fs.ReadAllText("real.txt"));
    }

    /// <summary>
    /// With options, permissions and times are carried only when asked for, as the tree copy
    /// carries them.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void With_options_permissions_and_times_are_carried_only_when_asked(bool overwrite)
    {
        DateTimeOffset written = new(2011, 3, 4, 5, 6, 7, TimeSpan.Zero);
        _fs.AddFile("data.txt", "x");
        if (OperatingSystem.IsWindows())
        {
            _fs.SetAttributes("data.txt", FileAttributes.Hidden);
        }
        else
        {
            _fs.SetUnixMode("data.txt", UnixFileMode.UserRead);
        }

        _fs.SetTimes("data.txt", lastAccess: written, lastWrite: written);
        using Dir root = _fs.OpenRoot();
        string wanted = root.GetMetadata("data.txt").Permissions.ToString();

        root.CopyFile("data.txt", root, "plain.txt", new CopyOptions { Overwrite = overwrite }, TestContext.Current.CancellationToken);
        root.CopyFile(
            "data.txt",
            root,
            "carried.txt",
            new CopyOptions { Overwrite = overwrite, PreservePermissions = true, PreserveTimes = true }, TestContext.Current.CancellationToken);

        Assert.NotEqual(wanted, root.GetMetadata("plain.txt").Permissions.ToString());
        Assert.NotEqual(written, root.GetMetadata("plain.txt").LastWriteTime);
        Assert.Equal(wanted, root.GetMetadata("carried.txt").Permissions.ToString());
        Assert.Equal(written, root.GetMetadata("carried.txt").LastWriteTime);
    }

    /// <summary>
    /// With options, a copy that fails part way leaves nothing at the destination, where the
    /// form that takes a flag leaves what it had written.
    /// </summary>
    [Fact]
    public void With_options_a_failed_copy_leaves_no_partial_file()
    {
        _fs.AddFile("data.txt", "contents");
        using Dir root = _fs.OpenRoot();

        _fs.FailNextWrites(1);
        _ = Assert.ThrowsAny<IOException>(() => root.CopyFile("data.txt", root, "copy.txt", CopyOptions.Default, TestContext.Current.CancellationToken));
        Assert.False(_fs.Exists("copy.txt"));

        _fs.FailNextWrites(1);
        _ = Assert.ThrowsAny<IOException>(() => root.CopyFile("data.txt", root, "flagged.txt"));
        Assert.True(_fs.Exists("flagged.txt"));
    }

    /// <summary>
    /// With options, a directory, a cancelled token and an impossible option are refused before
    /// anything is made.
    /// </summary>
    [Fact]
    public void With_options_what_cannot_be_copied_is_refused_before_anything_is_made()
    {
        _fs.AddFile("folder/kept.txt", "kept");
        _fs.AddFile("data.txt", "x");
        using Dir root = _fs.OpenRoot();

        CapIOException directory = Assert.Throws<CapIOException>(
            () => root.CopyFile("folder", root, "copy", CopyOptions.Default, TestContext.Current.CancellationToken));
        Assert.Equal(CapErrorKind.IsADirectory, directory.Kind);

        _ = Assert.ThrowsAny<OperationCanceledException>(
            () => root.CopyFile("data.txt", root, "copy", CopyOptions.Default, new CancellationToken(canceled: true)));
        _ = Assert.Throws<ArgumentException>(
            () => root.CopyFile("data.txt", root, "copy", new CopyOptions { OtherKinds = CopyAction.Recreate }, TestContext.Current.CancellationToken));
        _ = Assert.Throws<ArgumentNullException>(() => root.CopyFile("data.txt", root, "copy", (CopyOptions)null!, TestContext.Current.CancellationToken));

        Assert.False(_fs.Exists("copy"));
    }

    /// <summary>With options, a named pipe is dealt with as <see cref="CopyOptions.OtherKinds"/> says.</summary>
    [Fact]
    [NotInMemory("Needs a named pipe, which only the host's filesystem can hold.")]
    public void With_options_a_named_pipe_is_refused_or_skipped()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("There is no filesystem object of this kind on this platform.");
        }

        HostFile.CreateFifo(Path.Combine(_tree.HostPath, "pipe"));
        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

        CapIOException refused = Assert.Throws<CapIOException>(
            () => root.CopyFile("pipe", root, "copy", CopyOptions.Default, TestContext.Current.CancellationToken));
        Assert.Equal(CapErrorKind.NotSupported, refused.Kind);
        Assert.Equal(1, root.CopyFile("pipe", root, "copy", new CopyOptions { OtherKinds = CopyAction.Skip }, TestContext.Current.CancellationToken).Skipped);
        Assert.Equal(["pipe"], HostDirectory.GetFileSystemEntries(_tree.HostPath).Select(Path.GetFileName));
    }
}
