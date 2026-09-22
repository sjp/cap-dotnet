using Cap.Primitives;

namespace Cap.Std.Tests;

/// <summary>
/// A throwaway directory for one test class to build its scenarios in.
/// </summary>
/// <remarks>
/// <para>
/// The library's own scratch directory, used by the suite that tests the library. That is
/// deliberate rather than convenient: the cleanup it performs is a handle-by-handle descent
/// through whatever the test left behind, and the trees these tests leave behind are full of
/// exactly what makes that hard — symbolic links aimed outside, names planted as raw bytes,
/// objects that are not files or directories at all. Running it after every test in the suite
/// exercises it against far more shapes than a test written for it would think to build.
/// </para>
/// <para>
/// The host path is kept alongside the handle, because the scenarios have to be built by
/// something that is not the code under test. A test that arranged its attack through the
/// capability API would be arranging an attack that API had already agreed to.
/// </para>
/// </remarks>
internal sealed class ScratchTree : IDisposable
{
    private readonly CapTempDir _temp;

    /// <summary>Creates the directory under the system's temporary location.</summary>
    public ScratchTree()
    {
        // Opened by path and the scratch directory made inside it, rather than asking for one
        // in the temporary location directly, because the path of what comes back is then
        // known without having to ask the handle where it is -- and asking is a thing some
        // hosts cannot answer.
        string location = Path.GetTempPath();
        using Dir parent = Dir.Open(location, AmbientAuthority.Acquire());

        _temp = CapTempDir.NewIn(parent);
        HostPath = Path.Combine(location, _temp.Name);
    }

    /// <summary>The directory, as the authority a test hands to the code under test.</summary>
    public Dir Directory => _temp.Directory;

    /// <summary>The directory, as an ordinary path for the ambient set-up to build in.</summary>
    public string HostPath { get; }

    /// <summary>Removes the directory and everything the test left in it.</summary>
    public void Dispose() => _temp.Dispose();
}
