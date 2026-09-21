using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cap.Tests;

/// <summary>
/// Preconditions that must hold in every cap-dotnet test assembly.
/// </summary>
internal static partial class TestEnvironment
{
    /// <summary>
    /// True when the test process can open files the filesystem's own permission checks
    /// should have refused it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This matters more than it looks. Much of the escape corpus is negative tests --
    /// "this operation must fail" -- and a process that outranks the permission system can
    /// turn one of those green without the containment logic ever having run.
    /// </para>
    /// <para>
    /// The two platforms need different questions asked, because "root" and "administrator"
    /// are not the same kind of thing. On Unix, euid 0 bypasses DAC outright, so that is
    /// the whole question. On Windows, being an administrator bypasses nothing by itself:
    /// ACLs are still evaluated against an elevated token. What bypasses them is a specific
    /// set of privileges, and those sit in the token *disabled* by default. So the
    /// meaningful question there is whether one of them is enabled, not whether the user is
    /// an administrator.
    /// </para>
    /// <para>
    /// Asking the blunt "is this elevated?" question on Windows would also make the corpus
    /// impossible to write. Creating a symbolic link there requires either an elevated
    /// token or Developer Mode, and the symlink cases are the heart of the suite -- so the
    /// tests need the elevation that the blunt check would forbid, while still needing the
    /// permission system underneath them to be real.
    /// </para>
    /// </remarks>
    public static bool CanBypassFilePermissions
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsAclBypassPrivilegeEnabled();
            }

            return Environment.IsPrivilegedProcess;
        }
    }

    /// <summary>
    /// Privileges that let a file open ignore an ACL that should have stopped it.
    /// </summary>
    /// <remarks>
    /// <c>SeBackupPrivilege</c> is the one that matters most here: it skips the ACL check on
    /// open when the caller passes <c>FILE_FLAG_BACKUP_SEMANTICS</c>, which every directory
    /// open must pass. <c>SeRestorePrivilege</c> is its write-side counterpart, and
    /// <c>SeTakeOwnershipPrivilege</c> lets a caller grant itself access it was denied.
    /// </remarks>
    private static readonly string[] AclBypassPrivileges =
    [
        "SeBackupPrivilege",
        "SeRestorePrivilege",
        "SeTakeOwnershipPrivilege",
    ];

    private const uint TokenQuery = 0x0008;
    private const uint TokenPrivilegesClass = 3;
    private const uint SePrivilegeEnabled = 0x0002;

    /// <summary>Size of one <c>LUID_AND_ATTRIBUTES</c>: LUID (8 bytes) plus attributes (4).</summary>
    private const int LuidAndAttributesSize = 12;

    [SupportedOSPlatform("windows")]
    private static bool WindowsAclBypassPrivilegeEnabled()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out nint token))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not open the process token to check for ACL-bypassing privileges. " +
                "The guard fails loudly rather than assuming the process is unprivileged.");
        }

        try
        {
            // First call sizes the buffer; it is expected to fail with ERROR_INSUFFICIENT_BUFFER.
            _ = GetTokenInformation(token, TokenPrivilegesClass, null, 0, out uint needed);
            if (needed < sizeof(uint))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not size the process token's privilege list.");
            }

            byte[] buffer = new byte[needed];
            if (!GetTokenInformation(token, TokenPrivilegesClass, buffer, needed, out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not read the process token's privilege list.");
            }

            return AnyEnabled(buffer);
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    /// <summary>
    /// Scans a <c>TOKEN_PRIVILEGES</c> block for an enabled privilege from
    /// <see cref="AclBypassPrivileges"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool AnyEnabled(ReadOnlySpan<byte> tokenPrivileges)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(tokenPrivileges);

        // Parsing a kernel-supplied buffer, so the length is checked rather than trusted.
        long required = sizeof(uint) + ((long)count * LuidAndAttributesSize);
        if (required > tokenPrivileges.Length)
        {
            throw new InvalidOperationException(
                $"Token privilege list claims {count} entries, which does not fit in " +
                $"{tokenPrivileges.Length} bytes.");
        }

        foreach (string name in AclBypassPrivileges)
        {
            if (!LookupPrivilegeValue(null, name, out Luid wanted))
            {
                // A privilege this version of Windows does not define cannot be enabled.
                if (Marshal.GetLastWin32Error() == 1313 /* ERROR_NO_SUCH_PRIVILEGE */)
                {
                    continue;
                }

                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not look up the LUID for {name}.");
            }

            for (uint i = 0; i < count; i++)
            {
                int offset = sizeof(uint) + ((int)i * LuidAndAttributesSize);
                uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(tokenPrivileges[(offset + 8)..]);
                if ((attributes & SePrivilegeEnabled) == 0)
                {
                    continue;
                }

                uint low = BinaryPrimitives.ReadUInt32LittleEndian(tokenPrivileges[offset..]);
                int high = BinaryPrimitives.ReadInt32LittleEndian(tokenPrivileges[(offset + 4)..]);
                if (low == wanted.LowPart && high == wanted.HighPart)
                {
                    return true;
                }
            }
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        nint tokenHandle,
        uint tokenInformationClass,
        [Out] byte[]? tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);
}

public sealed class ProcessPrivilegeGuard
{
    [Fact]
    public void Test_process_cannot_bypass_file_permissions()
    {
        Assert.False(
            TestEnvironment.CanBypassFilePermissions,
            "cap-dotnet tests must not run with the ability to bypass file permissions: " +
            "negative containment tests can pass for the wrong reason when the process " +
            "outranks the permission system. On Unix, re-run as an ordinary user rather " +
            "than root. On Windows, being an administrator is fine -- but a backup, " +
            "restore or take-ownership privilege must not be enabled in the token.");
    }
}
