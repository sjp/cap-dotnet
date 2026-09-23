using System.Diagnostics;
using System.Reflection;

namespace Cap.Stress.Tests;

/// <summary>
/// A second copy of this test program, started to do one thing and report on it.
/// </summary>
/// <remarks>
/// Some things cannot be done in the process running the suite: lowering the descriptor limit
/// would starve the test framework of the descriptors it needs, and an attacker in the same
/// process shares its scheduler and its descriptor table with the code it is attacking. The
/// child is this same program, told by an environment variable which job it has, and it
/// answers before the test platform starts.
/// </remarks>
internal static class ChildProcess
{
    /// <summary>Starts the child with its job in the environment.</summary>
    public static Process Start(IReadOnlyDictionary<string, string> environment)
    {
        ProcessStartInfo start = new()
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // The test program may have been started through its own launcher or through the
        // shared host, and only the first can be re-run by path alone.
        string host = Environment.ProcessPath ??
            throw new InvalidOperationException("The running program has no path to re-launch.");

        start.FileName = host;
        if (Path.GetFileNameWithoutExtension(host) is "dotnet")
        {
            string assembly = Assembly.GetExecutingAssembly().GetName().Name + ".dll";
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, assembly));
        }

        foreach ((string name, string value) in environment)
        {
            start.Environment[name] = value;
        }

        return Process.Start(start) ?? throw new InvalidOperationException("The child did not start.");
    }
}
