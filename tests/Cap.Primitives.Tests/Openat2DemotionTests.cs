using System.Diagnostics;
using System.Reflection;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;
using Cap.Tests;

namespace Cap.Primitives.Tests;

/// <summary>
/// What happens when the kernel refuses the confined open.
/// </summary>
/// <remarks>
/// <para>
/// Two ways it can refuse, and both are ordinary. A kernel older than the syscall reports
/// that it is not implemented. A kernel that has it, running under a sandbox filter that
/// takes it away, reports a permission failure instead — which is what a container runtime
/// whose default profile predates the syscall does, and which is therefore the answer a
/// large share of real deployments get.
/// </para>
/// <para>
/// Recognising only the first would leave the second surfacing as an unexplained permission
/// error from operations that have nothing to do with permissions, on precisely the hosts
/// that are hardest to debug on. So both are asked of a real kernel, by taking the syscall
/// away from a child process and reading what the probe made of it. Asking this library to
/// stand down would not be the same test: that path never consults the kernel at all.
/// </para>
/// </remarks>
[Collection(PlatformOpsTestGroup.Name)]
public sealed class Openat2DemotionTests
{
    [Theory]
    [InlineData("EPERM", 1)]
    [InlineData("ENOSYS", 38)]
    public void A_refused_confined_open_demotes_to_the_walk(string denial, int expectedErrno)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string report = RunProbeChild(denial);
        if (report.Contains("filter=" + Openat2DemotionChild.FilterUnavailable, StringComparison.Ordinal))
        {
            Assert.Skip(
                "This host will not let the test install a syscall filter, so the kernel " +
                "cannot be made to refuse the confined open here. The probe's handling of a " +
                "refusal is unverified on this machine.");
        }

        Assert.Contains("backend=" + ResolutionBackend.PortableWalk, report, StringComparison.Ordinal);
        Assert.Contains("errno=" + expectedErrno, report, StringComparison.Ordinal);

        // The failure this guards against is a demotion that blames the wrong thing. An
        // invalid-argument answer would mean the request was malformed rather than refused,
        // and would disable the fast path everywhere while looking exactly like a kernel
        // that never had it.
        Assert.DoesNotContain("errno=22", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// Once the answer is in, the refused syscall is never issued again.
    /// </summary>
    /// <remarks>
    /// The probe exists to be asked once. Were it consulted per call, every resolution on a
    /// filtered host would make a syscall that is certain to fail before doing the work that
    /// was going to happen anyway — a cost paid over and over by the deployments least able
    /// to afford it. Two confined opens are attempted in the child and neither may reach the
    /// kernel.
    /// </remarks>
    [Theory]
    [InlineData("EPERM")]
    [InlineData("ENOSYS")]
    public void A_demoted_process_stops_asking_the_kernel(string denial)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string report = RunProbeChild(denial);
        if (report.Contains("filter=" + Openat2DemotionChild.FilterUnavailable, StringComparison.Ordinal))
        {
            Assert.Skip("This host will not let the test install a syscall filter.");
        }

        Assert.Contains("attempts=0", report, StringComparison.Ordinal);

        // And the caller is told the backend is unavailable rather than being handed a
        // quietly weaker resolution: the confined open reports what it is, and choosing the
        // fallback is a decision made above this layer.
        Assert.Contains("first=" + CapErrorCategory.NotSupported, report, StringComparison.Ordinal);
        Assert.Contains("second=" + CapErrorCategory.NotSupported, report, StringComparison.Ordinal);
    }

    /// <summary>
    /// Starts this same test program as a child, with the confined open denied, and returns
    /// the one line it reports.
    /// </summary>
    private static string RunProbeChild(string denial)
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

        start.Environment[Openat2DemotionChild.RequestVariable] = denial;

        // The child must not inherit a denial that is already in force, or it would report
        // on the filter this process is running under rather than the one it was asked to
        // install.
        start.Environment.Remove(SeccompFilter.DenyOpenat2Variable);

        // Nor the switch that turns the confined open off outright. This run exists to watch
        // the probe meet a refusal and record which one it was; a child told not to attempt
        // the syscall never issues it, reports no code, and would make the whole case pass
        // or fail according to how the suite was launched rather than according to what the
        // kernel did.
        if (OperatingSystem.IsLinux())
        {
            start.Environment.Remove(Openat2Probe.DisableVariableName);
        }

        using Process child = Process.Start(start) ??
            throw new InvalidOperationException("The probe child did not start.");

        string output = child.StandardOutput.ReadToEnd();
        string errors = child.StandardError.ReadToEnd();
        Assert.True(
            child.WaitForExit(ChildTimeout),
            "The probe child did not finish within " + ChildTimeout + "ms.");

        foreach (string line in output.Split('\n'))
        {
            if (line.StartsWith(Openat2DemotionChild.ReportPrefix, StringComparison.Ordinal))
            {
                return line;
            }
        }

        throw new InvalidOperationException(
            "The probe child reported nothing. Its output was:\n" + output + errors);
    }

    private const int ChildTimeout = 60_000;
}
