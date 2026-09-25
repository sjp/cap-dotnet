using System.Runtime.Versioning;
using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;

namespace Cap.Std.Tests;

/// <summary>
/// That a handle resolves through the strongest strategy the platform offers.
/// </summary>
/// <remarks>
/// <para>
/// The two strategies produce the same handle for the same path and differ only in what an
/// attacker can do while they run. One is a single kernel operation that cannot leave the
/// subtree, so there is no instant at which anything can be substituted; the other opens
/// each name in turn against the handle the previous step produced, which cannot be
/// redirected elsewhere but is not one instant. A regression from the first to the second
/// would pass every test written about results.
/// </para>
/// <para>
/// That makes it worth asserting from the inside, by counting. A handle on a host whose
/// kernel resolves paths atomically must spend exactly one such operation on a path of any
/// length, and must open none of its names separately.
/// </para>
/// </remarks>
[Collection(DirTestGroup.Name)]
public sealed class DirResolutionDispatchTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>
    /// Where the kernel can resolve a whole path under confinement, that is what runs.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    public void A_nested_path_costs_one_kernel_operation_where_the_kernel_offers_one()
    {
        if (PlatformOps.Host is not LinuxPlatformOps ops)
        {
            Assert.Skip("This platform has no kernel-atomic confined open to dispatch to.");
            return;
        }

        if (!ops.Capabilities.SupportsConfinedOpen)
        {
            Assert.Skip(
                "This kernel does not offer the confined open, so the walk is the correct " +
                $"strategy here. Reason: {ops.ConfinedOpenUnavailableReason}");
        }

        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "a", "b", "c", "d"));

        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

        long confinedBefore = ops.ConfinedOpenAttempts;
        long componentsBefore = ops.ComponentOpens;

        using Dir nested = root.OpenDir("a/b/c/d");

        Assert.Equal(1, ops.ConfinedOpenAttempts - confinedBefore);
        Assert.Equal(0, ops.ComponentOpens - componentsBefore);
    }

    /// <summary>
    /// Where it cannot, the walk runs — and spends one open per name rather than pretending.
    /// </summary>
    /// <remarks>
    /// The mirror image of the test above, and it exists because the interesting failure is
    /// not "the walk ran" but "something ran and nobody can tell which". A count of zero
    /// opens on a host with no confined open would mean resolution had happened somewhere
    /// this test cannot see.
    /// </remarks>
    [Fact]
    [SupportedOSPlatform("linux")]
    public void A_nested_path_is_walked_a_name_at_a_time_where_the_kernel_offers_nothing()
    {
        if (PlatformOps.Host is not LinuxPlatformOps ops)
        {
            Assert.Skip("The per-name open count is kept only by this platform's implementation.");
            return;
        }

        if (ops.Capabilities.SupportsConfinedOpen)
        {
            Assert.Skip("This kernel offers the confined open, so the walk is not what dispatch chooses.");
        }

        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "a", "b", "c", "d"));

        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

        long confinedBefore = ops.ConfinedOpenAttempts;
        long componentsBefore = ops.ComponentOpens;

        using Dir nested = root.OpenDir("a/b/c/d");

        long walked = ops.ComponentOpens - componentsBefore;

        Assert.Equal(0, ops.ConfinedOpenAttempts - confinedBefore);
        Assert.True(walked >= 4, $"A path of four names was resolved in {walked} opens.");
    }

    /// <summary>
    /// An operation that acts on a name resolves the rest of the path through the same
    /// strongest strategy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Worth asserting separately because these operations cannot be expressed as an open at
    /// all: they stop one component short, and the kernel-atomic call has no way to be asked
    /// for that. The path is therefore divided first and only the part ahead of the last
    /// component is handed to the kernel — which keeps the guarantee where it was, but only
    /// if the division actually happens. Resolving the prefix by walking it instead would
    /// work, produce identical results, and quietly hand every caller of these operations the
    /// weaker property on the one platform that offers the stronger one.
    /// </para>
    /// <para>
    /// A single name costs nothing at all: there is no prefix, so the name is used against
    /// the handle the caller already holds.
    /// </para>
    /// </remarks>
    [Fact]
    [SupportedOSPlatform("linux")]
    public void Acting_on_a_name_resolves_the_rest_of_the_path_the_same_way()
    {
        if (PlatformOps.Host is not LinuxPlatformOps ops)
        {
            Assert.Skip("This platform has no kernel-atomic confined open to dispatch to.");
            return;
        }

        if (!ops.Capabilities.SupportsConfinedOpen)
        {
            Assert.Skip(
                "This kernel does not offer the confined open, so the walk is the correct " +
                $"strategy here. Reason: {ops.ConfinedOpenUnavailableReason}");
        }

        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "a", "b", "c"));
        File.WriteAllText(Path.Combine(_tree.HostPath, "a", "b", "c", "doomed"), "contents");
        File.WriteAllText(Path.Combine(_tree.HostPath, "alone"), "contents");

        using Dir root = Dir.Open(_tree.HostPath, AmbientAuthority.Acquire());

        long confinedBefore = ops.ConfinedOpenAttempts;
        long componentsBefore = ops.ComponentOpens;

        root.DeleteFile("a/b/c/doomed");

        Assert.Equal(1, ops.ConfinedOpenAttempts - confinedBefore);
        Assert.Equal(0, ops.ComponentOpens - componentsBefore);

        confinedBefore = ops.ConfinedOpenAttempts;
        componentsBefore = ops.ComponentOpens;

        root.DeleteFile("alone");

        Assert.Equal(0, ops.ConfinedOpenAttempts - confinedBefore);
        Assert.Equal(0, ops.ComponentOpens - componentsBefore);
    }
}
