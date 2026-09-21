using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;

namespace Cap.Primitives.Tests;

/// <summary>
/// That the confined open is one operation, not a walk wearing its name.
/// </summary>
/// <remarks>
/// <para>
/// The atomicity is the entire reason this backend is preferred, and atomicity is a property
/// of how the work is done rather than of what comes back. Resolution that opened each name
/// in turn would produce exactly the same handle for exactly the same paths, pass every test
/// written about results, and leave a window between every pair of components for a
/// directory to be swapped out from under it.
/// </para>
/// <para>
/// So it is counted two ways. From inside, by the opens the implementation itself issues: a
/// path of several names must cost one confined operation and no per-name opens at all.
/// From outside, by watching the syscalls the process actually makes, which is the only
/// check that cannot be fooled by a counter that is simply wrong. The second needs
/// permission to trace a running process, which many containers refuse, so it says when it
/// could not look rather than passing quietly.
/// </para>
/// </remarks>
[Collection(PlatformOpsTestGroup.Name)]
public sealed partial class ConfinedOpenSyscallTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cap-syscalls-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// However many names the path has, the implementation issues one confined open and
    /// walks nothing.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    public void A_confined_open_resolves_a_whole_path_in_one_operation()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        LinuxPlatformOps ops = new();
        if (!ops.Capabilities.SupportsConfinedOpen)
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(_root, "a", "b", "c", "d"));

        using SafeDirHandle root = OpenRoot(ops);
        long confinedBefore = ops.ConfinedOpenAttempts;
        long componentsBefore = ops.ComponentOpens;

        CapResult<SafeDirHandle> result = ops.OpenConfinedDirectory(
            root, "a/b/c/d", CapAccess.Read, ConfinedResolveOptions.None);
        Assert.True(result.IsSuccess, result.Error.FailureDescription);
        result.Value.Dispose();

        Assert.Equal(1, ops.ConfinedOpenAttempts - confinedBefore);
        Assert.Equal(0, ops.ComponentOpens - componentsBefore);
    }

    /// <summary>
    /// And the syscalls the process really makes agree: one, naming the whole path.
    /// </summary>
    /// <remarks>
    /// Watched from outside because the counter above is this library's own account of
    /// itself. A tracer is the only witness that does not share the assumptions being
    /// checked — and it is also how a change that reintroduced a per-name open, in a place
    /// nobody thought to count, would be caught.
    /// </remarks>
    [Fact]
    [SupportedOSPlatform("linux")]
    public void A_confined_open_issues_exactly_one_syscall()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        LinuxPlatformOps ops = new();
        if (!ops.Capabilities.SupportsConfinedOpen)
        {
            return;
        }

        if (!TryFindTracer(out string tracer))
        {
            Assert.Skip("No syscall tracer on this host, so the syscalls cannot be watched from outside.");
        }

        string marker = "trace-" + Guid.NewGuid().ToString("N");
        string canary = "canary-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(_root, marker, "inner"));

        using SafeDirHandle root = OpenRoot(ops);
        string log = Path.Combine(_root, "trace.log");

        using Process trace = StartTracer(tracer, log);
        try
        {
            // Attaching is not instant, and a trace that began after the interesting call
            // would report nothing and look exactly like a call that was never made. So the
            // process makes a deliberately recognisable one until it shows up in the log.
            if (!WaitForTracing(ops, root, canary, log))
            {
                Assert.Skip(
                    "The tracer attached but reported no syscalls, which this host does not " +
                    "permit. Whether the confined open is a single syscall is unverified here.");
            }

            CapResult<SafeDirHandle> result = ops.OpenConfinedDirectory(
                root, marker + "/inner", CapAccess.Read, ConfinedResolveOptions.None);
            Assert.True(result.IsSuccess, result.Error.FailureDescription);
            result.Value.Dispose();
        }
        finally
        {
            StopTracer(trace);
        }

        string[] observed = File.ReadAllLines(log)
            .Where(line => line.Contains(marker, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            observed.Length == 1,
            "Resolving one path named " + observed.Length + " times in the trace. A confined " +
            "open resolves the whole path in a single kernel operation; more than one call " +
            "means something walked it. Lines:\n" + string.Join('\n', observed));

        Assert.Contains("openat2(", observed[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs a tracer over this process, recording only the calls that open something.
    /// </summary>
    private static Process StartTracer(string tracer, string log)
    {
        ProcessStartInfo start = new()
        {
            FileName = tracer,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in new[]
                 {
                     "-f",
                     "-e", "trace=openat,openat2",
                     "-p", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                     "-o", log,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ??
            throw new InvalidOperationException("The tracer did not start.");
    }

    /// <summary>
    /// Makes an easily recognised open until it appears in the log, so that what follows is
    /// known to be watched.
    /// </summary>
    /// <returns>False when the tracer never reports anything and cannot be relied on.</returns>
    [SupportedOSPlatform("linux")]
    private static bool WaitForTracing(LinuxPlatformOps ops, SafeDirHandle root, string canary, string log)
    {
        for (int attempt = 0; attempt < TracerAttachAttempts; attempt++)
        {
            // Expected to fail: the name does not exist. The syscall is made either way, and
            // that is the whole of what is being waited for.
            _ = ops.OpenChildDirectory(root, canary, CapAccess.Read);

            if (File.Exists(log) &&
                File.ReadAllText(log).Contains(canary, StringComparison.Ordinal))
            {
                return true;
            }

            Thread.Sleep(TracerPollInterval);
        }

        return false;
    }

    /// <summary>
    /// Ends the trace and waits for the log to be written out.
    /// </summary>
    /// <remarks>
    /// An interrupt rather than a kill: the tracer detaches and flushes what it has buffered
    /// only when it is allowed to shut down, and a log truncated mid-write would fail the
    /// assertions for a reason that has nothing to do with the code under test.
    /// </remarks>
    private static void StopTracer(Process trace)
    {
        if (!trace.HasExited)
        {
            // Interrupting rather than killing, so the trace is flushed. A tracer that
            // ignores it is killed afterwards rather than being waited on forever.
            _ = Kill(trace.Id, Interrupt);
            if (!trace.WaitForExit(TracerShutdownMilliseconds))
            {
                trace.Kill();
            }
        }

        trace.WaitForExit();
    }

    private static bool TryFindTracer(out string path)
    {
        foreach (string candidate in new[] { "/usr/bin/strace", "/bin/strace", "/usr/sbin/strace" })
        {
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    [SupportedOSPlatform("linux")]
    private SafeDirHandle OpenRoot(LinuxPlatformOps ops)
    {
        CapResult<SafeDirHandle> result = ops.OpenAmbientDirectory(_root, CapAccess.Read);
        Assert.True(result.IsSuccess, result.Error.FailureDescription);
        return result.Value;
    }

    private const int TracerAttachAttempts = 100;
    private const int TracerPollInterval = 50;
    private const int TracerShutdownMilliseconds = 5_000;

    /// <summary>The signal a tracer treats as "detach and write out what you have".</summary>
    private const int Interrupt = 2;

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}
