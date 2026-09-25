using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Cap.Testing;

/// <summary>
/// Giving an existing file a second name on disk, which the framework has no API for.
/// </summary>
/// <remarks>
/// Path-based and ambient, like the rest of the set-up: this builds what the library is then
/// tested against, and must not be the library.
/// </remarks>
internal static partial class HardLinks
{
    /// <summary>Gives an existing file a second name.</summary>
    public static void Create(string existing, string link)
    {
        bool created = OperatingSystem.IsWindows()
            ? CreateHardLinkW(link, existing, 0)
            : LinkUnix(existing, link) == 0;

        if (!created)
        {
            throw new IOException(
                $"Could not link '{link}' to '{existing}'.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinkUnix(string existing, string link);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string link, string existing, nint securityAttributes);
}
