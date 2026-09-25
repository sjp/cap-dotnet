using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;
using Cap.Tests.Fakes;

namespace Cap.Primitives.Tests;

/// <summary>
/// That a handle carries the backend that issued it, and nothing else ever handles it.
/// </summary>
/// <remarks>
/// <para>
/// A handle's value is meaningful only to the implementation that produced it: a descriptor
/// number to the kernel, an index to the simulation. So each handle records its backend, every
/// handle produced from it records the same one, and resolution goes through that backend
/// rather than through whatever the process's host happens to be. These tests use the
/// simulation and a real directory side by side. They replace nothing process-wide, and so
/// they run in parallel with everything else.
/// </para>
/// </remarks>
public sealed class HandleBackendTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cap-backend-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// Every handle the simulation gives out records it: the first, a copy, and each step of a
    /// walk, whether it is kept or handed back.
    /// </summary>
    [Fact]
    public void Every_handle_produced_from_a_handle_records_the_same_backend()
    {
        FakeFileSystem fs = new();
        _ = fs.AddDirectory("sandbox/a/b");
        FakePlatformOps ops = new(fs);

        using SafeDirHandle root = OpenSimulated(ops, "sandbox");
        Assert.Same(ops, root.Backend);

        using SafeDirHandle copy = ops.DuplicateDirectory(root).Value!;
        Assert.Same(ops, copy.Backend);

        using SafeDirHandle walked = Resolve(root, "a/b");
        Assert.Same(ops, walked.Backend);

        CapResult<ResolvedParent> parent =
            Resolver.ResolveParent(root, Parse("a/b/new"), ConfinedResolveOptions.None);
        Assert.True(parent.IsSuccess, parent.Error.FailureDescription);
        using (ResolvedParent resolved = parent.Value!)
        {
            Assert.Same(ops, resolved.Directory.Backend);
        }

        CapResult<OpenedNode> node = Resolver.OpenNode(
            root, Parse("a"), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);
        Assert.True(node.IsSuccess, node.Error.FailureDescription);
        using (OpenedNode opened = node.Value!)
        {
            Assert.Same(ops, opened.Backend);
            Assert.Same(ops, opened.Directory!.Backend);
        }
    }

    /// <summary>A file reached by a walk records the backend of the directory it was reached from.</summary>
    [Fact]
    public void A_file_opened_without_knowing_its_kind_records_its_backend()
    {
        FakeFileSystem fs = new();
        _ = fs.AddFile("sandbox/plain.txt");
        FakePlatformOps ops = new(fs);

        using SafeDirHandle root = OpenSimulated(ops, "sandbox");
        CapResult<OpenedNode> node = Resolver.OpenNode(
            root, Parse("plain.txt"), FileOpenRequest.Existing(FileAccess.Read), ConfinedResolveOptions.None);
        Assert.True(node.IsSuccess, node.Error.FailureDescription);

        using OpenedNode opened = node.Value!;
        Assert.NotNull(opened.File);
        Assert.Same(ops, opened.Backend);
    }

    /// <summary>
    /// A simulated tree and a real one resolve at the same time, each through its own backend,
    /// and neither sees the other's names.
    /// </summary>
    [Fact]
    public void A_simulated_tree_and_a_real_one_are_used_at_once()
    {
        _ = Directory.CreateDirectory(Path.Combine(_root, "on-disk"));

        FakeFileSystem fs = new();
        _ = fs.AddDirectory("sandbox/in-memory");
        FakePlatformOps ops = new(fs);

        using SafeDirHandle simulated = OpenSimulated(ops, "sandbox");
        using SafeDirHandle real = PlatformOps.Host.OpenAmbientDirectory(_root, CapAccess.Read).Value!;

        Assert.Same(ops, simulated.Backend);
        Assert.True(real.Backend.IssuesKernelHandles);

        using (SafeDirHandle found = Resolve(simulated, "in-memory"))
        {
            Assert.Same(ops, found.Backend);
        }

        using (SafeDirHandle found = Resolve(real, "on-disk"))
        {
            Assert.Same(real.Backend, found.Backend);
        }

        Assert.Equal(
            CapErrorCategory.NotFound,
            Resolver.OpenDirectory(simulated, Parse("on-disk"), CapAccess.Read, ConfinedResolveOptions.None).Error.Category);
        Assert.Equal(
            CapErrorCategory.NotFound,
            Resolver.OpenDirectory(real, Parse("in-memory"), CapAccess.Read, ConfinedResolveOptions.None).Error.Category);
    }

    /// <summary>Closing a handle goes to the backend that issued it, which forgets it.</summary>
    [Fact]
    public void Closing_a_handle_is_reported_to_its_backend()
    {
        FakeFileSystem fs = new();
        _ = fs.AddDirectory("sandbox");
        FakePlatformOps ops = new(fs);

        SafeDirHandle root = OpenSimulated(ops, "sandbox");
        using SafeDirHandle copy = ops.DuplicateDirectory(root).Value!;
        nint closedValue = root.DangerousGetHandle();

        root.Dispose();

        Assert.Equal(1, ops.OpenHandleCount);
        Assert.False(ops.CloseDirectory(closedValue), "The closed handle's entry was still in the table.");
        Assert.True(ops.StatHandle(copy, out _).IsSuccess);
    }

    /// <summary>
    /// The simulation refuses a handle it did not issue rather than looking its number up.
    /// </summary>
    /// <remarks>
    /// The guard behind every check the public layer makes. A real descriptor's number could be
    /// an entry in the simulation's table by coincidence, and an answer about that entry would
    /// be an answer about the wrong directory.
    /// </remarks>
    [Fact]
    public void A_simulated_backend_refuses_a_handle_it_did_not_issue()
    {
        FakeFileSystem fs = new();
        _ = fs.AddDirectory("sandbox");
        FakePlatformOps ops = new(fs);
        FakePlatformOps other = new(fs);

        using SafeDirHandle foreign = OpenSimulated(other, "sandbox");

        _ = Assert.Throws<InvalidOperationException>(() => ops.StatHandle(foreign, out _));
    }

    /// <summary>
    /// Two handles may share a call when one backend issued both, or when both are the
    /// kernel's own objects, and not otherwise.
    /// </summary>
    [Fact]
    public void Handles_share_a_backend_only_within_one_filesystem()
    {
        FakeFileSystem fs = new();
        _ = fs.AddDirectory("sandbox");
        FakePlatformOps ops = new(fs);
        FakePlatformOps other = new(fs);

        using SafeDirHandle simulated = OpenSimulated(ops, "sandbox");
        using SafeDirHandle sibling = OpenSimulated(ops, "sandbox");
        using SafeDirHandle foreign = OpenSimulated(other, "sandbox");
        using SafeDirHandle real = PlatformOps.Host.OpenAmbientDirectory(_root, CapAccess.Read).Value!;
        using SafeDirHandle realCopy = PlatformOps.Host.DuplicateDirectory(real).Value!;

        Assert.True(simulated.SharesBackendWith(sibling));
        Assert.True(real.SharesBackendWith(realCopy));
        Assert.False(simulated.SharesBackendWith(foreign));
        Assert.False(simulated.SharesBackendWith(real));
        Assert.False(real.SharesBackendWith(simulated));
    }

    /// <summary>
    /// A socket cannot be named beneath a simulated directory, which has no kernel object to
    /// name it by.
    /// </summary>
    [Fact]
    public void A_socket_cannot_be_named_beneath_a_simulated_directory()
    {
        if (!OperatingSystem.IsLinux() || !UnixSocketNaming.IsSupported)
        {
            Assert.Skip("Sockets are named beneath a directory handle only on Linux with the descriptor directory present.");
        }

        FakeFileSystem fs = new();
        _ = fs.AddDirectory("sandbox");
        FakePlatformOps ops = new(fs);
        using SafeDirHandle simulated = OpenSimulated(ops, "sandbox");
        int handlesBefore = ops.OpenHandleCount;

        Assert.Equal(CapErrorCategory.NotSupported, UnixSocketNaming.ForBind(simulated, "listener").Error.Category);
        Assert.Equal(CapErrorCategory.NotSupported, UnixSocketNaming.ForConnect(simulated, "listener").Error.Category);
        Assert.Equal(handlesBefore, ops.OpenHandleCount);
    }

    private static SafeDirHandle OpenSimulated(FakePlatformOps ops, string path)
    {
        CapResult<SafeDirHandle> result = ops.OpenAmbientDirectory(path, CapAccess.Read);
        Assert.True(result.IsSuccess, result.Error.FailureDescription);
        return result.Value!;
    }

    private static SafeDirHandle Resolve(SafeDirHandle root, string path)
    {
        CapResult<SafeDirHandle> result =
            Resolver.OpenDirectory(root, Parse(path), CapAccess.Read, ConfinedResolveOptions.None);
        Assert.True(result.IsSuccess, $"'{path}': {result.Error.FailureDescription}");
        return result.Value!;
    }

    private static CapPath Parse(string raw)
    {
        Assert.True(
            CapPath.TryParse(raw, CapPathSyntax.Unix, ParentLinkPolicy.Preserve, out CapPath path, out CapPathError error),
            $"'{raw}' did not parse: {error}");
        return path;
    }
}
