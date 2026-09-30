using Cap.Primitives.Interop;
using Cap.Testing;
using Cap.Tests.Fakes;
using Xunit.Sdk;

namespace Cap.Primitives.Tests;

/// <summary>
/// The check every corpus row makes on the way out of a backend: that the backend it named
/// is the one that resolved.
/// </summary>
/// <remarks>
/// The check is what stands between a confined leg that quietly became a walk and thousands
/// of corpus rows passing on the walk under the confined open's name, so it is driven here
/// against the simulation, where either strategy can be chosen, and made to fail.
/// </remarks>
public sealed class BackendScopeTests
{
    /// <summary>
    /// A confined leg whose resolution walked fails the check, whatever the case came to.
    /// </summary>
    [Fact]
    public void A_confined_scope_whose_resolution_walked_fails()
    {
        FakePlatformOps ops = Simulation(confinedOpen: false);
        using SafeDirHandle root = OpenRoot(ops);

        BackendScope scope = new(Backends.ConfinedOpen, ops, new NoSubstitution());
        Resolve(root, "a/b");

        Assert.Throws<TrueException>(scope.AssertItRan);
    }

    /// <summary>A confined leg that resolved through the confined open passes the check.</summary>
    [Fact]
    public void A_confined_scope_whose_resolution_was_confined_passes()
    {
        FakePlatformOps ops = Simulation(confinedOpen: true);
        using SafeDirHandle root = OpenRoot(ops);

        BackendScope scope = new(Backends.ConfinedOpen, ops, new NoSubstitution());
        Resolve(root, "a/b");

        scope.AssertItRan();
        Assert.True(ops.ConfinedOpenAttempts > 0);
    }

    /// <summary>
    /// An operation that opens what it created is still held to having resolved its path
    /// through the confined open.
    /// </summary>
    [Fact]
    public void A_confined_scope_that_opened_what_it_created_must_still_have_resolved_confined()
    {
        FakePlatformOps walked = Simulation(confinedOpen: false);
        using SafeDirHandle walkedRoot = OpenRoot(walked);
        BackendScope walking = new(Backends.InMemoryConfined, walked, new NoSubstitution());
        Resolve(walkedRoot, "a/b");

        Assert.Throws<TrueException>(() => walking.AssertItRanOpeningWhatItCreated(resolvedAPath: true));

        FakePlatformOps confined = Simulation(confinedOpen: true);
        using SafeDirHandle confinedRoot = OpenRoot(confined);
        BackendScope confining = new(Backends.InMemoryConfined, confined, new NoSubstitution());
        Resolve(confinedRoot, "a/b");

        confining.AssertItRanOpeningWhatItCreated(resolvedAPath: true);
    }

    /// <summary>A walk leg that used the confined open fails the check, as it always has.</summary>
    [Fact]
    public void A_walk_scope_that_made_a_confined_open_fails()
    {
        FakePlatformOps ops = Simulation(confinedOpen: true);
        using SafeDirHandle root = OpenRoot(ops);

        BackendScope scope = new(Backends.LinuxWalk, ops, new NoSubstitution());
        Resolve(root, "a/b");

        Assert.Throws<EqualException>(scope.AssertItRan);
    }

    private static FakePlatformOps Simulation(bool confinedOpen)
    {
        FakeFileSystem fs = new() { SupportsConfinedOpen = confinedOpen };
        _ = fs.AddDirectory("sandbox/a/b");
        return new FakePlatformOps(fs);
    }

    private static SafeDirHandle OpenRoot(FakePlatformOps ops)
    {
        CapResult<SafeDirHandle> result = ops.OpenAmbientDirectory("sandbox", CapAccess.Read);
        Assert.True(result.IsSuccess, result.Error.FailureDescription);
        return result.Value!;
    }

    private static void Resolve(SafeDirHandle root, string raw)
    {
        Assert.True(
            CapPath.TryParse(raw, CapPathSyntax.Unix, ParentLinkPolicy.Preserve, out CapPath path, out CapPathError error),
            $"'{raw}' did not parse: {error}");
        CapResult<SafeDirHandle> result =
            Resolver.OpenDirectory(root, path, CapAccess.Read, ConfinedResolveOptions.None);
        Assert.True(result.IsSuccess, $"'{raw}': {result.Error.FailureDescription}");
        result.Value!.Dispose();
    }

    /// <summary>
    /// Stands in for substituting the host, which these tests leave alone: the scope is only
    /// asked about the counters of the instance it was given.
    /// </summary>
    private sealed class NoSubstitution : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
