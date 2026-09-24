using Cap.Primitives;

namespace Cap.Std.Tests;

/// <summary>
/// The reason a failure carries, which a caller switches on instead of reading the message.
/// </summary>
/// <remarks>
/// <para>
/// Several failures that mean different things to a caller arrive as the same exception type:
/// a name already taken, a directory that is not empty, a component that is not a directory.
/// What tells them apart is <see cref="CapIOException.Kind"/>, so each is provoked here and
/// the reason checked, on every platform, since the reason is promised to be the same
/// whichever system reported the failure.
/// </para>
/// <para>
/// A name spelled with a trailing separator is a request for a directory, and an operation
/// that cannot make or remove one there is refused by what the name holds, as POSIX refuses
/// it: missing is <see cref="CapErrorKind.NotFound"/>, a directory already there is the
/// operation's own refusal, and anything else is <see cref="CapErrorKind.NotADirectory"/>.
/// Those are decided by the library rather than by the platform, so they are covered here too.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class FailureKindTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private Dir OpenRoot() => Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

    private string Host(params string[] parts) => Path.Combine([_tree.HostPath, .. parts]);

    private static CapErrorKind KindOf(Action action) =>
        CapIOException.KindOf(Assert.ThrowsAny<Exception>(action));

    // --- the reasons themselves --------------------------------------------------------------

    [Fact]
    public void A_name_already_taken_is_reported_as_already_existing()
    {
        File.WriteAllText(Host("file"), "x");
        using Dir root = OpenRoot();

        CapIOException thrown = Assert.ThrowsAny<CapIOException>(() => root.CreateNewFile("file").Dispose());

        Assert.Equal(CapErrorKind.AlreadyExists, thrown.Kind);
    }

    [Fact]
    public void A_directory_with_entries_is_reported_as_not_empty()
    {
        Directory.CreateDirectory(Host("full"));
        File.WriteAllText(Host("full", "inside"), "x");
        using Dir root = OpenRoot();

        Assert.Equal(CapErrorKind.NotEmpty, KindOf(() => root.DeleteDir("full")));
    }

    [Fact]
    public void A_path_that_continues_past_a_file_is_reported_as_not_a_directory()
    {
        File.WriteAllText(Host("file"), "x");
        using Dir root = OpenRoot();

        Assert.Equal(CapErrorKind.NotADirectory, KindOf(() => root.OpenDir("file").Dispose()));
    }

    [Fact]
    public void A_file_removal_that_names_a_directory_is_reported_as_one()
    {
        Directory.CreateDirectory(Host("dir"));
        using Dir root = OpenRoot();

        // macOS refuses unlink on a directory as a permission failure, which is what it reports.
        CapErrorKind kind = KindOf(() => root.DeleteFile("dir"));

        Assert.Contains(kind, new[] { CapErrorKind.IsADirectory, CapErrorKind.PermissionDenied });
    }

    [Fact]
    public void A_missing_name_is_reported_as_not_found_through_the_framework_type()
    {
        using Dir root = OpenRoot();

        Exception thrown = Assert.ThrowsAny<Exception>(() => root.OpenFile("absent").Dispose());

        Assert.IsType<FileNotFoundException>(thrown);
        Assert.Equal(CapErrorKind.NotFound, CapIOException.KindOf(thrown));
    }

    [Fact]
    public void A_path_that_leaves_is_reported_as_an_escape()
    {
        using Dir root = OpenRoot();

        SandboxEscapeException thrown = Assert.Throws<SandboxEscapeException>(() => root.OpenFile("../x").Dispose());

        Assert.Equal(CapErrorKind.Escaped, thrown.Kind);
    }

    [Fact]
    public void A_link_the_policy_will_not_follow_is_reported_as_not_followed()
    {
        Directory.CreateDirectory(Host("plain"));
        RequireSymlinks(() => Directory.CreateSymbolicLink(Host("link"), "plain"));
        using Dir root = OpenRoot();
        using Dir strict = root.Restrict(SymlinkPolicy.Deny);

        Assert.Equal(CapErrorKind.LinkNotFollowed, KindOf(() => strict.OpenDir("link").Dispose()));
    }

    // --- a name spelled as a directory -------------------------------------------------------

    [Fact]
    public void A_link_made_at_a_missing_name_spelled_as_a_directory_is_reported_as_not_found()
    {
        using Dir root = OpenRoot();

        Assert.Equal(CapErrorKind.NotFound, KindOf(() => root.CreateSymlink("target/", "source")));
        Assert.False(Path.Exists(Host("target")));
    }

    [Fact]
    public void A_link_made_at_a_directory_spelled_as_one_is_reported_as_already_existing()
    {
        Directory.CreateDirectory(Host("target"));
        using Dir root = OpenRoot();

        Assert.Equal(CapErrorKind.AlreadyExists, KindOf(() => root.CreateSymlink("target/", "source")));
        Assert.True(Directory.Exists(Host("target")));
    }

    [Fact]
    public void A_link_made_at_a_file_spelled_as_a_directory_is_reported_as_not_a_directory()
    {
        File.WriteAllText(Host("target"), "x");
        using Dir root = OpenRoot();

        Assert.Equal(CapErrorKind.NotADirectory, KindOf(() => root.CreateSymlink("target/", "source")));
        Assert.Equal("x", File.ReadAllText(Host("target")));
    }

    [Fact]
    public void A_file_removal_spelled_as_a_directory_is_refused_by_what_the_name_holds()
    {
        File.WriteAllText(Host("file"), "x");
        Directory.CreateDirectory(Host("dir"));
        using Dir root = OpenRoot();

        Assert.Equal(CapErrorKind.NotADirectory, KindOf(() => root.DeleteFile("file/")));
        Assert.Equal(CapErrorKind.IsADirectory, KindOf(() => root.DeleteFile("dir/")));
        Assert.Equal(CapErrorKind.NotFound, KindOf(() => root.DeleteFile("absent/")));
        Assert.True(File.Exists(Host("file")));
        Assert.True(Directory.Exists(Host("dir")));
    }

    [Fact]
    public void A_second_name_for_a_file_spelled_as_a_directory_is_refused_by_what_the_name_holds()
    {
        File.WriteAllText(Host("file"), "x");
        Directory.CreateDirectory(Host("dir"));
        using Dir root = OpenRoot();

        Assert.Equal(CapErrorKind.NotFound, KindOf(() => root.CreateHardLink("file", root, "link/")));
        Assert.Equal(CapErrorKind.AlreadyExists, KindOf(() => root.CreateHardLink("file", root, "dir/")));
        Assert.False(Path.Exists(Host("link")));
    }

    // --- the helper ---------------------------------------------------------------------------

    [Fact]
    public void The_framework_types_are_read_as_the_reason_each_stands_for()
    {
        Assert.Equal(CapErrorKind.NotFound, CapIOException.KindOf(new FileNotFoundException()));
        Assert.Equal(CapErrorKind.NotFound, CapIOException.KindOf(new DirectoryNotFoundException()));
        Assert.Equal(CapErrorKind.PermissionDenied, CapIOException.KindOf(new UnauthorizedAccessException()));
        Assert.Equal(CapErrorKind.NameTooLong, CapIOException.KindOf(new PathTooLongException()));
        Assert.Equal(CapErrorKind.Other, CapIOException.KindOf(new IOException()));
        Assert.Equal(CapErrorKind.Other, CapIOException.KindOf(new CapIOException("no reason given")));
        Assert.Equal(CapErrorKind.Escaped, CapIOException.KindOf(new SandboxEscapeException()));
    }

    private static void RequireSymlinks(Action create)
    {
        try
        {
            create();
        }
        catch (Exception thrown) when (thrown is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Symbolic links cannot be created here: {thrown.Message}");
        }
    }
}
