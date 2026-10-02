using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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
