namespace Cap.IO.Abstractions.Tests;

/// <summary>
/// The virtual namespace's syntax: what reaches the <c>Dir</c> for a caller's path, and the
/// folded names shown back. The Windows rules are exercised on every platform.
/// </summary>
public sealed class VirtualPathTests
{
    private static readonly VirtualPath Posix = new(drive: null, windows: false);
    private static readonly VirtualPath WindowsSlash = new(drive: null, windows: true);
    private static readonly VirtualPath WindowsDrive = new(drive: 'C', windows: true);

    [Theory]
    [InlineData("/a/b", "a/b")]
    [InlineData("/a/../b", "a/../b")]
    [InlineData("/../outside", "../outside")]
    [InlineData("/./a//b/", "./a//b/")]
    [InlineData("//etc/passwd", "/etc/passwd")]
    public void Only_the_root_is_removed(string path, string relative)
    {
        Request request = Posix.Resolve(path, "/cwd");

        Assert.Equal(relative, request.Relative);
        Assert.Equal(path, request.Virtual);
        Assert.False(request.IsRoot);
    }

    [Theory]
    [InlineData("a", "/cwd/a", "cwd/a")]
    [InlineData("../a", "/cwd/../a", "cwd/../a")]
    [InlineData("../../a", "/cwd/../../a", "cwd/../../a")]
    public void A_relative_path_is_joined_to_the_current_directory_and_not_folded(string path, string full, string relative)
    {
        Request request = Posix.Resolve(path, "/cwd");

        Assert.Equal(full, request.Virtual);
        Assert.Equal(relative, request.Relative);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/.")]
    [InlineData("/./")]
    [InlineData("/.//.")]
    public void Spellings_of_the_root_itself_are_recognised(string path)
    {
        Assert.True(Posix.Resolve(path, "/").IsRoot);
    }

    [Theory]
    [InlineData("/a/..")]
    [InlineData("/..")]
    [InlineData("/a")]
    public void A_path_that_needs_a_walk_is_not_taken_for_the_root(string path)
    {
        Assert.False(Posix.Resolve(path, "/").IsRoot);
    }

    [Theory]
    [InlineData(@"C:\Windows", @"C:\Windows")]
    [InlineData(@"C:file", @"C:file")]
    [InlineData(@"D:\data", @"D:\data")]
    public void A_drive_path_reaches_the_dir_unchanged_to_be_refused(string path, string relative)
    {
        Assert.Equal(relative, WindowsSlash.Resolve(path, "/").Relative);
    }

    [Theory]
    [InlineData(@"\a\b", @"a\b")]
    [InlineData("/a/b", "a/b")]
    [InlineData(@"\\server\share", @"\server\share")]
    public void Either_separator_is_the_root_under_windows_rules(string path, string relative)
    {
        Assert.Equal(relative, WindowsSlash.Resolve(path, "/").Relative);
    }

    [Theory]
    [InlineData(@"C:\a", "a")]
    [InlineData(@"c:/a", "a")]
    [InlineData(@"\a", "a")]
    [InlineData(@"D:\a", @"D:\a")]
    [InlineData(@"\\server\share", @"\\server\share")]
    [InlineData(@"C:a", @"C:a")]
    public void A_virtual_drive_is_the_only_drive(string path, string relative)
    {
        Assert.Equal(relative, WindowsDrive.Resolve(path, @"C:\").Relative);
    }

    [Theory]
    [InlineData("/a/../b/./c", "/b/c")]
    [InlineData("/../../a", "/a")]
    [InlineData("/a/b/", "/a/b/")]
    [InlineData("/", "/")]
    [InlineData("x/../y", "/cwd/y")]
    public void Full_names_are_folded_for_display(string path, string full)
    {
        Assert.Equal(full, Posix.GetFullPath(path, "/cwd"));
    }

    [Fact]
    public void A_drive_root_folds_with_its_own_separator()
    {
        Assert.Equal(@"C:\a\c", WindowsDrive.GetFullPath("/a/b/../c", @"C:\"));
    }

    [Theory]
    [InlineData("a/b/c", new[] { "a", "a/b", "a/b/c" })]
    [InlineData("a/../b", new[] { "a", "a/../b" })]
    [InlineData("./a//b/", new[] { "./a", "./a//b" })]
    [InlineData("..", new string[0])]
    public void Directories_are_created_from_slices_of_the_callers_path(string relative, string[] prefixes)
    {
        Assert.Equal(prefixes, Posix.CreatablePrefixes(relative));
    }

    [Theory]
    [InlineData("/a/b", "/a")]
    [InlineData("/a", "/")]
    [InlineData("/a/b/", "/a")]
    [InlineData("/a/..", "/a/../..")]
    [InlineData("/", null)]
    public void A_parent_is_a_slice_or_a_further_climb(string request, string? parent)
    {
        Assert.Equal(parent, Posix.ParentRequest(request));
    }

    [Theory]
    [InlineData("/a/b", "/a/c/d", "../c/d")]
    [InlineData("/a", "/a", ".")]
    [InlineData("/a/b", "/", "../..")]
    public void A_relative_path_is_worked_out_lexically(string from, string to, string relative)
    {
        Assert.Equal(relative, Posix.GetRelativePath(from, to, "/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/a/b", "b")]
    [InlineData("/a/b/", "b")]
    [InlineData("/", "/")]
    public void The_last_name_ignores_a_trailing_separator(string path, string name)
    {
        Assert.Equal(name, Posix.LastName(path));
    }

    [Fact]
    public void Combining_writes_the_namespace_separator_whatever_the_host()
    {
        Assert.Equal("/a/b", WindowsSlash.Combine(["/", "a", "b"]));
        Assert.Equal("/a/b", Posix.Combine(["/", "a", "b"]));
        Assert.Equal(@"C:\a\b", WindowsDrive.Combine([@"C:\", "a", "b"]));
    }

    [Fact]
    public void Combining_restarts_at_a_rooted_part_and_skips_an_empty_one()
    {
        Assert.Equal("/b/c", WindowsSlash.Combine(["/a", "/b", "", "c"]));
        Assert.Equal(@"D:\x", WindowsSlash.Combine(["/a", @"D:\x"]));
        Assert.Throws<ArgumentNullException>(() => WindowsSlash.Combine(["/a", null!]));
    }

    [Fact]
    public void Joining_keeps_a_rooted_part_and_skips_null_and_empty_ones()
    {
        Assert.Equal("/a/b/c", WindowsSlash.JoinAll(["/a", null, "", "/b", "c"]));
        Assert.Equal(@"a\b", WindowsSlash.JoinAll([@"a\", "b"]));
        Assert.Equal(string.Empty, WindowsSlash.JoinAll([null, ""]));
    }
}
