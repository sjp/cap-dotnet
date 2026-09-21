using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;
using Cap.Tests;

namespace Cap.Primitives.Tests;

/// <summary>
/// Runs the capability probe against a kernel that has been made to refuse the confined
/// open, and reports what it concluded.
/// </summary>
/// <remarks>
/// <para>
/// This is the other half of a test, and it lives in a process of its own because a syscall
/// filter cannot be taken back. Installing one changes the host for every test that runs
/// afterwards, so the test that wants a hostile kernel starts a second copy of this program,
/// tells it which failure to arrange, and reads the single line it prints.
/// </para>
/// <para>
/// It answers before the test platform starts. The probe settles on first use and is cached
/// for the life of the process, so anything that resolved a path first would fix the answer
/// before the filter existed.
/// </para>
/// </remarks>
internal static class Openat2DemotionChild
{
    /// <summary>
    /// Set to <c>EPERM</c> or <c>ENOSYS</c> to make this process a probe report rather than
    /// a test run.
    /// </summary>
    public const string RequestVariable = "CAPDOTNET_TEST_PROBE_CHILD";

    /// <summary>Marks the one line of output the parent reads.</summary>
    public const string ReportPrefix = "probe-report ";

    /// <summary>What the report says when the filter could not be installed at all.</summary>
    public const string FilterUnavailable = "unavailable";

    [ModuleInitializer]
    internal static void ReportIfRequested()
    {
        string? requested = Environment.GetEnvironmentVariable(RequestVariable);
        if (string.IsNullOrEmpty(requested) || !OperatingSystem.IsLinux())
        {
            return;
        }

        Console.Out.WriteLine(ReportPrefix + Describe(requested));
        Console.Out.Flush();

        // Never reaches the test platform: this process exists to answer one question, and
        // running a suite under a filter it installed for itself would be a different test.
        Environment.Exit(0);
    }

    [SupportedOSPlatform("linux")]
    private static string Describe(string requested)
    {
        if (!SeccompFilter.TryParseErrno(requested, out int errno) ||
            !SeccompFilter.TryDenyOpenat2(errno))
        {
            return "filter=" + FilterUnavailable;
        }

        LinuxPlatformOps ops = new();

        // Asked for twice. The probe is meant to settle once and be believed; a
        // implementation that re-probed per call would be issuing a refused syscall on every
        // resolution, which is a syscall storm in exactly the deployments least able to
        // absorb one, and the attempt counter is what makes the difference visible.
        CapErrorCategory first = AttemptConfinedOpen(ops);
        CapErrorCategory second = AttemptConfinedOpen(ops);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"filter=installed backend={ops.Capabilities.Backend} errno={ops.ConfinedOpenProbeErrno} " +
            $"attempts={ops.ConfinedOpenAttempts} first={first} second={second} " +
            $"reason={ops.ConfinedOpenUnavailableReason}");
    }

    [SupportedOSPlatform("linux")]
    private static CapErrorCategory AttemptConfinedOpen(LinuxPlatformOps ops)
    {
        CapResult<SafeDirHandle> root = ops.OpenAmbientDirectory(
            Environment.CurrentDirectory, CapAccess.Read);
        if (!root.IsSuccess)
        {
            return root.Error.Category;
        }

        using SafeDirHandle handle = root.Value;
        CapResult<SafeDirHandle> confined = ops.OpenConfinedDirectory(
            handle, ".", CapAccess.Read, ConfinedResolveOptions.None);

        if (!confined.IsSuccess)
        {
            return confined.Error.Category;
        }

        confined.Value.Dispose();
        return CapErrorCategory.None;
    }
}
