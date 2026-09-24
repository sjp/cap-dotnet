using System.Diagnostics;
using Cap.Std;

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
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));

        CopyReport report = Copy();

        Assert.Equal("contents", File.ReadAllText(Path.Combine(_tree.HostPath, "destination", "top.txt")));
        Assert.Equal(
            "contents",
            File.ReadAllText(Path.Combine(_tree.HostPath, "destination", "a", "b", "deep.txt")));
        Assert.Equal(2, report.Files);
        Assert.Equal(2, report.Directories);
        Assert.Equal(0, report.Skipped);
    }

    /// <summary>A symbolic link stops the copy unless the caller has said what to do with one.</summary>
    [Fact]
    public void A_link_stops_the_copy_by_default()
    {
        Make("outside", "secret.txt");
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "source"));
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        Directory.CreateSymbolicLink(
            Path.Combine(_tree.HostPath, "source", "escape"), Path.Combine("..", "outside"));

        Assert.Throws<CapIOException>(() => Copy());

        // And above all, what the link pointed at was not reached: a copy that followed it
        // would have written the contents of a directory outside the source into the
        // destination under the link's name.
        Assert.False(Path.Exists(Path.Combine(_tree.HostPath, "destination", "escape")));
    }

    /// <summary>A symbolic link can be left out.</summary>
    [Fact]
    public void A_link_can_be_skipped()
    {
        Make("outside", "secret.txt");
        Make("source", "kept.txt");
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        Directory.CreateSymbolicLink(
            Path.Combine(_tree.HostPath, "source", "escape"), Path.Combine("..", "outside"));

        CopyReport report = Copy(new CopyOptions { Symlinks = CopyAction.Skip });

        Assert.Equal(1, report.Skipped);
        Assert.Equal(1, report.Files);
        Assert.False(Path.Exists(Path.Combine(_tree.HostPath, "destination", "escape")));
        Assert.True(File.Exists(Path.Combine(_tree.HostPath, "destination", "kept.txt")));
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
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "source"));
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        Directory.CreateSymbolicLink(
            Path.Combine(_tree.HostPath, "source", "pointer"), Path.Combine("..", "outside"));

        CopyReport report = Copy(new CopyOptions { Symlinks = CopyAction.Recreate });

        string copied = Path.Combine(_tree.HostPath, "destination", "pointer");
        Assert.Equal(1, report.Symlinks);
        Assert.Equal(Path.Combine("..", "outside"), new FileInfo(copied).LinkTarget);
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
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "source"));
        Directory.CreateSymbolicLink(Path.Combine(_tree.HostPath, "source", "pointer"), rooted);

        SandboxEscapeException refusal = Assert.Throws<SandboxEscapeException>(
            () => Copy(new CopyOptions { Symlinks = CopyAction.Recreate, Overwrite = true }));

        Assert.Contains(rooted, refusal.Message, StringComparison.Ordinal);
        string existing = Path.Combine(_tree.HostPath, "destination", "pointer");
        Assert.Null(new FileInfo(existing).LinkTarget);
        Assert.Equal("contents", File.ReadAllText(existing));
    }

    /// <summary>A named pipe stops the copy, and can be skipped.</summary>
    /// <remarks>
    /// The kind that would do the most damage if it were read: a copy that opened it would
    /// wait for a writer that may never come, and a caller would see the operation hang with
    /// no explanation. Refused by default, left out on request, and never turned into a file.
    /// </remarks>
    [Fact]
    public void A_named_pipe_stops_the_copy_and_can_be_skipped()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("There is no filesystem object of this kind on this platform.");
            return;
        }

        Make("source", "kept.txt");
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        MakeFifo(Path.Combine(_tree.HostPath, "source", "pipe"));

        Assert.Throws<CapIOException>(() => Copy());

        CopyReport report = Copy(new CopyOptions
        {
            OtherKinds = CopyAction.Skip,
            Overwrite = true,
        });

        Assert.Equal(1, report.Skipped);
        Assert.False(Path.Exists(Path.Combine(_tree.HostPath, "destination", "pipe")));
    }

    /// <summary>Asking for an object of a kind this cannot create is refused up front.</summary>
    [Fact]
    public void Recreating_a_pipe_or_a_device_is_refused_as_a_request()
    {
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "source"));
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));

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
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        MakeHardLink(
            Path.Combine(_tree.HostPath, "source", "original.txt"),
            Path.Combine(_tree.HostPath, "source", "second.txt"));

        CopyReport report = Copy();

        Assert.Equal(2, report.Files);

        string first = Path.Combine(_tree.HostPath, "destination", "original.txt");
        string second = Path.Combine(_tree.HostPath, "destination", "second.txt");
        File.WriteAllText(first, "changed");
        Assert.Equal("contents", File.ReadAllText(second));
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
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        File.WriteAllText(Path.Combine(_tree.HostPath, "destination", "report.txt"), "stale");

        Copy(new CopyOptions { Overwrite = true });

        Assert.Equal(
            "contents",
            File.ReadAllText(Path.Combine(_tree.HostPath, "destination", "report.txt")));
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
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "keep.txt"), "untouched");
        File.CreateSymbolicLink(Path.Combine(destination, "report.txt"), "keep.txt");

        CopyReport report = Copy(new CopyOptions { Overwrite = true });

        FileInfo copied = new(Path.Combine(destination, "report.txt"));
        Assert.Equal(1, report.Files);
        Assert.Null(copied.LinkTarget);
        Assert.Equal("contents", File.ReadAllText(copied.FullName));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(destination, "keep.txt")));
        Assert.Equal(
            ["keep.txt", "report.txt"],
            Directory.GetFileSystemEntries(destination).Select(Path.GetFileName).Order());
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
        Directory.CreateDirectory(destination);
        string target = kind == "outside"
            ? Path.Combine("..", "outside", "secret.txt")
            : "missing.txt";
        File.CreateSymbolicLink(Path.Combine(destination, "report.txt"), target);

        Copy(new CopyOptions { Overwrite = true });

        FileInfo copied = new(Path.Combine(destination, "report.txt"));
        Assert.Null(copied.LinkTarget);
        Assert.Equal("contents", File.ReadAllText(copied.FullName));
        Assert.Equal("contents", File.ReadAllText(Path.Combine(_tree.HostPath, "outside", "secret.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "missing.txt")));
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
        Directory.CreateDirectory(Path.Combine(destination, "elsewhere"));
        Directory.CreateSymbolicLink(Path.Combine(destination, "nested"), "elsewhere");

        Assert.Throws<CapIOException>(() => Copy(new CopyOptions { Overwrite = true }));

        Assert.Equal("elsewhere", new DirectoryInfo(Path.Combine(destination, "nested")).LinkTarget);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(destination, "elsewhere")));
    }

    /// <summary>A directory where the source has a file stops the copy, and is left in place.</summary>
    [Fact]
    public void A_directory_at_a_file_name_stops_the_copy_even_when_overwriting()
    {
        Make("source", "report.txt");
        Make("destination", "report.txt", "inside.txt");

        Assert.Throws<CapIOException>(() => Copy(new CopyOptions { Overwrite = true }));

        string destination = Path.Combine(_tree.HostPath, "destination");
        Assert.True(File.Exists(Path.Combine(destination, "report.txt", "inside.txt")));
        Assert.Equal(["report.txt"], Directory.GetFileSystemEntries(destination).Select(Path.GetFileName));
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
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        File.SetUnixFileMode(
            Path.Combine(_tree.HostPath, "source", "private.txt"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Copy(new CopyOptions { PreservePermissions = true });

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(_tree.HostPath, "destination", "private.txt")));
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
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));
        File.SetUnixFileMode(
            Path.Combine(_tree.HostPath, "source", "private.txt"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Copy();

        Assert.NotEqual(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(_tree.HostPath, "destination", "private.txt")));
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
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "source", "destination"));

        using Dir source = _tree.Directory.OpenDir("source");
        using Dir destination = source.OpenDir("destination");

        Assert.Throws<CapIOException>(() => source.CopyTo(destination));
    }

    /// <summary>A source deeper than the limit stops the copy.</summary>
    [Fact]
    public void A_source_deeper_than_the_limit_is_refused()
    {
        string path = Path.Combine(_tree.HostPath, "source");
        for (int i = 0; i < 8; i++)
        {
            path = Path.Combine(path, "level");
            Directory.CreateDirectory(path);
        }

        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));

        Assert.Throws<CapIOException>(() => Copy(new CopyOptions { MaxDepth = 3 }));
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "contents");
    }

    /// <summary>Creates a named pipe, which the framework has no call for.</summary>
    private static void MakeFifo(string path) => Run("mkfifo", path);

    /// <summary>Creates a second name for an existing file.</summary>
    private static void MakeHardLink(string existing, string added) => Run("ln", existing, added);

    /// <summary>Runs one of the system's own tools, and insists that it worked.</summary>
    private static void Run(string program, params string[] arguments)
    {
        ProcessStartInfo start = new() { FileName = program, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ??
            throw new InvalidOperationException($"'{program}' did not start.");

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{program}' failed with {process.ExitCode}.");
        }
    }
}
