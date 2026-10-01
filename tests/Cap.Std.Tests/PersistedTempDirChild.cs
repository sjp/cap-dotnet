using System.Runtime.CompilerServices;
using Cap.Primitives;

namespace Cap.Std.Tests;

/// <summary>
/// Creates and disposes one scratch directory, and reports where it was.
/// </summary>
/// <remarks>
/// <para>
/// Half of a test, in a process of its own because the variable it exercises is read once per
/// process: by the time a test in the suite could set it, the running process has already
/// decided. So the variable is set for a second copy of this program, and the parent looks at
/// what the second copy left behind.
/// </para>
/// <para>
/// It answers before the test platform starts, because it is not running a suite: it does one
/// thing, prints the directory's path and leaves.
/// </para>
/// </remarks>
internal static class PersistedTempDirChild
{
    /// <summary>Set to any value to make this process the child.</summary>
    public const string RequestVariable = "CAPDOTNET_TEST_PERSIST_CHILD";

    [ModuleInitializer]
    internal static void CreateAndDisposeIfRequested()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RequestVariable)))
        {
            return;
        }

        string path;
        using (CapTempDir temp = CapTempDir.New(AmbientAuthority.Acquire()))
        {
            if (!temp.Directory.TryGetPath(AmbientAuthority.Acquire(), out string? found))
            {
                Environment.Exit(1);
            }

            path = found;
        }

        Console.Out.WriteLine(path);
        Console.Out.Flush();
        Environment.Exit(0);
    }
}
