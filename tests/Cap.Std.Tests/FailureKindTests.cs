using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Windows;
using Cap.Tests;

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
        HostFile.WriteAllText(Host("file"), "x");
        using Dir root = OpenRoot();

        CapIOException thrown = Assert.ThrowsAny<CapIOException>(() => root.CreateNewFile("file").Dispose());

        Assert.Equal(CapErrorKind.AlreadyExists, thrown.Kind);
    }

    [Fact]
    public void A_directory_with_entries_is_reported_as_not_empty()
    {
        HostDirectory.CreateDirectory(Host("full"));
        HostFile.WriteAllText(Host("full", "inside"), "x");
        using Dir root = OpenRoot();

        Assert.Equal(CapErrorKind.NotEmpty, KindOf(() => root.DeleteDir("full")));
    }

    [Fact]
    public void A_path_that_continues_past_a_file_is_reported_as_not_a_directory()
    {
        HostFile.WriteAllText(Host("file"), "x");
        using Dir root = OpenRoot();

        Assert.Equal(CapErrorKind.NotADirectory, KindOf(() => root.OpenDir("file").Dispose()));
    }

    /// <summary>
    /// The answer is the same everywhere, although the platforms do not word it the same way.
    /// </summary>
    /// <remarks>
    /// One of them refuses the removal of a directory under this call as a permission failure,
    /// which would send a caller looking for a permissions problem over a directory it can read
    /// perfectly well.
    /// </remarks>
    [Fact]
    public void A_file_removal_that_names_a_directory_is_reported_as_one()
    {
        HostDirectory.CreateDirectory(Host("dir"));
        using Dir root = OpenRoot();

        Assert.Equal(CapErrorKind.IsADirectory, KindOf(() => root.DeleteFile("dir")));
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
        HostDirectory.CreateDirectory(Host("plain"));
        RequireSymlinks(() => HostDirectory.CreateSymbolicLink(Host("link"), "plain"));
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
        Assert.False(HostEntry.Exists(Host("target")));
    }

    [Fact]
    public void A_link_made_at_a_directory_spelled_as_one_is_reported_as_already_existing()
    {
        HostDirectory.CreateDirectory(Host("target"));
        using Dir root = OpenRoot();

        Assert.Equal(CapErrorKind.AlreadyExists, KindOf(() => root.CreateSymlink("target/", "source")));
        Assert.True(HostDirectory.Exists(Host("target")));
    }

    [Fact]
    public void A_link_made_at_a_file_spelled_as_a_directory_is_reported_as_not_a_directory()
    {
        HostFile.WriteAllText(Host("target"), "x");
        using Dir root = OpenRoot();

        Assert.Equal(CapErrorKind.NotADirectory, KindOf(() => root.CreateSymlink("target/", "source")));
        Assert.Equal("x", HostFile.ReadAllText(Host("target")));
    }

    [Fact]
    public void A_file_removal_spelled_as_a_directory_is_refused_by_what_the_name_holds()
    {
        HostFile.WriteAllText(Host("file"), "x");
        HostDirectory.CreateDirectory(Host("dir"));
        using Dir root = OpenRoot();

        Assert.Equal(CapErrorKind.NotADirectory, KindOf(() => root.DeleteFile("file/")));
        Assert.Equal(CapErrorKind.IsADirectory, KindOf(() => root.DeleteFile("dir/")));
        Assert.Equal(CapErrorKind.NotFound, KindOf(() => root.DeleteFile("absent/")));
        Assert.True(HostFile.Exists(Host("file")));
        Assert.True(HostDirectory.Exists(Host("dir")));
    }

    [Fact]
    public void A_second_name_for_a_file_spelled_as_a_directory_is_refused_by_what_the_name_holds()
    {
        HostFile.WriteAllText(Host("file"), "x");
        HostDirectory.CreateDirectory(Host("dir"));
        using Dir root = OpenRoot();

        Assert.Equal(CapErrorKind.NotFound, KindOf(() => root.CreateHardLink("file", root, "link/")));
        Assert.Equal(CapErrorKind.AlreadyExists, KindOf(() => root.CreateHardLink("file", root, "dir/")));
        Assert.False(HostEntry.Exists(Host("link")));
    }

    /// <summary>
    /// A symbolic link Windows refuses for want of the privilege reaches the caller as the
    /// framework's permission failure, as <c>File.CreateSymbolicLink</c> reports it.
    /// </summary>
    /// <remarks>
    /// Asserted from the code the link write reports rather than by making the link: the build
    /// agents hold the privilege. The Windows on-disk suite removes it to check the code.
    /// </remarks>
    [Fact]
    public void A_link_refused_for_want_of_the_privilege_is_a_permission_failure()
    {
        CapError refused = Win32Errors.ToError(Win32Errors.ERROR_PRIVILEGE_NOT_HELD);

        Exception thrown = FailureTranslation.ToException(refused, "link", ExpectedTarget.Name);

        Assert.IsType<UnauthorizedAccessException>(thrown);
        Assert.Equal(CapErrorKind.PermissionDenied, CapIOException.KindOf(thrown));
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

    /// <summary>
    /// Every constructor of the two exception types sets the reason it implies and keeps the
    /// cause it was given.
    /// </summary>
    /// <remarks>
    /// Code that wraps a failure of its own in one of these, or rethrows one with a cause
    /// attached, reads the reason back through the same property the library's own failures
    /// use, so a constructor that dropped either would hide the failure behind
    /// <see cref="CapErrorKind.Other"/> or lose the trail that led to it.
    /// </remarks>
    [Fact]
    public void Every_constructor_of_the_exception_types_sets_the_kind()
    {
        IOException cause = new("underneath");

        CapIOException plain = new();
        Assert.Equal(CapErrorKind.Other, plain.Kind);
        Assert.False(string.IsNullOrEmpty(plain.Message));
        Assert.Null(plain.InnerException);

        CapIOException wrapping = new("wrapped", cause);
        Assert.Equal(CapErrorKind.Other, wrapping.Kind);
        Assert.Equal("wrapped", wrapping.Message);
        Assert.Same(cause, wrapping.InnerException);

        CapIOException kinded = new(CapErrorKind.NotEmpty, "kinded");
        Assert.Equal(CapErrorKind.NotEmpty, kinded.Kind);
        Assert.Null(kinded.InnerException);

        CapIOException kindedWrapping = new(CapErrorKind.NotEmpty, "kinded", cause);
        Assert.Equal(CapErrorKind.NotEmpty, kindedWrapping.Kind);
        Assert.Same(cause, kindedWrapping.InnerException);

        SandboxEscapeException escape = new();
        Assert.Equal(CapErrorKind.Escaped, escape.Kind);
        Assert.False(string.IsNullOrEmpty(escape.Message));
        Assert.Null(escape.InnerException);

        SandboxEscapeException escapeWithMessage = new("left");
        Assert.Equal(CapErrorKind.Escaped, escapeWithMessage.Kind);
        Assert.Equal("left", escapeWithMessage.Message);

        SandboxEscapeException escapeWrapping = new("left", cause);
        Assert.Equal(CapErrorKind.Escaped, escapeWrapping.Kind);
        Assert.Equal("left", escapeWrapping.Message);
        Assert.Same(cause, escapeWrapping.InnerException);
    }

    private static void RequireSymlinks(Action create) => HostLinks.Require(HostFeature.Symlinks, create);
}
