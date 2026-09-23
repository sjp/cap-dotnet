using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cap.Stress.Tests;

/// <summary>
/// The ambient moves an adversary makes, and the few the framework has no API for.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is done by path with the process's own authority, because this is the
/// attacker's side of the race and must not be the code under test. An attack arranged through
/// the capability API would be an attack that API had already agreed to.
/// </para>
/// <para>
/// The framework's own move methods are not used for the swaps, because they ask what kind of
/// object they are moving before moving it, and some refuse to move a symbolic link to a
/// directory at all. The attacker in this model has the plain system call, so that is what it
/// is given.
/// </para>
/// </remarks>
internal static partial class HostOps
{
    private const int AtCurrentDirectory = -100;
    private const uint LinuxRenameExchange = 2;
    private const uint DarwinRenameSwap = 2;

    private static volatile bool s_exchangeUnavailable;

    /// <summary>
    /// Swaps what two names hold, atomically where the host can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Linux and macOS can exchange two names in one step, and that is the sharper attack: at no
    /// instant is either name missing, so every resolution that races the swap meets one object
    /// or the other and none is turned away for finding nothing. Windows has no such call, and
    /// neither does every filesystem, so there it is three renames through a spare name, and
    /// a resolution landing between them finds the name missing.
    /// </para>
    /// <para>
    /// A three-step swap that fails part of the way is put back as far as it can be, so that one
    /// lost step does not leave the attacker unable to take the next.
    /// </para>
    /// </remarks>
    /// <exception cref="IOException">The swap could not be made.</exception>
    public static void Exchange(string first, string second)
    {
        if (!s_exchangeUnavailable)
        {
            if (TryExchangeAtomically(first, second, out int error))
            {
                return;
            }

            if (error is not (Enosys or Einval or Enotsup or EnotsupDarwin))
            {
                throw Failure("exchange", first, second, error);
            }

            s_exchangeUnavailable = true;
        }

        string spare = first + ".swap";
        Rename(first, spare);
        try
        {
            Rename(second, first);
        }
        catch (IOException)
        {
            TryRename(spare, first);
            throw;
        }

        try
        {
            Rename(spare, second);
        }
        catch (IOException)
        {
            TryRename(first, second);
            TryRename(spare, first);
            throw;
        }
    }

    /// <summary>
    /// Whether swaps on this host have been made in one step. Meaningful once one has been made.
    /// </summary>
    public static bool ExchangesAtomically =>
        (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && !s_exchangeUnavailable;

    /// <summary>Gives an entry a new name, whatever kind of entry it is.</summary>
    /// <exception cref="IOException">The rename could not be made.</exception>
    public static void Rename(string from, string to)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileExW(from, to, 0))
            {
                throw Failure("rename", from, to, Marshal.GetLastPInvokeError());
            }

            return;
        }

        if (RenameUnix(from, to) != 0)
        {
            throw Failure("rename", from, to, Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>Renames, and says whether it did rather than why it did not.</summary>
    public static bool TryRename(string from, string to)
    {
        try
        {
            Rename(from, to);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes an entry and everything beneath it without following a link anywhere.
    /// </summary>
    /// <remarks>
    /// Used to clear away what a round left behind. A link is removed as the link, never
    /// descended into, since the links these races plant point at the very directories the test
    /// is checking were never touched.
    /// </remarks>
    public static void RemoveWithoutFollowing(string path)
    {
        FileInfo entry = new(path);
        if (entry.LinkTarget is not null)
        {
            // A link to a directory is removed as a directory on Windows and as a file elsewhere.
            if (OperatingSystem.IsWindows() && entry.Attributes.HasFlag(FileAttributes.Directory))
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }
        }
        else if (Directory.Exists(path))
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(path))
            {
                RemoveWithoutFollowing(child);
            }

            Directory.Delete(path);
        }
        else if (entry.Exists)
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Whether symbolic links can be made in a directory.
    /// </summary>
    /// <remarks>
    /// On Windows making one needs a privilege or developer mode, and some volumes cannot hold
    /// one at all. Every race here is fought with a link aimed outside, so a host that cannot
    /// make one cannot stage them.
    /// </remarks>
    public static bool CanCreateSymbolicLinks(string directory)
    {
        string probe = Path.Join(directory, ".link-probe");
        try
        {
            Directory.CreateSymbolicLink(probe, directory);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (new FileInfo(probe).LinkTarget is not null)
            {
                File.Delete(probe);
            }
        }
    }

    /// <summary>The soft and hard limits on how many descriptors this process may hold.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static (ulong Soft, ulong Hard) GetDescriptorLimit()
    {
        if (GetRLimit(DescriptorLimitResource, out ResourceLimit limit) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the descriptor limit.");
        }

        return (limit.Current, limit.Maximum);
    }

    /// <summary>Sets the limit on how many descriptors this process may hold.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public static void SetDescriptorLimit(ulong soft, ulong hard)
    {
        ResourceLimit limit = new() { Current = soft, Maximum = hard };
        if (SetRLimit(DescriptorLimitResource, in limit) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not set the descriptor limit.");
        }
    }

    private static bool TryExchangeAtomically(string first, string second, out int error)
    {
        int result;
        if (OperatingSystem.IsLinux())
        {
            result = RenameAt2(AtCurrentDirectory, first, AtCurrentDirectory, second, LinuxRenameExchange);
        }
        else if (OperatingSystem.IsMacOS())
        {
            result = RenameExNp(first, second, DarwinRenameSwap);
        }
        else
        {
            error = Enosys;
            return false;
        }

        error = result == 0 ? 0 : Marshal.GetLastPInvokeError();
        return result == 0;
    }

    private static IOException Failure(string what, string from, string to, int error) =>
        new($"Could not {what} '{from}' and '{to}'.", new Win32Exception(error));

    private const int Enosys = 38;
    private const int Einval = 22;
    private const int Enotsup = 95;
    private const int EnotsupDarwin = 45;

    private static int DescriptorLimitResource => OperatingSystem.IsMacOS() ? 8 : 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct ResourceLimit
    {
        public ulong Current;
        public ulong Maximum;
    }

    [LibraryImport("libc", EntryPoint = "rename", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameUnix(string from, string to);

    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt2(int fromDirectory, string from, int toDirectory, string to, uint flags);

    [LibraryImport("libc", EntryPoint = "renamex_np", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameExNp(string from, string to, uint flags);

    [LibraryImport("libc", EntryPoint = "getrlimit", SetLastError = true)]
    private static partial int GetRLimit(int resource, out ResourceLimit limit);

    [LibraryImport("libc", EntryPoint = "setrlimit", SetLastError = true)]
    private static partial int SetRLimit(int resource, in ResourceLimit limit);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileExW(string from, string to, uint flags);
}
