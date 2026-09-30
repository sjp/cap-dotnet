using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cap.Tests;

namespace Cap.Escape.Tests;

/// <summary>
/// The parts of arranging a tree the framework has no API for.
/// </summary>
/// <remarks>
/// Path-based and ambient, like the rest of the set-up: these build what the library is then
/// attacked with, and must not be the library.
/// </remarks>
internal static partial class HostFilesystem
{
    /// <summary>How long one of the system's own tools is given before it is killed.</summary>
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Creates a junction, which unlike a symbolic link needs no privilege.</summary>
    /// <remarks>
    /// Through the shell because the framework has no API for one. The paths are quoted rather
    /// than passed as separate arguments: the shell re-parses its own command line, and a
    /// temporary directory can contain a space.
    /// </remarks>
    public static void CreateJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Junctions exist only on Windows.");
        }

        using Process process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new IOException("Could not start the shell to create a junction.");

        (string output, string errors) = ChildProcessWait.Finish(process, "mklink", ToolTimeout);

        if (!HostDirectory.Exists(link))
        {
            throw new IOException($"Could not create a junction at '{link}': {errors}{output}");
        }
    }

    /// <summary>
    /// The short name the volume generated for a path's last component, or null when it has
    /// none.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? ShortNameOf(string path)
    {
        char[] buffer = new char[1024];
        uint written = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        if (written == 0 || written >= buffer.Length)
        {
            return null;
        }

        string shortPath = new(buffer, 0, (int)written);
        int separator = shortPath.LastIndexOf('\\');
        return separator < 0 ? shortPath : shortPath[(separator + 1)..];
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint bufferLength);
}
