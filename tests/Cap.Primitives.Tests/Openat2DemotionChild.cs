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
    /// Set to <c>EPERM</c>, <c>EACCES</c> or <c>ENOSYS</c> to make this process a probe
    /// report rather than a test run, or to one of the working-directory requests below.
    /// </summary>
    public const string RequestVariable = "CAPDOTNET_TEST_PROBE_CHILD";

    /// <summary>
    /// Probe with no filter, from whatever working directory the parent started this process
    /// in — which the parent makes one this process may search but not list.
    /// </summary>
    public const string UnreadableWorkingDirectory = "CWD-0111";

    /// <summary>Probe with no filter, after removing this process's own working directory.</summary>
    public const string RemovedWorkingDirectory = "CWD-REMOVED";

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
        string filter;
        if (requested is UnreadableWorkingDirectory)
        {
            filter = "none";
        }
        else if (requested is RemovedWorkingDirectory)
        {
            string doomed = Path.Combine(Path.GetTempPath(), "cap-probe-cwd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(doomed);
            Directory.SetCurrentDirectory(doomed);
            Directory.Delete(doomed);
            filter = "none";
        }
        else if (!SeccompFilter.TryParseErrno(requested, out int errno) ||
            !SeccompFilter.TryDenyOpenat2(errno))
        {
            return "filter=" + FilterUnavailable;
        }
        else
        {
            filter = "installed";
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
            $"filter={filter} backend={ops.Capabilities.Backend} errno={ops.ConfinedOpenProbeErrno} " +
            $"attempts={ops.ConfinedOpenAttempts} first={first} second={second} " +
            $"reason={ops.ConfinedOpenUnavailableReason}");
    }

    [SupportedOSPlatform("linux")]
    private static CapErrorCategory AttemptConfinedOpen(LinuxPlatformOps ops)
    {
        // Not the working directory: some runs start this process in one it may not list, or
        // remove it from under itself, and the attempts must not fail for that reason.
        CapResult<SafeDirHandle> root = ops.OpenAmbientDirectory(
            Path.GetTempPath(), CapAccess.Read);
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
