using System.Diagnostics;
using Cap.Primitives;
using Cap.Std;
using Cap.Std.Testing;
using Cap.Tests;

namespace Cap.Fs.Ext.Tests;

/// <summary>
/// Copying a tree from one handle to another.
/// </summary>
/// <remarks>
/// <para>
/// Most of what is asserted here is about the things a copy must not quietly do. A copy that
/// followed a symbolic link would reach outside the tree it was given and write what it found
/// there into the destination under an innocent name; a copy that read a named pipe would
/// block until something wrote to it, or fill the disk if something did; a copy that turned
/// either of them into an ordinary file would produce a destination that looks like the source
/// and is not.
/// </para>
/// <para>
/// The behaviour for each kind is a documented table, so it is tested as one: every kind, and
/// every setting the caller can choose for it.
/// </para>
/// </remarks>
public sealed class CopyTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>Files and directories arrive with their contents and their shape.</summary>
    [Fact]
    public void A_tree_of_files_and_directories_is_reproduced()
    {
        Make("source", "top.txt");
        Make("source", "a", "b", "deep.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));

        CopyReport report = Copy();

        Assert.Equal("contents", HostFile.ReadAllText(Path.Combine(_tree.HostPath, "destination", "top.txt")));
        Assert.Equal(
            "contents",
            HostFile.ReadAllText(Path.Combine(_tree.HostPath, "destination", "a", "b", "deep.txt")));
        Assert.Equal(2, report.Files);
        Assert.Equal(2, report.Directories);
        Assert.Equal(0, report.Skipped);
    }

    /// <summary>A symbolic link stops the copy unless the caller has said what to do with one.</summary>
    [Fact]
    public void A_link_stops_the_copy_by_default()
    {
        Make("outside", "secret.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "source"));
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        HostDirectory.CreateSymbolicLink(
            Path.Combine(_tree.HostPath, "source", "escape"), Path.Combine("..", "outside"));

        Assert.Throws<CapIOException>(() => Copy());

        // And above all, what the link pointed at was not reached: a copy that followed it
        // would have written the contents of a directory outside the source into the
        // destination under the link's name.
        Assert.False(HostEntry.Exists(Path.Combine(_tree.HostPath, "destination", "escape")));
    }

    /// <summary>A symbolic link can be left out.</summary>
    [Fact]
    public void A_link_can_be_skipped()
    {
        Make("outside", "secret.txt");
        Make("source", "kept.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        HostDirectory.CreateSymbolicLink(
            Path.Combine(_tree.HostPath, "source", "escape"), Path.Combine("..", "outside"));

        CopyReport report = Copy(new CopyOptions { Symlinks = CopyAction.Skip });

        Assert.Equal(1, report.Skipped);
        Assert.Equal(1, report.Files);
        Assert.False(HostEntry.Exists(Path.Combine(_tree.HostPath, "destination", "escape")));
        Assert.True(HostFile.Exists(Path.Combine(_tree.HostPath, "destination", "kept.txt")));
    }

    /// <summary>A symbolic link can be made again, with its target text unchanged.</summary>
    /// <remarks>
    /// The text is copied rather than resolved, so a relative target that reached one place
    /// from the source reaches whatever the same text names from the destination. That is the
    /// only honest reading of what a link is, and it is why a recreated link is not the same
    /// promise as a copied file.
    /// </remarks>
    [Fact]
    public void A_link_can_be_made_again_with_the_same_target()
    {
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "source"));
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        HostDirectory.CreateSymbolicLink(
            Path.Combine(_tree.HostPath, "source", "pointer"), Path.Combine("..", "outside"));

        CopyReport report = Copy(new CopyOptions { Symlinks = CopyAction.Recreate });

        string copied = Path.Combine(_tree.HostPath, "destination", "pointer");
        Assert.Equal(1, report.Symlinks);
        Assert.Equal(Path.Combine("..", "outside"), HostEntry.LinkTarget(copied));
    }

    /// <summary>
    /// A link whose target is rooted cannot be made again beneath a handle, so it stops the
    /// copy, and whatever held its name in the destination is left alone even when
    /// overwriting was asked for.
    /// </summary>
    [Fact]
    public void A_link_with_a_rooted_target_stops_the_copy_before_the_destination_is_touched()
    {
        Make("outside", "secret.txt");
        Make("destination", "pointer");
        string rooted = Path.Combine(_tree.HostPath, "outside");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "source"));
        HostDirectory.CreateSymbolicLink(Path.Combine(_tree.HostPath, "source", "pointer"), rooted);

        SandboxEscapeException refusal = Assert.Throws<SandboxEscapeException>(
            () => Copy(new CopyOptions { Symlinks = CopyAction.Recreate, Overwrite = true }));

        Assert.Contains(rooted, refusal.Message, StringComparison.Ordinal);
        string existing = Path.Combine(_tree.HostPath, "destination", "pointer");
        Assert.Null(HostEntry.LinkTarget(existing));
        Assert.Equal("contents", HostFile.ReadAllText(existing));
    }

    /// <summary>A named pipe stops the copy, and can be skipped.</summary>
    /// <remarks>
    /// The kind that would do the most damage if it were read: a copy that opened it would
    /// wait for a writer that may never come, and a caller would see the operation hang with
    /// no explanation. Refused by default, left out on request, and never turned into a file.
    /// </remarks>
    [Fact]
    [NotInMemory("Needs a named pipe, which only the host's filesystem can hold.")]
    public void A_named_pipe_stops_the_copy_and_can_be_skipped()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("There is no filesystem object of this kind on this platform.");
            return;
        }

        Make("source", "kept.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        MakeFifo(Path.Combine(_tree.HostPath, "source", "pipe"));

        Assert.Throws<CapIOException>(() => Copy());

        CopyReport report = Copy(new CopyOptions
        {
            OtherKinds = CopyAction.Skip,
            Overwrite = true,
        });

        Assert.Equal(1, report.Skipped);
        Assert.False(HostEntry.Exists(Path.Combine(_tree.HostPath, "destination", "pipe")));
    }

    /// <summary>Asking for an object of a kind this cannot create is refused up front.</summary>
    [Fact]
    public void Recreating_a_pipe_or_a_device_is_refused_as_a_request()
    {
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "source"));
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));

        Assert.Throws<ArgumentException>(
            () => Copy(new CopyOptions { OtherKinds = CopyAction.Recreate }));
    }

    /// <summary>Two names for one file become two files.</summary>
    /// <remarks>
    /// Documented rather than clever. Preserving the sharing would mean a destination in which
    /// writing one file changes another, which is a property the source had and the copy's
    /// caller did not ask for — so the copy makes independent files and says so.
    /// </remarks>
    [Fact]
    public void A_hard_link_becomes_an_independent_file()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Making a second name for a file needs a privilege this test does not assume.");
            return;
        }

        Make("source", "original.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        MakeHardLink(
            Path.Combine(_tree.HostPath, "source", "original.txt"),
            Path.Combine(_tree.HostPath, "source", "second.txt"));

        CopyReport report = Copy();

        Assert.Equal(2, report.Files);

        string first = Path.Combine(_tree.HostPath, "destination", "original.txt");
        string second = Path.Combine(_tree.HostPath, "destination", "second.txt");
        HostFile.WriteAllText(first, "changed");
        Assert.Equal("contents", HostFile.ReadAllText(second));
    }

    /// <summary>A name already taken in the destination stops the copy.</summary>
    [Fact]
    public void A_name_already_taken_stops_the_copy()
    {
        Make("source", "report.txt");
        Make("destination", "report.txt");

        Assert.Throws<CapIOException>(() => Copy());
    }

    /// <summary>A name already taken is written over when the caller asks.</summary>
    [Fact]
    public void A_name_already_taken_is_written_over_when_asked()
    {
        Make("source", "report.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        HostFile.WriteAllText(Path.Combine(_tree.HostPath, "destination", "report.txt"), "stale");

        Copy(new CopyOptions { Overwrite = true });

        Assert.Equal(
            "contents",
            HostFile.ReadAllText(Path.Combine(_tree.HostPath, "destination", "report.txt")));
    }

    /// <summary>
    /// A link at a file's name in the destination is replaced by the file, and whatever it
    /// pointed at is left alone.
    /// </summary>
    /// <remarks>
    /// The case that matters is a link to another file in the same destination tree: a copy
    /// that opened the name for writing would follow it and overwrite that other file, so
    /// whoever placed the link would choose which file in the tree the copy rewrites.
    /// </remarks>
    [Fact]
    public void A_link_at_a_file_name_is_replaced_rather_than_written_through()
    {
        Make("source", "report.txt");
        string destination = Path.Combine(_tree.HostPath, "destination");
        HostDirectory.CreateDirectory(destination);
        HostFile.WriteAllText(Path.Combine(destination, "keep.txt"), "untouched");
        HostFile.CreateSymbolicLink(Path.Combine(destination, "report.txt"), "keep.txt");

        CopyReport report = Copy(new CopyOptions { Overwrite = true });

        string copied = Path.Combine(destination, "report.txt");
        Assert.Equal(1, report.Files);
        Assert.Null(HostEntry.LinkTarget(copied));
        Assert.Equal("contents", HostFile.ReadAllText(copied));
        Assert.Equal("untouched", HostFile.ReadAllText(Path.Combine(destination, "keep.txt")));
        Assert.Equal(
            ["keep.txt", "report.txt"],
            HostDirectory.GetFileSystemEntries(destination).Select(Path.GetFileName).Order());
    }

    /// <summary>
    /// A link at a file's name is replaced the same way wherever it points, including outside
    /// the destination and at nothing.
    /// </summary>
    [Theory]
    [InlineData("outside")]
    [InlineData("dangling")]
    public void A_link_at_a_file_name_is_replaced_wherever_it_points(string kind)
    {
        Make("source", "report.txt");
        Make("outside", "secret.txt");
        string destination = Path.Combine(_tree.HostPath, "destination");
        HostDirectory.CreateDirectory(destination);
        string target = kind == "outside"
            ? Path.Combine("..", "outside", "secret.txt")
            : "missing.txt";
        HostFile.CreateSymbolicLink(Path.Combine(destination, "report.txt"), target);

        Copy(new CopyOptions { Overwrite = true });

        string copied = Path.Combine(destination, "report.txt");
        Assert.Null(HostEntry.LinkTarget(copied));
        Assert.Equal("contents", HostFile.ReadAllText(copied));
        Assert.Equal("contents", HostFile.ReadAllText(Path.Combine(_tree.HostPath, "outside", "secret.txt")));
        Assert.False(HostFile.Exists(Path.Combine(destination, "missing.txt")));
    }

    /// <summary>
    /// A link at a directory's name in the destination stops the copy, and the directory it
    /// points at is not copied into.
    /// </summary>
    [Fact]
    public void A_link_at_a_directory_name_stops_the_copy_even_when_overwriting()
    {
        Make("source", "nested", "inner.txt");
        string destination = Path.Combine(_tree.HostPath, "destination");
        HostDirectory.CreateDirectory(Path.Combine(destination, "elsewhere"));
        HostDirectory.CreateSymbolicLink(Path.Combine(destination, "nested"), "elsewhere");

        Assert.Throws<CapIOException>(() => Copy(new CopyOptions { Overwrite = true }));

        Assert.Equal("elsewhere", HostEntry.LinkTarget(Path.Combine(destination, "nested")));
        Assert.Empty(HostDirectory.GetFileSystemEntries(Path.Combine(destination, "elsewhere")));
    }

    /// <summary>A directory where the source has a file stops the copy, and is left in place.</summary>
    [Fact]
    public void A_directory_at_a_file_name_stops_the_copy_even_when_overwriting()
    {
        Make("source", "report.txt");
        Make("destination", "report.txt", "inside.txt");

        Assert.Throws<CapIOException>(() => Copy(new CopyOptions { Overwrite = true }));

        string destination = Path.Combine(_tree.HostPath, "destination");
        Assert.True(HostFile.Exists(Path.Combine(destination, "report.txt", "inside.txt")));
        Assert.Equal(["report.txt"], HostDirectory.GetFileSystemEntries(destination).Select(Path.GetFileName));
    }

    /// <summary>
    /// A file where the source has a directory stops the copy, and is left in place, however
    /// the copy was told to treat names already taken.
    /// </summary>
    /// <remarks>
    /// Overwriting replaces files with files. Replacing a file with a directory would be a
    /// removal the caller did not ask for, of something the copy did not make.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_file_at_a_directory_name_stops_the_copy_even_when_overwriting(bool overwrite)
    {
        Make("source", "nested", "inner.txt");
        MakeBytes([1, 2, 3], "destination", "nested");

        Assert.ThrowsAny<IOException>(() => Copy(new CopyOptions { Overwrite = overwrite }));

        string destination = Path.Combine(_tree.HostPath, "destination");
        Assert.Equal([1, 2, 3], HostFile.ReadAllBytes(Path.Combine(destination, "nested")));
        Assert.Equal(["nested"], HostDirectory.GetFileSystemEntries(destination).Select(Path.GetFileName));
    }

    /// <summary>
    /// A file that is copied and then cannot be moved onto the name it replaces is reported
    /// against that name, not against the scratch file it was written to.
    /// </summary>
    [Fact]
    public void A_file_that_cannot_replace_its_name_is_reported_against_the_name()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("source/data.txt", "new");
        fs.AddFile("destination/data.txt", "old");
        fs.SetUndeletable("destination/data.txt");
        using Dir source = fs.OpenRoot("source");
        using Dir destination = fs.OpenRoot("destination");

        Exception? refused = Record.Exception(() => source.CopyTo(destination, new CopyOptions { Overwrite = true }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.NotNull(refused);
        Assert.IsType<UnauthorizedAccessException>(refused);
        Assert.Contains("'data.txt'", refused.Message);
        Assert.DoesNotContain("cap-", refused.Message);
        Assert.Equal("old", fs.ReadAllText("destination/data.txt"));
        Assert.Equal(["data.txt"], fs.GetEntries("destination"));
    }

    /// <summary>
    /// A file that cannot be read stops the copy, and leaves no scratch name and no partial
    /// copy of itself behind; what was copied before it is whole.
    /// </summary>
    /// <remarks>
    /// Arranged with a fault on a filesystem held in memory, so it runs on every leg, and with
    /// replacement asked for so that every file goes through a scratch name.
    /// </remarks>
    [Fact]
    public void A_failed_copy_leaves_no_scratch_file()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("source/1.txt", "one");
        fs.AddFile("source/2.txt", "two");
        fs.AddFile("source/3.txt", "three");
        fs.AddDirectory("destination");
        fs.SetUnreadable("source/2.txt");
        using Dir source = fs.OpenRoot("source");
        using Dir destination = fs.OpenRoot("destination");

        _ = Assert.Throws<UnauthorizedAccessException>(
            () => source.CopyTo(destination, new CopyOptions { Overwrite = true }, cancellationToken: TestContext.Current.CancellationToken));

        string[] left = [.. fs.GetEntries("destination")];
        Assert.DoesNotContain(left, name => name.StartsWith("cap-", StringComparison.Ordinal));
        Assert.DoesNotContain("2.txt", left);
        foreach (string name in left)
        {
            Assert.Equal(fs.ReadAllText($"source/{name}"), fs.ReadAllText($"destination/{name}"));
        }
    }

    /// <summary>Permissions are carried across when the caller asks for them.</summary>
    [Fact]
    public void Permissions_are_carried_across_when_asked()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Permissions here are attribute flags, which this case does not set.");
            return;
        }

        Make("source", "private.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        HostFile.SetUnixFileMode(
            Path.Combine(_tree.HostPath, "source", "private.txt"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Copy(new CopyOptions { PreservePermissions = true });

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            HostFile.GetUnixFileMode(Path.Combine(_tree.HostPath, "destination", "private.txt")));
    }

    /// <summary>
    /// A directory its owner cannot write to is copied with its permissions, which are given
    /// to the copy only after its contents are in.
    /// </summary>
    [Fact]
    public void A_read_only_directory_is_copied_with_its_permissions()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Permissions here are attribute flags, and none of them stops a directory being filled.");
            return;
        }

        const UnixFileMode Locked =
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        const UnixFileMode Open = Locked | UnixFileMode.UserWrite;

        Make("source", "locked", "inner.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        string sourceLocked = Path.Combine(_tree.HostPath, "source", "locked");
        string destinationLocked = Path.Combine(_tree.HostPath, "destination", "locked");
        HostFile.SetUnixFileMode(sourceLocked, Locked);
        try
        {
            Copy(new CopyOptions { PreservePermissions = true });

            Assert.Equal(Locked, HostFile.GetUnixFileMode(destinationLocked));
            Assert.Equal("contents", HostFile.ReadAllText(Path.Combine(destinationLocked, "inner.txt")));
        }
        finally
        {
            // Opened again so the scratch tree can be removed.
            HostFile.SetUnixFileMode(sourceLocked, Open);
            if (HostDirectory.Exists(destinationLocked))
            {
                HostFile.SetUnixFileMode(destinationLocked, Open);
            }
        }
    }

    /// <summary>Without being asked, a copy gets whatever a new file would get.</summary>
    [Fact]
    public void Permissions_are_not_carried_across_unless_asked()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Permissions here are attribute flags, which this case does not set.");
            return;
        }

        Make("source", "private.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        HostFile.SetUnixFileMode(
            Path.Combine(_tree.HostPath, "source", "private.txt"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Copy();

        Assert.NotEqual(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            HostFile.GetUnixFileMode(Path.Combine(_tree.HostPath, "destination", "private.txt")));
    }

    /// <summary>
    /// When asked, files, directories and recreated links arrive with the source's times,
    /// whether the copy creates each file or replaces one already there.
    /// </summary>
    /// <remarks>
    /// The directory is the case that needs care: every entry the copy makes inside it moves
    /// its last-write time on, so its times have to be set after its contents, not when it is
    /// created.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Times_are_carried_across_when_asked(bool overwrite)
    {
        DateTimeOffset written = new DateTimeOffset(2003, 4, 5, 6, 7, 8, TimeSpan.Zero).AddTicks(1234567);
        DateTimeOffset accessed = new DateTimeOffset(2004, 5, 6, 7, 8, 9, TimeSpan.Zero).AddTicks(7654321);

        Make("source", "top.txt");
        Make("source", "nested", "inner.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        HostFile.CreateSymbolicLink(Path.Combine(_tree.HostPath, "source", "pointer"), "top.txt");
        if (overwrite)
        {
            Make("destination", "top.txt");
        }

        Dir root = _tree.Directory;
        foreach (string name in (string[])["source/top.txt", "source/nested/inner.txt", "source/pointer", "source/nested"])
        {
            root.SetTimes(name, CapFileTime.At(accessed), CapFileTime.At(written));
        }

        Copy(new CopyOptions { PreserveTimes = true, Symlinks = CopyAction.Recreate, Overwrite = overwrite });

        foreach (string name in (string[])["destination/top.txt", "destination/nested/inner.txt", "destination/pointer", "destination/nested"])
        {
            CapMetadata copied = root.GetMetadata(name);
            Assert.Equal(written, copied.LastWriteTime);
            Assert.Equal(accessed, copied.LastAccessTime);
        }

        // Recreated as a link, so the times checked above are the link's own.
        Assert.Equal(CapFileType.Symlink, root.GetMetadata("destination/pointer").Type);
    }

    /// <summary>Without being asked, a copy carries the time it was made, as any new file does.</summary>
    [Fact]
    public void Times_are_not_carried_across_unless_asked()
    {
        DateTimeOffset written = new(2003, 4, 5, 6, 7, 8, TimeSpan.Zero);

        Make("source", "top.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        _tree.Directory.SetTimes("source/top.txt", lastWrite: CapFileTime.At(written));

        Copy();

        Assert.NotEqual(written, _tree.Directory.GetMetadata("destination/top.txt").LastWriteTime);
    }

    /// <summary>
    /// The directory the copy writes into keeps its own times: the copy fills it and does not
    /// reproduce it.
    /// </summary>
    [Fact]
    public void The_destination_directory_keeps_its_own_times()
    {
        DateTimeOffset written = new(2003, 4, 5, 6, 7, 8, TimeSpan.Zero);

        Make("source", "top.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        _tree.Directory.SetTimes("source", lastWrite: CapFileTime.At(written));

        Copy(new CopyOptions { PreserveTimes = true });

        Assert.NotEqual(written, _tree.Directory.GetMetadata("destination").LastWriteTime);
    }

    /// <summary>A copy whose destination lies inside its source is refused.</summary>
    /// <remarks>
    /// Left to run it would copy what it had just written, and then copy that, without end.
    /// Noticed by identity rather than by comparing names, because the two directories can be
    /// reached by different names and a name is the one thing that can be reassigned.
    /// </remarks>
    [Fact]
    public void A_destination_inside_the_source_is_refused()
    {
        Make("source", "top.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "source", "destination"));

        using Dir source = _tree.Directory.OpenDir("source");
        using Dir destination = source.OpenDir("destination");

        CapIOException refused = Assert.Throws<CapIOException>(() => source.CopyTo(destination, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(CapErrorKind.InvalidArgument, refused.Kind);

        // Refused when the copy reached the destination, so nothing was copied into itself:
        // what is there, if anything, is the file listed ahead of it, whole, and no scratch.
        string inside = Path.Combine(_tree.HostPath, "source", "destination");
        string[] left = [.. HostDirectory.GetFileSystemEntries(inside).Select(Path.GetFileName)!];
        Assert.True(left is [] or ["top.txt"], $"The destination held: {string.Join(", ", left)}");
        if (left is ["top.txt"])
        {
            Assert.Equal("contents", HostFile.ReadAllText(Path.Combine(inside, "top.txt")));
        }

        Assert.Equal(["destination", "top.txt"], HostDirectory.GetFileSystemEntries(Path.Combine(_tree.HostPath, "source")).Select(Path.GetFileName).Order());
    }

    /// <summary>
    /// Times are carried across between two handles opened separately on one filesystem, a
    /// recreated link's own times included.
    /// </summary>
    /// <remarks>
    /// The other cases derive both handles from one root. Opening each from the ambient path
    /// gives two roots with nothing in common but the filesystem, which is the shape a caller
    /// copying between two places it was handed has.
    /// </remarks>
    [Fact]
    public void A_copy_between_two_roots_on_one_filesystem_preserves_link_times()
    {
        DateTimeOffset written = new(2003, 4, 5, 6, 7, 8, TimeSpan.Zero);
        DateTimeOffset accessed = new(2004, 5, 6, 7, 8, 9, TimeSpan.Zero);
        Make("source", "top.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        HostFile.CreateSymbolicLink(Path.Combine(_tree.HostPath, "source", "pointer"), "top.txt");
        _tree.Directory.SetTimes("source/pointer", CapFileTime.At(accessed), CapFileTime.At(written));

        using (Dir source = Dir.Open(Path.Combine(_tree.HostPath, "source"), AmbientAuthority.Acquire()))
        using (Dir destination = Dir.Open(Path.Combine(_tree.HostPath, "destination"), AmbientAuthority.Acquire()))
        {
            source.CopyTo(destination, new CopyOptions { PreserveTimes = true, Symlinks = CopyAction.Recreate }, cancellationToken: TestContext.Current.CancellationToken);
        }

        CapMetadata copied = _tree.Directory.GetMetadata("destination/pointer");
        Assert.Equal(CapFileType.Symlink, copied.Type);
        Assert.Equal(written, copied.LastWriteTime);
        Assert.Equal(accessed, copied.LastAccessTime);
    }

    /// <summary>
    /// A refusal to set a copied file's times stops the copy, and the file whose times could
    /// not be set is not left behind as though it had been copied.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_refused_time_change_stops_the_copy(bool overwrite)
    {
        Make("source", "top.txt");
        string destination = Path.Combine(_tree.HostPath, "destination");
        HostDirectory.CreateDirectory(destination);
        if (overwrite)
        {
            HostFile.WriteAllText(Path.Combine(destination, "top.txt"), "old");
        }

        UnauthorizedAccessException refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(
            asynchronous: false,
            new CopyOptions { PreserveTimes = true, Overwrite = overwrite },
            observe: call =>
            {
                if (call.Contains(": SetTimes(", StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("The times may not be changed.");
                }
            }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("The times may not be changed.", refused.Message);
        Assert.DoesNotContain(
            HostDirectory.GetFileSystemEntries(destination),
            entry => Path.GetFileName(entry).StartsWith("cap-", StringComparison.Ordinal));
        if (overwrite)
        {
            Assert.Equal("old", HostFile.ReadAllText(Path.Combine(destination, "top.txt")));
        }
        else
        {
            Assert.Empty(HostDirectory.GetFileSystemEntries(destination));
        }
    }

    /// <summary>
    /// A destination deeper inside the source is refused when the copy reaches it, and what
    /// was copied on the way down is left behind.
    /// </summary>
    /// <remarks>
    /// The residue is pinned because the documentation promises it: the directories above the
    /// destination have been recreated inside it by the time the copy finds out where it is.
    /// The source holds nothing else, so what is left does not depend on listing order.
    /// </remarks>
    [Fact]
    public void A_destination_deep_inside_the_source_leaves_the_directories_above_it_behind()
    {
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "source", "outer", "destination"));

        using Dir source = _tree.Directory.OpenDir("source");
        using Dir destination = source.OpenDir(Path.Combine("outer", "destination"));

        CapIOException refused = Assert.Throws<CapIOException>(() => source.CopyTo(destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(CapErrorKind.InvalidArgument, refused.Kind);
        string left = Path.Combine(_tree.HostPath, "source", "outer", "destination");
        Assert.Equal(["outer"], HostDirectory.GetFileSystemEntries(left).Select(Path.GetFileName));
        Assert.Empty(HostDirectory.GetFileSystemEntries(Path.Combine(left, "outer")));
    }

    /// <summary>
    /// Two handles on one directory are refused before anything is read or written, whether
    /// or not files may be replaced.
    /// </summary>
    /// <remarks>
    /// Without the refusal, a copy that may not replace stops at the first directory with a
    /// name it did not expect to find taken, and one that may replace rewrites every file onto
    /// itself as a new file while listing the directory it is changing, then reports success.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_copy_onto_itself_is_refused_before_anything_is_written(bool overwrite)
    {
        Make("source", "a.txt");
        Make("source", "sub", "b.txt");
        DateTime written = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        HostFile.SetLastWriteTimeUtc(Path.Combine(_tree.HostPath, "source", "a.txt"), written);
        HostFile.SetLastWriteTimeUtc(Path.Combine(_tree.HostPath, "source", "sub", "b.txt"), written);

        using Dir source = _tree.Directory.OpenDir("source");
        using Dir same = _tree.Directory.OpenDir("source");
        CapFileId a = source.GetMetadata("a.txt").FileId;
        CapFileId b = source.GetMetadata(Path.Combine("sub", "b.txt")).FileId;

        CapIOException refused = Assert.Throws<CapIOException>(
            () => source.CopyTo(same, new CopyOptions { Overwrite = overwrite }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(CapErrorKind.InvalidArgument, refused.Kind);
        Assert.Equal(a, source.GetMetadata("a.txt").FileId);
        Assert.Equal(b, source.GetMetadata(Path.Combine("sub", "b.txt")).FileId);
        Assert.Equal(written, HostFile.GetLastWriteTimeUtc(Path.Combine(_tree.HostPath, "source", "a.txt")));
        Assert.Equal(written, HostFile.GetLastWriteTimeUtc(Path.Combine(_tree.HostPath, "source", "sub", "b.txt")));
        Assert.Equal(
            ["a.txt", "sub"],
            HostDirectory.GetFileSystemEntries(Path.Combine(_tree.HostPath, "source")).Select(Path.GetFileName).Order());
        Assert.Equal(
            ["b.txt"],
            HostDirectory.GetFileSystemEntries(Path.Combine(_tree.HostPath, "source", "sub")).Select(Path.GetFileName));
    }

    /// <summary>
    /// A copy onto itself through handles that are not a <see cref="Dir"/> is refused having
    /// done nothing but ask each handle what it is.
    /// </summary>
    /// <remarks>
    /// Two wrappers compare identities only when both report a backend on the host's own
    /// filesystem, which a filesystem held in memory is not.
    /// </remarks>
    [Fact]
    [NotInMemory("Identities of handles that are not a Dir are compared only on the host's filesystem.")]
    public void A_copy_onto_itself_through_the_interface_is_refused_without_reading_or_writing()
    {
        Make("source", "a.txt");
        Make("source", "sub", "b.txt");

        List<string> log = [];
        using RecordingDir source = new(_tree.Directory.OpenDir("source"), log, "src");
        using RecordingDir same = new(_tree.Directory.OpenDir("source"), log, "same");

        CapIOException refused = Assert.Throws<CapIOException>(
            () => source.CopyTo(same, new CopyOptions { Overwrite = true }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(CapErrorKind.InvalidArgument, refused.Kind);
        Assert.Equal(["same: GetMetadata()", "src: GetMetadata()"], log);
    }

    /// <summary>A source deeper than the limit stops the copy.</summary>
    [Fact]
    public void A_source_deeper_than_the_limit_is_refused()
    {
        string path = Path.Combine(_tree.HostPath, "source");
        for (int i = 0; i < 8; i++)
        {
            path = Path.Combine(path, "level");
            HostDirectory.CreateDirectory(path);
        }

        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));

        Assert.Throws<CapIOException>(() => Copy(new CopyOptions { MaxDepth = 3 }));
    }

    /// <summary>An empty directory at the limit is copied.</summary>
    [Fact]
    public void An_empty_directory_at_the_limit_is_copied()
    {
        Make("source", "f.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "source", "e"));
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));

        CopyReport report = Copy(new CopyOptions { MaxDepth = 1 });

        Assert.Equal(1, report.Directories);
        Assert.Equal(1, report.Files);
        Assert.True(HostDirectory.Exists(Path.Combine(_tree.HostPath, "destination", "e")));
        Assert.True(HostFile.Exists(Path.Combine(_tree.HostPath, "destination", "f.txt")));
    }

    /// <summary>
    /// A directory at the limit with anything in it fails the copy, with nothing created for
    /// it in the destination.
    /// </summary>
    [Fact]
    public void A_non_empty_directory_at_the_limit_fails_the_copy()
    {
        Make("source", "a", "x.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));

        CapIOException thrown = Assert.Throws<CapIOException>(() => Copy(new CopyOptions { MaxDepth = 1 }));

        Assert.Equal(CapErrorKind.PathTooDeep, thrown.Kind);
        Assert.False(HostDirectory.Exists(Path.Combine(_tree.HostPath, "destination", "a")));
    }

    /// <summary>A source exactly as deep as the limit is copied to its end.</summary>
    [Fact]
    public void A_source_exactly_as_deep_as_the_limit_is_copied_to_its_end()
    {
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "source", "1", "2", "3"));
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));

        CopyReport report = Copy(new CopyOptions { MaxDepth = 3 });

        Assert.Equal(3, report.Directories);
        Assert.True(HostDirectory.Exists(Path.Combine(_tree.HostPath, "destination", "1", "2", "3")));
    }

    /// <summary>The asynchronous copy reproduces a tree as the synchronous one does.</summary>
    [Fact]
    public async Task The_asynchronous_copy_reproduces_the_tree()
    {
        Make("source", "top.txt");
        Make("source", "a", "b", "deep.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));

        CopyReport report = await Run(asynchronous: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("contents", HostFile.ReadAllText(Path.Combine(_tree.HostPath, "destination", "top.txt")));
        Assert.Equal(
            "contents",
            HostFile.ReadAllText(Path.Combine(_tree.HostPath, "destination", "a", "b", "deep.txt")));
        Assert.Equal(2, report.Files);
        Assert.Equal(2, report.Directories);
        Assert.Equal(0, report.Skipped);
        Assert.Equal(2 * "contents".Length, report.Bytes);
    }

    /// <summary>
    /// Progress is reported once per entry, its counts never go down, and the last report is
    /// the one returned.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Progress_is_reported_monotonically(bool asynchronous)
    {
        Make("source", "top.txt");
        Make("source", "a", "one.txt");
        Make("source", "a", "b", "two.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        List<CopyReport> reports = [];

        CopyReport report = await Run(asynchronous, progress: new Reports(reports.Add), cancellationToken: TestContext.Current.CancellationToken);

        // Three files and two directories, each reported as it was done.
        Assert.Equal(5, reports.Count);
        for (int i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].Files >= reports[i - 1].Files);
            Assert.True(reports[i].Directories >= reports[i - 1].Directories);
            Assert.True(reports[i].Bytes >= reports[i - 1].Bytes);
            Assert.Equal(
                Total(reports[i - 1]) + 1,
                Total(reports[i]));
        }

        Assert.Equal(Counts(report), Counts(reports[^1]));
    }

    /// <summary>
    /// A copy stopped between files keeps the files it finished and leaves no scratch name.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancelled_copy_leaves_no_scratch_file_and_keeps_what_was_copied(bool asynchronous)
    {
        byte[] contents = Contents(1024 * 1024);
        for (int i = 0; i < 4; i++)
        {
            MakeBytes(contents, "source", $"file{i}.bin");
        }

        string destination = Path.Combine(_tree.HostPath, "destination");
        HostDirectory.CreateDirectory(destination);
        using CancellationTokenSource cancel = new();
        Reports progress = new(report =>
        {
            if (report.Files == 1)
            {
                cancel.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Run(asynchronous, new CopyOptions { Overwrite = true }, progress, cancellationToken: cancel.Token));

        string copied = Assert.Single(HostDirectory.GetFileSystemEntries(destination));
        Assert.Equal(contents, HostFile.ReadAllBytes(copied));
    }

    /// <summary>
    /// A copy stopped part of the way through a file leaves no part of that file under any
    /// name, and whatever the name held before is left holding it.
    /// </summary>
    /// <remarks>
    /// Stopped from inside the file's second write, which is past the point where a file
    /// created under its real name has contents in it, and so past the point where leaving it
    /// would leave a truncated file looking like a copied one.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_copy_cancelled_part_way_through_a_file_leaves_no_part_of_it(bool overwrite, bool asynchronous)
    {
        MakeBytes(Contents(1024 * 1024), "source", "big.bin");
        string destination = Path.Combine(_tree.HostPath, "destination");
        HostDirectory.CreateDirectory(destination);
        if (overwrite)
        {
            HostFile.WriteAllText(Path.Combine(destination, "big.bin"), "old");
        }

        using CancellationTokenSource cancel = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(
            asynchronous,
            new CopyOptions { Overwrite = overwrite },
            cancellationToken: cancel.Token,
            observe: call =>
            {
                if (IsLaterWrite(call))
                {
                    cancel.Cancel();
                }
            }));

        AssertNothingHalfWritten(destination, overwrite);
    }

    /// <summary>
    /// A copy that fails part of the way through a file removes what it had written of it,
    /// as a cancelled one does.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_copy_that_fails_part_way_through_a_file_leaves_no_part_of_it(bool overwrite, bool asynchronous)
    {
        MakeBytes(Contents(1024 * 1024), "source", "big.bin");
        string destination = Path.Combine(_tree.HostPath, "destination");
        HostDirectory.CreateDirectory(destination);
        if (overwrite)
        {
            HostFile.WriteAllText(Path.Combine(destination, "big.bin"), "old");
        }

        IOException injected = await Assert.ThrowsAsync<IOException>(() => Run(
            asynchronous,
            new CopyOptions { Overwrite = overwrite },
            observe: call =>
            {
                if (IsLaterWrite(call))
                {
                    throw new IOException("The device failed part of the way through the file.");
                }
            }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("The device failed part of the way through the file.", injected.Message);
        AssertNothingHalfWritten(destination, overwrite);
    }

    /// <summary>A copy asked to stop before it starts writes nothing.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_copy_already_cancelled_writes_nothing(bool asynchronous)
    {
        Make("source", "top.txt");
        Make("source", "a", "deep.txt");
        string destination = Path.Combine(_tree.HostPath, "destination");
        HostDirectory.CreateDirectory(destination);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Run(asynchronous, cancellationToken: new CancellationToken(canceled: true)));

        Assert.Empty(HostDirectory.GetFileSystemEntries(destination));
    }

    /// <summary>
    /// Whether a logged call is a write into a file past its first piece, which only a file
    /// larger than one piece has.
    /// </summary>
    private static bool IsLaterWrite(string call) =>
        call.Contains(": Write", StringComparison.Ordinal) && !call.EndsWith(", 0)", StringComparison.Ordinal);

    /// <summary>
    /// Asserts that the destination holds no scratch name, and that <c>big.bin</c> is absent
    /// or, when it was there to be replaced, still holds what it held.
    /// </summary>
    private static void AssertNothingHalfWritten(string destination, bool overwrite)
    {
        Assert.DoesNotContain(
            HostDirectory.GetFileSystemEntries(destination),
            entry => Path.GetFileName(entry).StartsWith("cap-", StringComparison.Ordinal));

        string big = Path.Combine(destination, "big.bin");
        if (overwrite)
        {
            Assert.Equal("old", HostFile.ReadAllText(big));
        }
        else
        {
            Assert.False(HostEntry.Exists(big));
        }
    }

    /// <summary>
    /// Copies the scratch tree's source directory into its destination directory, in the form
    /// asked for, optionally through a destination that shows each call to an observer.
    /// </summary>
    private async Task<CopyReport> Run(
        bool asynchronous,
        CopyOptions? options = null,
        IProgress<CopyReport>? progress = null,
        Action<string>? observe = null,
        CancellationToken cancellationToken = default)
    {
        using Dir source = _tree.Directory.OpenDir("source");
        using Dir opened = _tree.Directory.OpenDir("destination");

        // Not disposed: disposing it would close the handle beneath it a second time.
        IDir destination = observe is null ? opened : new RecordingDir(opened, [], "dest", observe);

        return asynchronous
            ? await source.CopyToAsync(destination, options, progress, cancellationToken)
            : source.CopyTo(destination, options, progress, cancellationToken);
    }

    /// <summary>The counts in a report, in a form that compares by value.</summary>
    private static (int Directories, int Files, int Symlinks, int Skipped, long Bytes) Counts(CopyReport report) =>
        (report.Directories, report.Files, report.Symlinks, report.Skipped, report.Bytes);

    /// <summary>How many entries a report accounts for.</summary>
    private static int Total(CopyReport report) =>
        report.Directories + report.Files + report.Symlinks + report.Skipped;

    /// <summary>Contents that differ from one position to the next, so a misplaced piece shows.</summary>
    private static byte[] Contents(int length)
    {
        byte[] contents = new byte[length];
        for (int i = 0; i < length; i++)
        {
            contents[i] = (byte)(i * 31 % 251);
        }

        return contents;
    }

    /// <summary>Creates a file holding the given bytes, and whatever directories it needs.</summary>
    private void MakeBytes(byte[] contents, params string[] parts)
    {
        string path = Path.Combine([_tree.HostPath, .. parts]);
        HostDirectory.CreateDirectory(Path.GetDirectoryName(path)!);
        HostFile.WriteAllBytes(path, contents);
    }

    /// <summary>Copies the scratch tree's source directory into its destination directory.</summary>
    private CopyReport Copy(CopyOptions? options = null)
    {
        using Dir source = _tree.Directory.OpenDir("source");
        using Dir destination = _tree.Directory.OpenDir("destination");

        return source.CopyTo(destination, options);
    }

    /// <summary>Creates a file, and whatever directories it needs, under the scratch tree.</summary>
    private void Make(params string[] parts)
    {
        string path = Path.Combine([_tree.HostPath, .. parts]);
        HostDirectory.CreateDirectory(Path.GetDirectoryName(path)!);
        HostFile.WriteAllText(path, "contents");
    }

    /// <summary>Creates a named pipe, which the framework has no call for.</summary>
    private static void MakeFifo(string path) => Run("mkfifo", path);

    /// <summary>Creates a second name for an existing file.</summary>
    private static void MakeHardLink(string existing, string added) => HostFile.CreateHardLink(existing, added);

    /// <summary>Runs one of the system's own tools, and insists that it worked.</summary>
    private static void Run(string program, params string[] arguments)
    {
        ProcessStartInfo start = new()
        {
            FileName = program,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ??
            throw new InvalidOperationException($"'{program}' did not start.");

        (_, string errors) = ChildProcessWait.Finish(process, $"'{program}'", TimeSpan.FromSeconds(30));
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{program}' failed with {process.ExitCode}: {errors}");
        }
    }

    /// <summary>
    /// Progress that is handed each report at once, on the copying thread, rather than posted
    /// elsewhere as <see cref="Progress{T}"/> would post it.
    /// </summary>
    private sealed class Reports(Action<CopyReport> report) : IProgress<CopyReport>
    {
        public void Report(CopyReport value) => report(value);
    }
}
