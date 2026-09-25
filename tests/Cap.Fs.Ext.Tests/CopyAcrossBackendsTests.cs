using Cap.Primitives;
using Cap.Std;
using Cap.Tests.Fakes;

namespace Cap.Fs.Ext.Tests;

/// <summary>
/// Copying between handles on different filesystems.
/// </summary>
/// <remarks>
/// <para>
/// A copy reads through one handle and writes through the other, and never gives either
/// backend the other's handle, so unlike a move it works across filesystems. What does not
/// carry over is identity. Each backend numbers its objects on its own, so the check that
/// the destination is not inside the source has no meaning between two of them, and must
/// not refuse a copy because two numbers happen to match.
/// </para>
/// <para>
/// Only directories are copied here. File contents in the simulation are not yet read or
/// written through its backend, so a file copied into or out of it would not be a test of
/// anything in this class.
/// </para>
/// </remarks>
public sealed class CopyAcrossBackendsTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>A tree of directories on disk is reproduced in a simulated one.</summary>
    [Fact]
    public void A_tree_on_disk_is_copied_into_a_simulated_one()
    {
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "source", "a", "b"));
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "source", "c"));

        FakeFileSystem fs = new();
        _ = fs.AddDirectory("/destination");
        FakePlatformOps ops = new(fs);

        using Dir source = _tree.Directory.OpenDir("source");
        using Dir destination = Dir.OpenThrough(ops, "/destination", AmbientAuthority.Acquire());

        CopyReport report = source.CopyTo(destination);

        Assert.Equal(3, report.Directories);
        Assert.NotNull(fs.Find("/destination/a/b"));
        Assert.NotNull(fs.Find("/destination/c"));
    }

    /// <summary>A simulated tree of directories is reproduced on disk.</summary>
    [Fact]
    public void A_simulated_tree_is_copied_onto_disk()
    {
        FakeFileSystem fs = new();
        _ = fs.AddDirectory("/source/a/b");
        FakePlatformOps ops = new(fs);
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "destination"));

        using Dir source = Dir.OpenThrough(ops, "/source", AmbientAuthority.Acquire());
        using Dir destination = _tree.Directory.OpenDir("destination");

        CopyReport report = source.CopyTo(destination);

        Assert.Equal(2, report.Directories);
        Assert.True(HostDirectory.Exists(Path.Combine(_tree.HostPath, "destination", "a", "b")));
    }

    /// <summary>
    /// A directory in the source whose identity matches the destination's, on another
    /// filesystem, is copied rather than taken for the destination.
    /// </summary>
    /// <remarks>
    /// Two simulations built the same way number their objects the same way, so the source's
    /// <c>inner</c> and the destination, which is the other simulation's <c>inner</c>, carry
    /// the same identity. Within one filesystem that would mean the destination is inside the
    /// source. Across two it means nothing.
    /// </remarks>
    [Fact]
    public void Matching_identities_on_different_filesystems_do_not_refuse_the_copy()
    {
        FakeFileSystem sourceFs = new();
        _ = sourceFs.AddDirectory("/root/inner");
        FakeFileSystem destinationFs = new();
        _ = destinationFs.AddDirectory("/root/inner");

        FakePlatformOps sourceOps = new(sourceFs);
        FakePlatformOps destinationOps = new(destinationFs);

        using Dir source = Dir.OpenThrough(sourceOps, "/root", AmbientAuthority.Acquire());
        using Dir destination = Dir.OpenThrough(destinationOps, "/root/inner", AmbientAuthority.Acquire());
        using (Dir sourceInner = source.OpenDir("inner"))
        {
            Assert.Equal(sourceInner.GetMetadata().FileId, destination.GetMetadata().FileId);
        }

        CopyReport report = source.CopyTo(destination);

        Assert.Equal(1, report.Directories);
        Assert.NotNull(destinationFs.Find("/root/inner/inner"));
    }
}
