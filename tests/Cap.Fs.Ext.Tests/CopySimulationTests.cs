using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Std;
using Cap.Std.Testing;
using Cap.Tests.Fakes;

namespace Cap.Fs.Ext.Tests;

/// <summary>
/// Copying a simulated tree whose entries change kind between being described and being opened.
/// </summary>
/// <remarks>
/// <para>
/// A copy decides what an entry is from a description of its name, then opens it. On a real
/// filesystem a name swapped for a link between the two is a race an attacker has to win; the
/// simulation springs it on the lookup that follows the description, every run, so the open is
/// shown to refuse the link rather than follow it under the source handle's policy.
/// </para>
/// <para>
/// The link swapped in points at a directory elsewhere in the same tree, which the handle's
/// policy would follow. That is the case the open has to refuse by itself: a link out of the
/// tree would be refused by containment whatever the copy asked for.
/// </para>
/// </remarks>
public sealed class CopySimulationTests
{
    private static readonly byte[] Secret = [1, 2, 3, 4];

    [Fact]
    public void A_directory_swapped_for_a_link_after_being_described_is_not_entered()
    {
        (FakeFileSystem fs, FakePlatformOps ops) = TreeSwapping("real", ToLink);
        using Dir source = Dir.OpenThrough(ops, "/tree/source", AmbientAuthority.Acquire());
        using Dir destination = Dir.OpenThrough(ops, "/tree/copy", AmbientAuthority.Acquire());
        Assert.Equal(SymlinkPolicy.FollowWithinSandbox, source.SymlinkPolicy);

        CapIOException thrown = Assert.IsType<CapIOException>(
            Assert.ThrowsAny<IOException>(() => source.CopyTo(destination, cancellationToken: TestContext.Current.CancellationToken)), exactMatch: true);

        Assert.Equal(CapErrorKind.NotSupported, thrown.Kind);
        Assert.Null(fs.Find("/tree/copy/real"));
    }

    [Fact]
    public void A_file_swapped_for_a_link_after_being_described_is_not_read()
    {
        (FakeFileSystem fs, FakePlatformOps ops) = TreeSwapping("big.txt", ToLink);
        using Dir source = Dir.OpenThrough(ops, "/tree/source", AmbientAuthority.Acquire());
        using Dir destination = Dir.OpenThrough(ops, "/tree/copy", AmbientAuthority.Acquire());

        CapIOException thrown = Assert.IsType<CapIOException>(
            Assert.ThrowsAny<IOException>(() => source.CopyTo(destination, cancellationToken: TestContext.Current.CancellationToken)), exactMatch: true);

        Assert.Equal(CapErrorKind.NotSupported, thrown.Kind);
        Assert.Null(fs.Find("/tree/copy/big.txt"));
    }

    [Theory]
    [InlineData("real")]
    [InlineData("big.txt")]
    public void An_entry_swapped_for_a_link_is_skipped_when_links_are_skipped(string swapped)
    {
        (FakeFileSystem fs, FakePlatformOps ops) = TreeSwapping(swapped, ToLink);
        using Dir source = Dir.OpenThrough(ops, "/tree/source", AmbientAuthority.Acquire());
        using Dir destination = Dir.OpenThrough(ops, "/tree/copy", AmbientAuthority.Acquire());

        CopyReport report = source.CopyTo(destination, new CopyOptions { Symlinks = CopyAction.Skip }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Skipped);
        Assert.Equal(0, report.Symlinks);
        Assert.Null(fs.Find($"/tree/copy/{swapped}"));
        Assert.Null(fs.Find("/tree/copy/outside.txt"));
    }

    [Theory]
    [InlineData("real")]
    [InlineData("big.txt")]
    public void An_entry_swapped_for_a_link_is_made_again_as_a_link_when_links_are_recreated(string swapped)
    {
        (FakeFileSystem fs, FakePlatformOps ops) = TreeSwapping(swapped, ToLink);
        using Dir source = Dir.OpenThrough(ops, "/tree/source", AmbientAuthority.Acquire());
        using Dir destination = Dir.OpenThrough(ops, "/tree/copy", AmbientAuthority.Acquire());

        CopyReport report = source.CopyTo(destination, new CopyOptions { Symlinks = CopyAction.Recreate }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Symlinks);
        MemoryNode made = Assert.IsType<MemoryNode>(fs.Find($"/tree/copy/{swapped}"));
        Assert.Equal(CapNodeType.SymbolicLink, made.Type);
        Assert.Equal("../elsewhere", made.LinkTarget);
    }

    /// <summary>
    /// A file swapped for a named pipe is opened — refusing links does not stop that — but is
    /// asked what it is before anything is read, and is dealt with as a pipe.
    /// </summary>
    [Fact]
    public void A_file_swapped_for_a_pipe_after_being_described_is_not_read()
    {
        (FakeFileSystem fs, FakePlatformOps ops) = TreeSwapping("big.txt", ToPipe);
        using Dir source = Dir.OpenThrough(ops, "/tree/source", AmbientAuthority.Acquire());
        using Dir destination = Dir.OpenThrough(ops, "/tree/copy", AmbientAuthority.Acquire());

        CapIOException thrown = Assert.IsType<CapIOException>(
            Assert.ThrowsAny<IOException>(() => source.CopyTo(destination, cancellationToken: TestContext.Current.CancellationToken)), exactMatch: true);

        Assert.Equal(CapErrorKind.NotSupported, thrown.Kind);
        Assert.Null(fs.Find("/tree/copy/big.txt"));
    }

    [Fact]
    public void A_file_swapped_for_a_pipe_is_skipped_when_other_kinds_are_skipped()
    {
        (FakeFileSystem fs, FakePlatformOps ops) = TreeSwapping("big.txt", ToPipe);
        using Dir source = Dir.OpenThrough(ops, "/tree/source", AmbientAuthority.Acquire());
        using Dir destination = Dir.OpenThrough(ops, "/tree/copy", AmbientAuthority.Acquire());

        CopyReport report = source.CopyTo(destination, new CopyOptions { OtherKinds = CopyAction.Skip }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Skipped);
        Assert.Equal(0, report.Bytes);
        Assert.Null(fs.Find("/tree/copy/big.txt"));
        Assert.Equal(1, report.Directories);
    }

    /// <summary>
    /// The same tree with nothing swapped copies whole, which is what makes the tests above
    /// about the swap rather than about a tree the copy could never have copied.
    /// </summary>
    [Fact]
    public void The_same_tree_left_alone_is_copied_whole()
    {
        (FakeFileSystem fs, FakePlatformOps ops) = TreeSwapping("nothing", ToLink);
        using Dir source = Dir.OpenThrough(ops, "/tree/source", AmbientAuthority.Acquire());
        using Dir destination = Dir.OpenThrough(ops, "/tree/copy", AmbientAuthority.Acquire());

        CopyReport report = source.CopyTo(destination, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Directories);
        Assert.Equal(2, report.Files);
        Assert.Equal(CapNodeType.Directory, fs.Find("/tree/copy/real")!.Type);
        byte[] copied = new byte[Secret.Length + 1];
        Assert.Equal(Secret.Length, fs.Find("/tree/copy/big.txt")!.ReadAt(copied, 0));
        Assert.Equal(Secret, copied[..Secret.Length]);
    }

    /// <summary>
    /// A source holding a directory with a file in it and a file beside it, an empty
    /// destination, and a directory elsewhere in the same tree for a link to be aimed at. The
    /// entry named is replaced on the second time its name is looked up in the source: the
    /// first is the copy describing it, the second is the copy opening it.
    /// </summary>
    private static (FakeFileSystem FileSystem, FakePlatformOps Ops) TreeSwapping(
        string name, Func<FakeFileSystem, MemoryNode> replacement)
    {
        FakeFileSystem fs = new();
        _ = fs.AddFile("/tree/source/real/inside.txt");
        fs.AddFile("/tree/source/big.txt").WriteAt(Secret, 0);
        fs.AddFile("/tree/elsewhere/outside.txt").WriteAt(Secret, 0);
        _ = fs.AddDirectory("/tree/copy");

        MemoryNode sourceDirectory = fs.Find("/tree/source")!;
        int lookups = 0;
        fs.BeforeLookup = (directory, sought) =>
        {
            if (directory == sourceDirectory && sought == name && ++lookups == 2)
            {
                fs.Replace($"/tree/source/{name}", replacement(fs));
            }
        };

        return (fs, new FakePlatformOps(fs));
    }

    private static MemoryNode ToLink(FakeFileSystem fs) => new()
    {
        Type = CapNodeType.SymbolicLink,
        LinkTarget = "../elsewhere",
        VolumeId = 1,
        NodeId = fs.NextNodeId(),
    };

    private static MemoryNode ToPipe(FakeFileSystem fs) => new()
    {
        Type = CapNodeType.Other,
        EntryType = CapFileType.Fifo,
        VolumeId = 1,
        NodeId = fs.NextNodeId(),
    };
}
