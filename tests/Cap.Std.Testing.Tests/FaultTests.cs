using Cap.Primitives;

namespace Cap.Std.Testing.Tests;

/// <summary>
/// The failures a real filesystem produces only in awkward circumstances, produced on demand.
/// </summary>
public sealed class FaultTests
{
    [Fact]
    public void An_unreadable_file_is_refused_but_can_still_be_described()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("secret.txt", "x");
        fs.SetUnreadable("secret.txt");

        using Dir root = fs.OpenRoot();

        Assert.Throws<UnauthorizedAccessException>(() => root.ReadAllBytes("secret.txt"));
        Assert.Equal(1, root.GetMetadata("secret.txt").Length);

        fs.SetUnreadable("secret.txt", unreadable: false);
        Assert.Equal("x", root.ReadAllText("secret.txt"));
    }

    [Theory]
    [MemberData(nameof(Resolutions.Both), MemberType = typeof(Resolutions))]
    public void Nothing_inside_an_unreadable_directory_can_be_reached(ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = Resolutions.Create(resolution);
        fs.AddFile("locked/inner.txt", "x");
        fs.SetUnreadable("locked");

        using Dir root = fs.OpenRoot();

        Assert.Throws<UnauthorizedAccessException>(() => root.ReadAllBytes("locked/inner.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => root.OpenDir("locked"));
    }

    [Fact]
    public void An_undeletable_name_is_neither_removed_nor_replaced()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("pinned.txt", "kept");
        fs.AddFile("other.txt", "other");
        fs.SetUndeletable("pinned.txt");

        using Dir root = fs.OpenRoot();

        Assert.Throws<UnauthorizedAccessException>(() => root.DeleteFile("pinned.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => root.Rename("other.txt", root, "pinned.txt", replaceExisting: true));
        Assert.Throws<UnauthorizedAccessException>(() => root.Rename("pinned.txt", root, "moved.txt"));
        Assert.Equal("kept", fs.ReadAllText("pinned.txt"));
        Assert.Equal("other", fs.ReadAllText("other.txt"));
    }

    [Fact]
    public void A_refused_move_of_an_undeletable_name_leaves_what_it_would_have_replaced()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("pinned.txt", "pinned");
        fs.AddFile("target.txt", "target");
        fs.SetUndeletable("pinned.txt");

        using Dir root = fs.OpenRoot();

        Assert.Throws<UnauthorizedAccessException>(() => root.Rename("pinned.txt", root, "target.txt", replaceExisting: true));
        Assert.Equal("target", fs.ReadAllText("target.txt"));
        Assert.Equal("pinned", fs.ReadAllText("pinned.txt"));
    }

    [Fact]
    public void The_name_of_an_unreadable_file_can_still_be_moved_and_removed()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile("secret.txt", "x");
        fs.SetUnreadable("secret.txt");

        using Dir root = fs.OpenRoot();
        root.Rename("secret.txt", root, "moved.txt");
        root.DeleteFile("moved.txt");

        Assert.Empty(fs.GetEntries());
    }

    [Fact]
    public void The_next_writes_fail_as_asked_and_then_writes_succeed_again()
    {
        InMemoryFileSystem fs = new();
        using Dir root = fs.OpenRoot();
        using CapFile file = root.OpenFile("log.txt", FileMode.CreateNew, FileAccess.ReadWrite);

        fs.FailNextWrites(2);

        IOException first = Assert.Throws<IOException>(() => file.Write([1], 0));
        IOException second = Assert.Throws<IOException>(() => file.SetLength(10));
        file.Write([2], 0);

        Assert.Equal(CapErrorKind.Other, CapIOException.KindOf(first));
        Assert.Equal(CapErrorKind.Other, CapIOException.KindOf(second));
        Assert.Equal([2], fs.ReadAllBytes("log.txt"));
    }

    [Theory]
    [InlineData(CapErrorKind.PermissionDenied, typeof(UnauthorizedAccessException))]
    [InlineData(CapErrorKind.NotFound, typeof(FileNotFoundException))]
    [InlineData(CapErrorKind.ReadOnlyFilesystem, typeof(CapIOException))]
    public void A_write_fails_with_the_kind_asked_for(CapErrorKind kind, Type expected)
    {
        InMemoryFileSystem fs = new();
        using Dir root = fs.OpenRoot();
        using CapFile file = root.OpenFile("data.bin", FileMode.CreateNew, FileAccess.Write);

        fs.FailNextWrites(1, kind);
        Exception thrown = Assert.ThrowsAny<Exception>(() => file.Write([1], 0));

        Assert.IsType(expected, thrown);
        Assert.Equal(kind, CapIOException.KindOf(thrown));
        Assert.Equal(0, file.Length);
    }

    [Fact]
    public void An_appending_write_fails_too()
    {
        InMemoryFileSystem fs = new();
        using Dir root = fs.OpenRoot();
        using CapFile file = root.OpenFile("append.log", FileMode.Append, FileAccess.Write);

        fs.FailNextWrites(1, CapErrorKind.PermissionDenied);

        Assert.Throws<UnauthorizedAccessException>(() => file.Write([1], 0));
        file.Write([2], 0);
        Assert.Equal([2], fs.ReadAllBytes("append.log"));
    }

    [Fact]
    public void A_write_past_the_capacity_fails_as_a_full_disk_does()
    {
        InMemoryFileSystem fs = new() { Capacity = 8 };
        fs.AddFile("existing.bin", new byte[5]);

        using Dir root = fs.OpenRoot();
        root.WriteAllBytes("fits.bin", new byte[3]);

        IOException full = Assert.Throws<IOException>(() => root.WriteAllBytes("too-much.bin", new byte[1]));
        Assert.Equal(CapErrorKind.Other, CapIOException.KindOf(full));
        Assert.Equal(8, fs.UsedBytes);

        root.DeleteFile("existing.bin");
        root.WriteAllBytes("now-it-fits.bin", new byte[5]);
        Assert.Equal(8, fs.UsedBytes);

        fs.Capacity = null;
        root.WriteAllBytes("unlimited.bin", new byte[100]);
        Assert.Equal(108, fs.UsedBytes);
    }

    [Fact]
    public void A_capacity_cannot_be_negative()
    {
        InMemoryFileSystem fs = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => fs.Capacity = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => fs.FailNextWrites(-1));
    }
}
