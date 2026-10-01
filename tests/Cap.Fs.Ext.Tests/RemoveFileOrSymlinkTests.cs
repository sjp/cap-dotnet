using Cap.Primitives;
using Cap.Std;
using Cap.Std.Testing;

namespace Cap.Fs.Ext.Tests;

/// <summary>
/// Removing a name that holds a file or a symbolic link, whichever kind of link it is.
/// </summary>
/// <remarks>
/// What is asserted for every link is that its target is still there afterwards: a removal
/// that looked at the target to decide how to remove the name has followed the link, and one
/// that removed the target has done the thing this exists to prevent.
/// </remarks>
public sealed class RemoveFileOrSymlinkTests
{
    private readonly InMemoryFileSystem _fs = new();

    public RemoveFileOrSymlinkTests()
    {
        _fs.AddFile("report.txt", "contents");
        _fs.AddFile("folder/inner.txt", "inner");
        _fs.AddDirectory("empty");
        _fs.AddSymbolicLink("to-file", "report.txt");
        _fs.AddSymbolicLink("to-folder", "folder");
        _fs.AddSymbolicLink("to-nothing", "absent");
        _fs.AddSymbolicLink("to-outside", "../../outside");
        _fs.AddSymbolicLink("folder/to-parent", "..");
    }

    /// <summary>A file and every kind of link are removed, and no link's target is touched.</summary>
    [Theory]
    [InlineData("report.txt")]
    [InlineData("to-file")]
    [InlineData("to-folder")]
    [InlineData("to-nothing")]
    [InlineData("to-outside")]
    [InlineData("folder/to-parent")]
    public void A_file_or_link_is_removed_and_a_target_is_left(string path)
    {
        using Dir root = _fs.OpenRoot();

        root.RemoveFileOrSymlink(path);

        Assert.False(_fs.Exists(path));
        Assert.Equal("inner", _fs.ReadAllText("folder/inner.txt"));
        if (path != "report.txt")
        {
            Assert.Equal("contents", _fs.ReadAllText("report.txt"));
        }
    }

    /// <summary>The reporting form removes what the throwing form removes.</summary>
    [Theory]
    [InlineData("report.txt")]
    [InlineData("to-folder")]
    [InlineData("to-nothing")]
    public void The_reporting_form_removes_a_file_or_link(string path)
    {
        using Dir root = _fs.OpenRoot();

        Assert.True(root.TryRemoveFileOrSymlink(path));
        Assert.False(_fs.Exists(path));
        Assert.False(root.TryRemoveFileOrSymlink(path));
        Assert.Equal("inner", _fs.ReadAllText("folder/inner.txt"));
    }

    /// <summary>A directory is refused and left, empty or not, by every form.</summary>
    [Theory]
    [InlineData("folder")]
    [InlineData("empty")]
    public void A_directory_is_refused(string path)
    {
        using Dir root = _fs.OpenRoot();

        CapIOException refused = Assert.Throws<CapIOException>(() => root.RemoveFileOrSymlink(path));
        Assert.Equal(CapErrorKind.IsADirectory, refused.Kind);
        Assert.False(root.TryRemoveFileOrSymlink(path));

        Assert.True(_fs.Exists("folder/inner.txt"));
        Assert.True(_fs.Exists("empty"));
    }

    /// <summary>A name holding nothing is reported as a missing file, or as false.</summary>
    [Fact]
    public void A_missing_name_is_reported()
    {
        using Dir root = _fs.OpenRoot();

        _ = Assert.Throws<FileNotFoundException>(() => root.RemoveFileOrSymlink("absent"));
        _ = Assert.Throws<DirectoryNotFoundException>(() => root.RemoveFileOrSymlink("missing/absent"));
        Assert.False(root.TryRemoveFileOrSymlink("absent"));
        Assert.False(root.TryRemoveFileOrSymlink("missing/absent"));
    }

    /// <summary>
    /// A path spelled to name a directory, and one leading out of the handle, are refused by
    /// every form, since neither is a name holding nothing.
    /// </summary>
    [Fact]
    public void A_misspelled_or_escaping_path_is_refused_by_every_form()
    {
        using Dir root = _fs.OpenRoot();

        _ = Assert.Throws<ArgumentException>(() => root.RemoveFileOrSymlink("to-folder/"));
        _ = Assert.Throws<ArgumentException>(() => root.TryRemoveFileOrSymlink("to-folder/"));
        _ = Assert.Throws<ArgumentException>(() => root.RemoveFileOrSymlink("folder/.."));
        _ = Assert.Throws<ArgumentException>(() => root.TryRemoveFileOrSymlink("folder/.."));
        _ = Assert.Throws<SandboxEscapeException>(() => root.RemoveFileOrSymlink("../report.txt"));
        _ = Assert.Throws<SandboxEscapeException>(() => root.TryRemoveFileOrSymlink("../report.txt"));
        Assert.True(_fs.Exists("to-folder"));
    }

    /// <summary>
    /// A link ahead of the last component is resolved as for any other path, so the name it
    /// leads to is the one removed, and a policy that refuses links refuses it.
    /// </summary>
    [Fact]
    public void A_link_ahead_of_the_name_is_resolved_under_the_handles_policy()
    {
        using (Dir strict = _fs.OpenRoot(SymlinkPolicy.Deny))
        {
            _ = Assert.ThrowsAny<IOException>(() => strict.RemoveFileOrSymlink("to-folder/inner.txt"));
            Assert.False(strict.TryRemoveFileOrSymlink("to-folder/inner.txt"));
        }

        using Dir root = _fs.OpenRoot();
        root.RemoveFileOrSymlink("to-folder/inner.txt");

        Assert.False(_fs.Exists("folder/inner.txt"));
        Assert.True(_fs.Exists("to-folder"));
    }

    /// <summary>
    /// Under Windows rules a link made to name a directory is removed, through a
    /// <see cref="Dir"/> and through a handle that removes such a link only as a directory.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_directory_link_under_Windows_rules_is_removed(bool splitsLinkRemoval)
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Windows });
        fs.AddFile("folder/inner.txt", "inner");
        fs.AddSymbolicLink("to-folder", "folder", targetIsDirectory: true);
        using RecordingDir root = new(fs.OpenRoot()) { SplitsLinkRemoval = splitsLinkRemoval };

        root.RemoveFileOrSymlink("to-folder");

        Assert.False(fs.Exists("to-folder"));
        Assert.Equal("inner", fs.ReadAllText("folder/inner.txt"));
        Assert.Equal(
            splitsLinkRemoval
                ? [".: TryDeleteFile(to-folder)", ".: TryDeleteDir(to-folder)"]
                : [".: TryDeleteFile(to-folder)"],
            root.Log.Where(call => call.Contains("Delete", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A handle that removes links only as directories still never has a real directory handed
    /// to its directory removal.
    /// </summary>
    [Fact]
    public void A_real_directory_never_reaches_the_directory_removal()
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Windows });
        fs.AddDirectory("empty");
        using RecordingDir root = new(fs.OpenRoot()) { SplitsLinkRemoval = true };

        _ = Assert.Throws<CapIOException>(() => root.RemoveFileOrSymlink("empty"));
        Assert.False(root.TryRemoveFileOrSymlink("empty"));

        Assert.True(fs.Exists("empty"));
        Assert.DoesNotContain(root.Log, call => call.Contains("Delete", StringComparison.Ordinal));
    }

    /// <summary>The asynchronous forms remove and answer as the synchronous ones do.</summary>
    [Fact]
    public async Task The_asynchronous_forms_remove_a_file_or_link()
    {
        using Dir root = _fs.OpenRoot();

        await root.RemoveFileOrSymlinkAsync("to-folder", TestContext.Current.CancellationToken);
        Assert.True(await root.TryRemoveFileOrSymlinkAsync("to-file", TestContext.Current.CancellationToken));
        Assert.False(await root.TryRemoveFileOrSymlinkAsync("to-file", TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<CapIOException>(
            () => root.RemoveFileOrSymlinkAsync("folder", TestContext.Current.CancellationToken));

        Assert.False(_fs.Exists("to-folder"));
        Assert.False(_fs.Exists("to-file"));
        Assert.Equal("contents", _fs.ReadAllText("report.txt"));
    }

    /// <summary>An asynchronous form already cancelled removes nothing, and does not answer false.</summary>
    [Fact]
    public async Task An_asynchronous_removal_already_cancelled_removes_nothing()
    {
        using Dir root = _fs.OpenRoot();
        CancellationToken cancelled = new(canceled: true);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => root.RemoveFileOrSymlinkAsync("report.txt", cancelled));
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => root.TryRemoveFileOrSymlinkAsync("report.txt", cancelled));

        Assert.True(_fs.Exists("report.txt"));
    }

    /// <summary>Arguments are checked before anything is done.</summary>
    [Fact]
    public void A_null_argument_is_refused()
    {
        using Dir root = _fs.OpenRoot();

        _ = Assert.Throws<ArgumentNullException>(() => root.RemoveFileOrSymlink(null!));
        _ = Assert.Throws<ArgumentNullException>(() => ((IDir)null!).TryRemoveFileOrSymlink("x"));
        _ = Assert.Throws<ArgumentNullException>(() => { _ = root.RemoveFileOrSymlinkAsync(null!, TestContext.Current.CancellationToken); });
        _ = Assert.Throws<ArgumentNullException>(() => { _ = ((IDir)null!).TryRemoveFileOrSymlinkAsync("x", TestContext.Current.CancellationToken); });
    }
}
