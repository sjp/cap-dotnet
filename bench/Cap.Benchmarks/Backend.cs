using BenchmarkDotNet.Jobs;
using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;

namespace Cap.Benchmarks;

/// <summary>
/// The resolution backends a benchmark run is split by, one BenchmarkDotNet job each.
/// </summary>
/// <remarks>
/// <para>
/// On Linux the same binary resolves either through the kernel's confined open, one syscall
/// however deep the path, or through the name-at-a-time walk that a kernel without that syscall
/// (or a seccomp profile that refuses it) falls back to. Both are what some real deployment
/// runs, so both are measured, and the walk is a job of its own so that the cost of not having
/// the confined open is a row that can be quoted rather than a guess.
/// </para>
/// <para>
/// The walk is selected with the documented environment variable, in a separate process, which
/// is how an operator would select it. Every benchmark then checks, before measuring anything,
/// that the process really is on the backend its job is named after: a job labelled with the
/// confined open that had quietly fallen back to the walk would publish the walk's numbers under
/// the wrong name.
/// </para>
/// </remarks>
internal static class Backend
{
    /// <summary>The variable a job sets to name the backend it expects to measure.</summary>
    public const string ExpectedVariable = "CAPDOTNET_BENCH_BACKEND";

    public const string ConfinedOpen = "openat2";
    public const string Walk = "walk";
    public const string Windows = "windows";

    /// <summary>
    /// One job per backend this host can run, each derived from <paramref name="template"/>.
    /// </summary>
    /// <param name="template">The run length and counts every job shares.</param>
    /// <param name="log">Told why a backend was left out, when one was.</param>
    public static IReadOnlyList<Job> Jobs(Job template, Action<string> log)
    {
        if (OperatingSystem.IsWindows())
        {
            return [Named(template, Windows)];
        }

        if (!OperatingSystem.IsLinux())
        {
            return [Named(template, Walk)];
        }

        var jobs = new List<Job>(2);
        string? unavailable = ConfinedOpenUnavailableReason();
        if (unavailable is null)
        {
            // Explicitly "0" rather than unset, so a caller who exported the variable to force
            // the walk still gets a confined-open job that measures the confined open.
            jobs.Add(Named(template.WithEnvironmentVariable(Openat2Probe.DisableVariableName, "0"), ConfinedOpen));
        }
        else
        {
            log($"Leaving out the {ConfinedOpen} job: the confined open is not available here ({unavailable}).");
        }

        jobs.Add(Named(template.WithEnvironmentVariable(Openat2Probe.DisableVariableName, "1"), Walk));
        return jobs;
    }

    /// <summary>
    /// Throws unless this process resolves through the backend its job was named after.
    /// </summary>
    /// <remarks>Called from every benchmark's global setup, before anything is measured.</remarks>
    public static void Verify()
    {
        string? expected = Environment.GetEnvironmentVariable(ExpectedVariable);
        if (expected is null)
        {
            // Run outside a job this program built (a debugger, a one-off BenchmarkRunner call):
            // nothing was promised about the backend, so there is nothing to hold it to.
            return;
        }

        string actual = Current();
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"This job is named for the {expected} backend but the process resolves through {actual}. " +
                "Its numbers would be published under the wrong backend, so it is stopped here.");
        }
    }

    /// <summary>The name of the backend this process resolves through.</summary>
    public static string Current() =>
        PlatformOps.Current.Capabilities.Backend switch
        {
            ResolutionBackend.ConfinedOpen => ConfinedOpen,
            ResolutionBackend.PortableWalk => Walk,
            ResolutionBackend.WindowsRelativeOpen => Windows,
            ResolutionBackend other => other.ToString(),
        };

    // The id is set last: it names the job in every table and in the regression gate's keys, and
    // is set after the environment so that nothing applied later can replace it.
    private static Job Named(Job template, string backend) =>
        template
            .WithEnvironmentVariable(ExpectedVariable, backend)
            .WithId(backend);

    private static string? ConfinedOpenUnavailableReason()
    {
        if (!OperatingSystem.IsLinux())
        {
            return "not Linux";
        }

        // Probed with the disabling variable ignored: the question is whether the kernel offers
        // the syscall, not whether this shell asked for it to be turned off.
        string? previous = Environment.GetEnvironmentVariable(Openat2Probe.DisableVariableName);
        Environment.SetEnvironmentVariable(Openat2Probe.DisableVariableName, null);
        try
        {
            var linux = new LinuxPlatformOps();
            return linux.Capabilities.SupportsConfinedOpen ? null : linux.ConfinedOpenUnavailableReason ?? "unknown";
        }
        finally
        {
            Environment.SetEnvironmentVariable(Openat2Probe.DisableVariableName, previous);
        }
    }
}
