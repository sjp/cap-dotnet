using Cap.Primitives;
using Cap.Std;
using Cap.Std.Testing;

namespace Cap.IO.Abstractions.Tests;

/// <summary>
/// Which entries an enumeration leaves out for the attributes the caller asked to skip, read
/// from the mode where the filesystem records one and from the attributes where it records
/// those.
/// </summary>
public sealed class EnumerationSkipTests
{
    [Fact]
    public void A_file_without_write_permission_is_skipped_as_read_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The adapter reads attributes, not a mode, on Windows.");
        InMemoryFileSystem memory = new();
        memory.AddFile("d/locked");
        memory.AddFile("d/open");
        memory.SetUnixMode("d/locked", UnixFileMode.UserRead);
        using Dir root = memory.OpenRoot();
        DirFileSystem fs = new(root);

        string[] found = [.. fs.Directory.EnumerateFiles("/d", "*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReadOnly })
            .Select(fs.Path.GetFileName)!];

        Assert.Equal(["open"], found);
    }

    [Fact]
    public void Default_options_skip_nothing_by_mode()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The adapter reads attributes, not a mode, on Windows.");
        InMemoryFileSystem memory = new();
        memory.AddFile("d/locked");
        memory.SetUnixMode("d/locked", 0);
        using Dir root = memory.OpenRoot();
        DirFileSystem fs = new(root);

        Assert.Equal(["locked"], fs.Directory.EnumerateFiles("/d", "*", new EnumerationOptions()).Select(fs.Path.GetFileName));
    }

    [Fact]
    public void A_file_recorded_hidden_or_system_is_skipped_by_default_options_where_attributes_are_recorded()
    {
        InMemoryFileSystem memory = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Windows });
        memory.AddFile("d/hidden");
        memory.AddFile("d/system");
        memory.AddFile("d/plain");
        memory.SetAttributes("d/hidden", FileAttributes.Hidden);
        memory.SetAttributes("d/system", FileAttributes.System);
        using Dir root = memory.OpenRoot();
        DirFileSystem fs = new(root);
        string d = fs.Path.Combine(fs.Path.GetPathRoot(fs.Directory.GetCurrentDirectory())!, "d");

        Assert.Equal(["plain"], fs.Directory.EnumerateFiles(d, "*", new EnumerationOptions()).Select(fs.Path.GetFileName));
        Assert.Equal(3, fs.Directory.EnumerateFiles(d).Count());
    }

    [Fact]
    public void A_directory_that_cannot_be_read_is_skipped_only_when_asked()
    {
        InMemoryFileSystem memory = new();
        memory.AddFile("d/locked/hidden.txt");
        memory.AddFile("d/open/shown.txt");
        memory.SetUnreadable("d/locked");
        using Dir root = memory.OpenRoot();
        DirFileSystem fs = new(root);
        EnumerationOptions ignoring = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
        EnumerationOptions failing = new() { RecurseSubdirectories = true, IgnoreInaccessible = false };

        Assert.Equal(["shown.txt"], fs.Directory.EnumerateFiles("/d", "*", ignoring).Select(fs.Path.GetFileName));
        Assert.Throws<UnauthorizedAccessException>(() => fs.Directory.EnumerateFiles("/d", "*", failing).ToList());
    }
}
