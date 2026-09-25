using Cap.Primitives.Interop;
using Cap.Tests.Fakes;

namespace Cap.Primitives.Tests;

/// <summary>
/// What the simulated filesystem can be made to do.
/// </summary>
/// <remarks>
/// The simulation is a test instrument, and an instrument that is wrong is worse than none:
/// resolution logic tested against a model that cannot express an attack would pass while
/// being defenceless against it. So the three things it exists to model — links, volume
/// boundaries, and mutation timed to land between two steps — are asserted here directly,
/// before anything is built on top of them.
/// </remarks>
public sealed class SimulatedFilesystemTests
{
    /// <summary>A link is reported as a link rather than followed, as on every real platform.</summary>
    [Fact]
    public void A_link_is_reported_rather_than_followed()
    {
        FakeFileSystem fs = new();
        fs.AddDirectory("real");
        fs.AddSymbolicLink("link", "real");

        FakePlatformOps ops = new(fs);
        using SafeDirHandle root = OpenRoot(ops, fs);

        CapResult<SafeDirHandle> result = ops.OpenChildDirectory(root, "link", CapAccess.Read);
        Assert.False(result.IsSuccess);
        Assert.Equal(CapErrorCategory.SymbolicLink, result.Error.Category);

        CapResult<string> target = ops.ReadChildLink(root, "link");
        Assert.True(target.IsSuccess);
        Assert.Equal("real", target.Value);
    }

    /// <summary>
    /// A reparse point that is not a filesystem link is refused rather than read as one.
    /// </summary>
    [Fact]
    public void An_opaque_reparse_point_is_refused()
    {
        FakeFileSystem fs = new();
        fs.AddOpaqueReparsePoint("alias", 0x8000001B);

        FakePlatformOps ops = new(fs);
        using SafeDirHandle root = OpenRoot(ops, fs);

        CapResult<SafeDirHandle> result = ops.OpenChildDirectory(root, "alias", CapAccess.Read);
        Assert.False(result.IsSuccess);
        Assert.Equal(CapErrorCategory.Reparse, result.Error.Category);
    }

    /// <summary>A mount point is a different volume, and can be refused as one.</summary>
    [Fact]
    public void A_mount_boundary_is_visible_and_can_be_refused()
    {
        FakeFileSystem fs = new() { SupportsConfinedOpen = true };
        fs.AddDirectory("plain");
        fs.AddMountPoint("mounted", volumeId: 99);

        FakePlatformOps ops = new(fs);
        using SafeDirHandle root = OpenRoot(ops, fs);

        Assert.True(ops.StatChild(root, "plain", out CapNodeInfo plain).IsSuccess);
        Assert.True(ops.StatChild(root, "mounted", out CapNodeInfo mounted).IsSuccess);
        Assert.False(plain.CrossesVolumeBoundaryFrom(plain));
        Assert.True(mounted.CrossesVolumeBoundaryFrom(plain));

        CapResult<SafeDirHandle> crossed =
            ops.OpenConfinedDirectory(root, "mounted", CapAccess.Read, ConfinedResolveOptions.None);
        Assert.True(crossed.IsSuccess, crossed.Error.FailureDescription);
        crossed.Value.Dispose();

        CapResult<SafeDirHandle> refused =
            ops.OpenConfinedDirectory(
                root, "mounted", CapAccess.Read, ConfinedResolveOptions.RefuseMountCrossing);
        Assert.False(refused.IsSuccess);
        Assert.Equal(CapErrorCategory.CrossDevice, refused.Error.Category);
    }

    /// <summary>
    /// A directory can be replaced with a link at the exact moment it is looked up.
    /// </summary>
    /// <remarks>
    /// This is the case that cannot be written against a real kernel without a loop and a
    /// stopwatch. Here the swap happens on a chosen lookup, every run, so the behaviour a
    /// walk shows under a concurrent attacker is an ordinary deterministic test.
    /// </remarks>
    [Fact]
    public void An_entry_can_be_swapped_between_two_steps_of_a_walk()
    {
        FakeFileSystem fs = new();
        fs.AddDirectory("a/b");

        FakePlatformOps ops = new(fs);
        using SafeDirHandle root = OpenRoot(ops, fs);

        CapResult<SafeDirHandle> first = ops.OpenChildDirectory(root, "a", CapAccess.Read);
        Assert.True(first.IsSuccess, first.Error.FailureDescription);
        using SafeDirHandle a = first.Value;

        // The attacker acts when, and only when, "b" is looked up -- which is after the
        // handle on "a" was taken and before the next step resolves.
        fs.BeforeLookup = (directory, name) =>
        {
            if (name == "b")
            {
                fs.Replace("a/b", new FakeNode
                {
                    Type = CapNodeType.SymbolicLink,
                    LinkTarget = "/elsewhere",
                    VolumeId = 1,
                    NodeId = fs.NextNodeId(),
                });
                fs.BeforeLookup = null;
            }
        };

        CapResult<SafeDirHandle> second = ops.OpenChildDirectory(a, "b", CapAccess.Read);
        Assert.False(second.IsSuccess);
        Assert.Equal(CapErrorCategory.SymbolicLink, second.Error.Category);
    }

    /// <summary>
    /// The simulated confined open refuses a path that climbs above the directory it started
    /// from, and allows one that climbs within it.
    /// </summary>
    [Fact]
    public void Confined_resolution_refuses_to_climb_out_but_not_to_climb()
    {
        FakeFileSystem fs = new() { SupportsConfinedOpen = true };
        fs.AddDirectory("a/b");
        fs.AddDirectory("a/sibling");

        FakePlatformOps ops = new(fs);
        using SafeDirHandle root = OpenRoot(ops, fs);

        CapResult<SafeDirHandle> inside =
            ops.OpenConfinedDirectory(root, "a/b/../sibling", CapAccess.Read, ConfinedResolveOptions.None);
        Assert.True(inside.IsSuccess, inside.Error.FailureDescription);
        inside.Value.Dispose();

        CapResult<SafeDirHandle> outside =
            ops.OpenConfinedDirectory(root, "a/../..", CapAccess.Read, ConfinedResolveOptions.None);
        Assert.False(outside.IsSuccess);
        Assert.Equal(CapErrorCategory.Escaped, outside.Error.Category);
    }

    /// <summary>
    /// A relative link that climbs is resolved against where it lives, not against where
    /// resolution began.
    /// </summary>
    [Fact]
    public void A_climbing_link_that_stays_inside_is_followed()
    {
        FakeFileSystem fs = new() { SupportsConfinedOpen = true };
        fs.AddDirectory("a/b");
        fs.AddDirectory("a/target");
        fs.AddSymbolicLink("a/b/up", "../target");

        FakePlatformOps ops = new(fs);
        using SafeDirHandle root = OpenRoot(ops, fs);

        CapResult<SafeDirHandle> result =
            ops.OpenConfinedDirectory(root, "a/b/up", CapAccess.Read, ConfinedResolveOptions.None);

        Assert.True(result.IsSuccess, result.Error.FailureDescription);
        using SafeDirHandle resolved = result.Value;
        Assert.True(ops.StatHandle(resolved, out CapNodeInfo info).IsSuccess);
        Assert.Equal(fs.Find("a/target")!.NodeId, info.NodeId);
    }

    /// <summary>An absolute link target leaves the subtree and is refused.</summary>
    [Fact]
    public void An_absolute_link_target_is_refused()
    {
        FakeFileSystem fs = new() { SupportsConfinedOpen = true };
        fs.AddSymbolicLink("escape", "/etc");

        FakePlatformOps ops = new(fs);
        using SafeDirHandle root = OpenRoot(ops, fs);

        CapResult<SafeDirHandle> result =
            ops.OpenConfinedDirectory(root, "escape", CapAccess.Read, ConfinedResolveOptions.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(CapErrorCategory.Escaped, result.Error.Category);
    }

    /// <summary>A chain of links longer than the budget stops rather than spinning.</summary>
    [Fact]
    public void A_link_chain_past_the_budget_stops()
    {
        FakeFileSystem fs = new() { SupportsConfinedOpen = true };
        fs.AddSymbolicLink("loop", "loop");

        FakePlatformOps ops = new(fs);
        using SafeDirHandle root = OpenRoot(ops, fs);

        CapResult<SafeDirHandle> result =
            ops.OpenConfinedDirectory(root, "loop", CapAccess.Read, ConfinedResolveOptions.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(CapErrorCategory.SymbolicLinkLoop, result.Error.Category);
    }

    /// <summary>
    /// When the simulated platform has no confined open, asking for one says so rather than
    /// falling back to something weaker without telling anyone.
    /// </summary>
    [Fact]
    public void A_platform_without_a_confined_open_says_so()
    {
        FakeFileSystem fs = new() { SupportsConfinedOpen = false };
        fs.AddDirectory("a");

        FakePlatformOps ops = new(fs);
        Assert.Equal(ResolutionBackend.PortableWalk, ops.Capabilities.Backend);

        using SafeDirHandle root = OpenRoot(ops, fs);
        CapResult<SafeDirHandle> result =
            ops.OpenConfinedDirectory(root, "a", CapAccess.Read, ConfinedResolveOptions.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(CapErrorCategory.NotSupported, result.Error.Category);
    }

    private static SafeDirHandle OpenRoot(FakePlatformOps ops, FakeFileSystem fs)
    {
        CapResult<SafeDirHandle> result = ops.OpenAmbientDirectory(string.Empty, CapAccess.Read);
        Assert.True(result.IsSuccess, result.Error.FailureDescription);
        _ = fs;
        return result.Value;
    }
}
