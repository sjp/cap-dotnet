using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Tests.Fakes;

namespace Cap.Std.Tests;

/// <summary>
/// Handles on two filesystems in one process: a simulated tree and the disk.
/// </summary>
/// <remarks>
/// <para>
/// A handle resolves through the backend that issued it, so a test can build a tree in a
/// simulation while everything else in the process goes on using the disk. These tests hold
/// both at once and check that each stays on its own side. An operation that would have to
/// hand one backend a handle from the other is refused as a move across devices, before
/// either backend is asked to do anything.
/// </para>
/// <para>
/// In the handle group because half of each test is on the disk, which the host's
/// process-wide counters see. The simulated half needs no such care.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class DirBackendTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private Dir OpenDisk() => Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

    private static (FakeFileSystem Fs, FakePlatformOps Ops, Dir Root) OpenSimulated()
    {
        FakeFileSystem fs = new();
        _ = fs.AddDirectory("/sandbox");
        FakePlatformOps ops = new(fs);
        return (fs, ops, Dir.OpenThrough(ops, "/sandbox", AmbientAuthority.Acquire()));
    }

    /// <summary>
    /// A tree in the simulation and a tree on disk are worked on together, and neither sees
    /// the other.
    /// </summary>
    [Fact]
    public void A_simulated_tree_and_a_tree_on_disk_are_used_at_once()
    {
        (FakeFileSystem fs, _, Dir simulated) = OpenSimulated();
        using Dir disk = OpenDisk();
        using (simulated)
        {
            using Dir simulatedChild = simulated.CreateDir("in-memory");
            using Dir diskChild = disk.CreateDir("on-disk");
            simulatedChild.CreateDir("nested").Dispose();
            diskChild.CreateDir("nested").Dispose();

            Assert.NotNull(fs.Find("/sandbox/in-memory/nested"));
            Assert.True(HostDirectory.Exists(Path.Combine(_tree.HostPath, "on-disk", "nested")));

            Assert.Equal(["in-memory"], simulated.EnumerateEntries().Select(entry => entry.Name));
            Assert.Equal(["on-disk"], disk.EnumerateEntries().Select(entry => entry.Name));
            Assert.False(simulated.Exists("on-disk"));
            Assert.False(disk.Exists("in-memory"));
            Assert.Null(fs.Find("/sandbox/on-disk"));
            Assert.False(HostDirectory.Exists(Path.Combine(_tree.HostPath, "in-memory")));
        }
    }

    /// <summary>
    /// A handle reports the backend it resolves through, and every handle derived from it
    /// reports the same.
    /// </summary>
    [Fact]
    public void A_handle_reports_its_own_backend()
    {
        (_, FakePlatformOps ops, Dir simulated) = OpenSimulated();
        using Dir disk = OpenDisk();
        using (simulated)
        {
            using Dir child = simulated.CreateDir("child");
            using Dir clone = simulated.Clone();

            Assert.Equal(ops.Capabilities.Backend, simulated.Backend);
            Assert.Equal(ops.Capabilities.Backend, child.Backend);
            Assert.Equal(ops.Capabilities.Backend, clone.Backend);

            Assert.Equal(Dir.ResolutionBackend, disk.Backend);
        }
    }

    /// <summary>
    /// Moving or linking an entry between handles on different filesystems fails as a move
    /// across devices, and neither filesystem is touched.
    /// </summary>
    [Fact]
    public void Moving_or_linking_between_a_simulated_tree_and_the_disk_is_refused()
    {
        (FakeFileSystem fs, FakePlatformOps ops, Dir simulated) = OpenSimulated();
        _ = fs.AddFile("/sandbox/entry");
        HostFile.WriteAllText(Path.Combine(_tree.HostPath, "entry"), "on disk");

        using Dir disk = OpenDisk();
        using (simulated)
        {
            int lookupsBefore = ops.HandleLookups;

            AssertCrossDevice(() => simulated.Rename("entry", disk, "moved"));
            AssertCrossDevice(() => disk.Rename("entry", simulated, "moved"));
            AssertCrossDevice(() => simulated.CreateHardLink("entry", disk, "linked"));
            AssertCrossDevice(() => disk.CreateHardLink("entry", simulated, "linked"));
            Assert.False(simulated.TryRename("entry", disk, "moved"));
            Assert.False(disk.TryRename("entry", simulated, "moved"));
            Assert.False(simulated.TryCreateHardLink("entry", disk, "linked"));
            Assert.False(disk.TryCreateHardLink("entry", simulated, "linked"));

            Assert.Equal(lookupsBefore, ops.HandleLookups);
            Assert.NotNull(fs.Find("/sandbox/entry"));
            Assert.Null(fs.Find("/sandbox/moved"));
            Assert.Null(fs.Find("/sandbox/linked"));
            Assert.Equal(["entry"], HostDirectory.GetFileSystemEntries(_tree.HostPath).Select(Path.GetFileName));
        }
    }

    /// <summary>
    /// Two simulated filesystems are as separate as a simulation and the disk, and neither is
    /// asked to act on the other's handle.
    /// </summary>
    [Fact]
    public void Moving_or_linking_between_two_simulated_trees_is_refused()
    {
        (FakeFileSystem fs, FakePlatformOps ops, Dir first) = OpenSimulated();
        (FakeFileSystem otherFs, FakePlatformOps otherOps, Dir second) = OpenSimulated();
        _ = fs.AddFile("/sandbox/entry");

        using (first)
        using (second)
        {
            int lookupsBefore = ops.HandleLookups;
            int otherLookupsBefore = otherOps.HandleLookups;

            AssertCrossDevice(() => first.Rename("entry", second, "moved"));
            AssertCrossDevice(() => first.CreateHardLink("entry", second, "linked"));
            Assert.False(first.TryRename("entry", second, "moved"));
            Assert.False(first.TryCreateHardLink("entry", second, "linked"));

            Assert.Equal(lookupsBefore, ops.HandleLookups);
            Assert.Equal(otherLookupsBefore, otherOps.HandleLookups);
            Assert.NotNull(fs.Find("/sandbox/entry"));
            Assert.Null(otherFs.Find("/sandbox/moved"));
            Assert.Null(otherFs.Find("/sandbox/linked"));
        }
    }

    /// <summary>A move within one simulated tree, between two of its handles, still works.</summary>
    [Fact]
    public void Moving_between_two_handles_on_one_simulated_tree_works()
    {
        (FakeFileSystem fs, _, Dir root) = OpenSimulated();
        _ = fs.AddFile("/sandbox/entry");

        using (root)
        {
            using Dir child = root.CreateDir("child");
            root.Rename("entry", child, "moved");

            Assert.Null(fs.Find("/sandbox/entry"));
            Assert.NotNull(fs.Find("/sandbox/child/moved"));
        }
    }

    private static void AssertCrossDevice(Action operation)
    {
        CapIOException refused = Assert.Throws<CapIOException>(operation);
        Assert.Equal(CapErrorKind.CrossDevice, refused.Kind);
    }
}
