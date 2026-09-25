using Cap.Primitives;

namespace Cap.Std.Testing.Tests;

/// <summary>
/// The two ways an in-memory filesystem can resolve a path, for a test to run down both.
/// </summary>
internal static class Resolutions
{
    public static TheoryData<ResolutionBackend> Both => new()
    {
        ResolutionBackend.PortableWalk,
        ResolutionBackend.ConfinedOpen,
    };

    /// <summary>A filesystem with the running platform's rules, resolving as asked.</summary>
    public static InMemoryFileSystem Create(ResolutionBackend resolution) =>
        new(new InMemoryFileSystemOptions { Resolution = resolution, PathSyntax = CapPathSyntax.Unix });
}
