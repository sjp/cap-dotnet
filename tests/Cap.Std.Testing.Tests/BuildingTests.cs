using Cap.Primitives;

namespace Cap.Std.Testing.Tests;

/// <summary>
/// Building a tree before a test and inspecting it afterwards, without a handle.
/// </summary>
public sealed class BuildingTests
{
    [Fact]
    public void A_built_tree_can_be_inspected_as_it_was_built()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("config/app.json", """{ "x": 1 }""");
        fs.AddDirectory("data");
        fs.AddSymbolicLink("data/latest", "../config");
        fs.AddFile("/data/blob.bin", [1, 2, 3]);

        Assert.True(fs.Exists("config/app.json"));
        Assert.True(fs.Exists("/config"));
        Assert.False(fs.Exists("config/other.json"));
        Assert.Equal("""{ "x": 1 }""", fs.ReadAllText("config/app.json"));
        Assert.Equal([1, 2, 3], fs.ReadAllBytes("data/blob.bin"));
        Assert.Equal(["config", "data"], fs.GetEntries());
        Assert.Equal(["blob.bin", "latest"], fs.GetEntries("data"));
        Assert.Equal("../config", fs.GetSymbolicLinkTarget("data/latest"));
    }

    [Fact]
    public void A_build_path_does_not_follow_links()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("real/file.txt", "x");
        fs.AddSymbolicLink("alias", "real");

        Assert.False(fs.Exists("alias/file.txt"));
        Assert.Throws<IOException>(() => fs.AddFile("alias/new.txt", "y"));
    }

    [Fact]
    public void A_name_that_is_taken_is_not_built_over()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("a.txt", "one");
        fs.AddDirectory("dir");

        Assert.Throws<IOException>(() => fs.AddFile("a.txt", "two"));
        Assert.Throws<IOException>(() => fs.AddDirectory("a.txt"));
        fs.AddDirectory("dir");
        Assert.Equal("one", fs.ReadAllText("a.txt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("a/../b")]
    [InlineData("./a")]
    public void A_build_path_that_names_nothing_or_climbs_is_refused(string path)
    {
        InMemoryFileSystem fs = new();
        Assert.Throws<ArgumentException>(() => fs.AddFile(path, "x"));
    }

    [Fact]
    public void A_name_the_handles_would_refuse_cannot_be_built()
    {
        InMemoryFileSystem windows = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Windows });
        Assert.Throws<ArgumentException>(() => windows.AddFile("CON", "x"));
        Assert.Throws<ArgumentException>(() => windows.AddFile("trailing.", "x"));
        Assert.Throws<ArgumentException>(() => windows.AddFile(@"a\b", "x"));

        InMemoryFileSystem unix = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Unix });
        unix.AddFile(@"a\b", "x");
        Assert.Equal([@"a\b"], unix.GetEntries());
    }

    [Fact]
    public void A_hard_link_is_a_second_name_for_the_same_file()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("one.txt", "shared");
        fs.AddHardLink("two.txt", "one.txt");

        using Dir root = fs.OpenRoot();
        CapMetadata one = root.GetMetadata("one.txt");
        CapMetadata two = root.GetMetadata("two.txt");

        Assert.True(one.IsSameFileAs(two));
        Assert.Equal(2, one.LinkCount);

        fs.AddDirectory("dir");
        Assert.Throws<IOException>(() => fs.AddHardLink("dir-link", "dir"));
    }

    [Fact]
    public void Times_and_permissions_set_while_building_are_reported_through_a_handle()
    {
        DateTimeOffset written = new(2020, 5, 6, 7, 8, 9, TimeSpan.Zero);
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Unix });
        fs.AddFile("a.txt", "x");
        fs.SetTimes("a.txt", lastWrite: written);
        fs.SetUnixMode("a.txt", UnixFileMode.UserRead);

        using Dir root = fs.OpenRoot();
        CapMetadata metadata = root.GetMetadata("a.txt");

        Assert.Equal(written, metadata.LastWriteTime);
        Assert.True(metadata.Permissions.TryGetUnixMode(out UnixFileMode mode));
        Assert.Equal(UnixFileMode.UserRead, mode);
        Assert.False(metadata.Permissions.TryGetWindowsAttributes(out _));
        Assert.Throws<InvalidOperationException>(() => fs.SetAttributes("a.txt", FileAttributes.ReadOnly));
    }

    [Fact]
    public void Options_that_are_not_a_resolution_strategy_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new InMemoryFileSystem(new InMemoryFileSystemOptions { Resolution = ResolutionBackend.InMemory }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new InMemoryFileSystem(new InMemoryFileSystemOptions { PathSyntax = (CapPathSyntax)42 }));
    }
}
