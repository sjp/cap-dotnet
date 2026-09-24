using Cap.Primitives;

namespace Cap.Std.Tests;

/// <summary>
/// Paths that climb with <c>..</c>, resolved beneath the handle.
/// </summary>
/// <remarks>
/// <para>
/// A <c>..</c> is walked, not collapsed as text and not refused on sight: a step back out of a
/// directory the path entered lands where it started, and a step taken at the handle's own
/// directory is refused as an escape whatever the rest of the path says. The escape corpus
/// holds every operation to that across backends and policies; what is here is the detail a
/// table of outcomes cannot state — which exception, with which kind, and that a refused
/// removal left everything where it was.
/// </para>
/// <para>
/// A path ending in <c>..</c> names a directory by where it sits rather than by a name in its
/// parent. It can be opened and described, and nothing can act on it as a name.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class DirParentStepTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private Dir OpenRoot() => Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

    private string Host(params string[] parts) => Path.Combine([_tree.HostPath, .. parts]);

    private void MakeFile(string contents, params string[] parts)
    {
        string path = Host(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    /// <summary>A path that climbs and descends again, staying inside, opens what it names.</summary>
    [Fact]
    public void A_climb_that_stays_inside_opens_the_file_it_names()
    {
        MakeFile("nested", "dir", "nested", "file");

        using Dir root = OpenRoot();

        Assert.Equal("nested", root.ReadAllText("dir/.//nested/../../dir/nested/../nested///./file"));
    }

    /// <summary>A climb one step too far is refused, even when the rest would come back inside.</summary>
    [Fact]
    public void A_climb_above_the_handle_is_refused_even_on_its_way_back_in()
    {
        MakeFile("nested", "dir", "nested", "file");

        using Dir root = OpenRoot();
        using Dir dir = root.OpenDir("dir");
        string back = $"../{Path.GetFileName(_tree.HostPath)}/dir/nested/file";

        Assert.Throws<SandboxEscapeException>(() => root.OpenFile("dir/nested/../../../dir/nested/file").Dispose());
        Assert.Throws<SandboxEscapeException>(() => dir.OpenFile("../dir/nested/file").Dispose());
        Assert.Throws<SandboxEscapeException>(() => root.OpenFile(back).Dispose());
    }

    /// <summary>
    /// A step back after a link lands beside where the link led, not beside the link.
    /// </summary>
    /// <remarks>
    /// The reason <c>..</c> is never collapsed as text. Collapsed, <c>hop/../file</c> would be
    /// <c>file</c> beside the link, which holds something else.
    /// </remarks>
    [Fact]
    public void A_climb_after_a_link_starts_from_where_the_link_led()
    {
        MakeFile("beside the target", "plain", "file");
        MakeFile("beside the link", "file");
        Directory.CreateDirectory(Host("plain", "inner"));
        Directory.CreateSymbolicLink(Host("hop"), Path.Combine("plain", "inner"));

        using Dir root = OpenRoot();

        Assert.Equal("beside the target", root.ReadAllText("hop/../file"));
    }

    /// <summary>A path ending in <c>..</c> opens and describes the directory it climbed back to.</summary>
    [Fact]
    public void A_path_ending_in_a_climb_opens_and_describes_the_directory_it_names()
    {
        Directory.CreateDirectory(Host("a", "b"));

        using Dir root = OpenRoot();
        using Dir a = root.OpenDir("a");
        using Dir climbed = root.OpenDir("a/b/..");

        Assert.Equal(a.GetMetadata().FileId, climbed.GetMetadata().FileId);
        Assert.Equal(a.GetMetadata().FileId, root.GetMetadata("a/b/..").FileId);
        Assert.Equal(root.GetMetadata().FileId, root.GetMetadata("a/..").FileId);
        Assert.True(root.Exists("a/b/.."));
        Assert.True(root.Exists("a/.."));
    }

    /// <summary>A path ending in a climb above the handle is refused, and is not there.</summary>
    [Fact]
    public void A_path_ending_in_a_climb_above_the_handle_is_refused()
    {
        Directory.CreateDirectory(Host("a"));

        using Dir root = OpenRoot();

        Assert.Throws<SandboxEscapeException>(() => root.OpenDir("a/../..").Dispose());
        Assert.Throws<SandboxEscapeException>(() => root.GetMetadata(".."));
        Assert.False(root.Exists(".."));
        Assert.False(root.TryGetMetadata("a/../..", out _));
    }

    /// <summary>
    /// Nothing that acts on a name can act on a path ending in <c>..</c>, and the directory it
    /// names is left alone.
    /// </summary>
    /// <remarks>
    /// Removing what <c>a/..</c> names would remove the handle's own directory, which the
    /// caller never spelled out; renaming or linking it would move or duplicate it. Each is
    /// refused as a request with no name in it, and a climb above the handle as an escape.
    /// </remarks>
    [Fact]
    public void A_path_ending_in_a_climb_is_not_a_name_anything_can_act_on()
    {
        MakeFile("kept", "a", "kept");
        MakeFile("source", "source");

        using Dir root = OpenRoot();

        Action[] acts =
        [
            () => root.CreateDir("a/..").Dispose(),
            () => root.DeleteDir("a/.."),
            () => root.DeleteFile("a/.."),
            () => root.Rename("a/..", root, "moved"),
            () => root.Rename("source", root, "a/.."),
            () => root.CreateSymlink("a/..", "source"),
            () => root.CreateHardLink("a/..", root, "linked"),
            () => root.CreateHardLink("source", root, "a/.."),
            () => root.ReadLink("a/.."),
        ];

        foreach (Action act in acts)
        {
            CapIOException thrown = Assert.ThrowsAny<CapIOException>(act);
            Assert.Equal(CapErrorKind.InvalidArgument, thrown.Kind);
        }

        Assert.False(root.TryDeleteDir("a/.."));
        Assert.Throws<SandboxEscapeException>(() => root.DeleteDir(".."));
        Assert.Throws<SandboxEscapeException>(() => root.DeleteFile("a/../.."));
        Assert.Throws<DirectoryNotFoundException>(() => root.DeleteDir("absent/.."));

        Assert.Equal("kept", File.ReadAllText(Host("a", "kept")));
        Assert.True(File.Exists(Host("source")));
        Assert.False(File.Exists(Host("moved")) || File.Exists(Host("linked")));
    }

    /// <summary>
    /// A file opened by a path spelled as a directory is refused by what the name holds.
    /// </summary>
    /// <remarks>
    /// As <c>open(2)</c> does: a name holding a file is not the directory the spelling asked
    /// for, a missing name is missing, and a directory is not something a file open can use.
    /// </remarks>
    [Fact]
    public void Opening_a_path_spelled_as_a_directory_is_refused_by_what_the_name_holds()
    {
        MakeFile("contents", "dir", "file");

        using Dir root = OpenRoot();

        CapIOException notADirectory = Assert.ThrowsAny<CapIOException>(() => root.OpenFile("dir/file/").Dispose());
        Assert.Equal(CapErrorKind.NotADirectory, notADirectory.Kind);

        Assert.Throws<FileNotFoundException>(() => root.OpenFile("dir/absent/").Dispose());

        CapIOException isADirectory = Assert.ThrowsAny<CapIOException>(() => root.OpenFile("dir/").Dispose());
        Assert.Equal(CapErrorKind.IsADirectory, isADirectory.Kind);

        CapIOException climbed = Assert.ThrowsAny<CapIOException>(() => root.OpenFile("dir/..").Dispose());
        Assert.Equal(CapErrorKind.IsADirectory, climbed.Kind);
    }

    /// <summary>
    /// An open that may create is refused as naming a directory whatever the name holds, and
    /// a climb out is still reported as an escape.
    /// </summary>
    [Fact]
    public void Creating_at_a_path_spelled_as_a_directory_is_refused_and_creates_nothing()
    {
        MakeFile("contents", "dir", "file");

        using Dir root = OpenRoot();

        foreach (string path in new[] { "dir/file/", "dir/absent/", "dir/", "dir/.." })
        {
            CapIOException thrown = Assert.ThrowsAny<CapIOException>(() => root.CreateFile(path).Dispose());
            Assert.Equal(CapErrorKind.IsADirectory, thrown.Kind);
        }

        Assert.False(File.Exists(Host("dir", "absent")));
        Assert.Throws<SandboxEscapeException>(() => root.CreateFile("dir/../../").Dispose());
        Assert.Throws<SandboxEscapeException>(() => root.OpenFile("..").Dispose());
    }
}
