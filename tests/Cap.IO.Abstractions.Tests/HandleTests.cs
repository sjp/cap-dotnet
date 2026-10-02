using Cap.Std;
using Cap.Std.Testing;

namespace Cap.IO.Abstractions.Tests;

/// <summary>
/// That a caller who stops reading early leaves nothing open behind it, counted by the
/// in-memory filesystem, which sees every handle.
/// </summary>
public sealed class HandleTests
{
    [Fact]
    public void Lines_left_unread_close_the_file()
    {
        InMemoryFileSystem memory = new();
        memory.AddFile("a.txt", "x\ny\nz\n");
        Dir root = memory.OpenRoot();
        DirFileSystem fs = new(root);

        Assert.Equal("x", fs.File.ReadLines("/a.txt").First());

        root.Dispose();
        Assert.Equal(0, memory.OpenHandleCount);
    }

    [Fact]
    public void Entries_left_unread_close_the_directories()
    {
        InMemoryFileSystem memory = new();
        memory.AddFile("d/a.txt");
        memory.AddFile("d/sub/b.txt");
        Dir root = memory.OpenRoot();
        DirFileSystem fs = new(root);

        Assert.NotNull(fs.Directory.EnumerateFiles("/d", "*", SearchOption.AllDirectories).First());

        root.Dispose();
        Assert.Equal(0, memory.OpenHandleCount);
    }
}
