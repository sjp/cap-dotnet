using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;

namespace Cap.Testing;

/// <summary>
/// The resolution backends this host has, and a way to run a case on each in turn.
/// </summary>
/// <remarks>
/// <para>
/// A handle resolves through whichever backend the process chose when it started, and on Linux
/// that choice is between two genuinely different implementations: the kernel's confined open,
/// and a walk taken one name at a time. Each is what some real deployment runs — the walk is
/// what a host whose kernel is too old, or whose seccomp profile refuses the syscall, gets —
/// so a run that exercised only the one this machine happened to pick would leave the other
/// untested on every developer's machine.
/// </para>
/// <para>
/// So a suite substitutes each in turn for the host, for the length of one test. A handle
/// resolves through the backend that opened it, so what the substitution chooses is the
/// backend of every root opened by path inside the scope, and of every handle derived from
/// one. A test must therefore open its root after entering the scope. A root opened before
/// it stays on whatever the host was then. The tests themselves still go through the public
/// API; only which backend that API dispatches to is chosen here. The walk is obtained the way a user would obtain it,
/// by turning the confined open off with the documented switch, so the instance under test is
/// the shipped implementation in its shipped fallback configuration rather than a test double.
/// </para>
/// </remarks>
internal static class Backends
{
    /// <summary>The kernel's confined, atomic open. Linux only.</summary>
    public const string ConfinedOpen = "openat2";

    /// <summary>The name-at-a-time walk, as Linux runs it when the confined open is unavailable.</summary>
    public const string LinuxWalk = "linux-walk";

    /// <summary>The name-at-a-time walk, which is the only backend macOS has.</summary>
    public const string DarwinWalk = "macos-walk";

    /// <summary>The Windows backend: relative native opens against a directory handle.</summary>
    public const string Windows = "windows";

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, IPlatformOps> Built = [];

    /// <summary>Every backend this host can run.</summary>
    public static IReadOnlyList<string> OnThisHost =>
        OperatingSystem.IsLinux() ? [ConfinedOpen, LinuxWalk]
        : OperatingSystem.IsMacOS() ? [DarwinWalk]
        : OperatingSystem.IsWindows() ? [Windows]
        : [];

    /// <summary>
    /// Makes a backend the one every root opened by path resolves through, until the returned
    /// scope is disposed.
    /// </summary>
    /// <remarks>
    /// A backend the host does not offer skips the test with the reason, rather than passing
    /// it: for the confined open, a kernel or a filter that refuses the syscall, or a run told
    /// to stand it down.
    /// </remarks>
    public static BackendScope Enter(string backend)
    {
        IPlatformOps ops = Obtain(backend);

        if (backend == ConfinedOpen && !ops.Capabilities.SupportsConfinedOpen)
        {
            string reason = OperatingSystem.IsLinux() && ops is LinuxPlatformOps linux
                ? linux.ConfinedOpenUnavailableReason ?? "unknown"
                : "not Linux";
            Assert.Skip(
                $"The confined open is not available on this host ({reason}). The walk leg covers " +
                "this test here; the confined open is covered where the kernel offers it.");
        }

        return new BackendScope(backend, ops, PlatformOps.Substitute(ops));
    }

    private static IPlatformOps Obtain(string backend)
    {
        lock (Gate)
        {
            if (!Built.TryGetValue(backend, out IPlatformOps? ops))
            {
                ops = Build(backend);
                Built[backend] = ops;
            }

            return ops;
        }
    }

    private static IPlatformOps Build(string backend)
    {
        switch (backend)
        {
            case ConfinedOpen when OperatingSystem.IsLinux():
                return new LinuxPlatformOps();

            case LinuxWalk when OperatingSystem.IsLinux():
                // The probe reads the switch while the instance is being built, and never
                // again, so it is turned on for exactly that long.
                bool wasSet = AppContext.TryGetSwitch(Openat2Probe.DisableSwitchName, out bool previous);
                AppContext.SetSwitch(Openat2Probe.DisableSwitchName, true);
                try
                {
                    return new LinuxPlatformOps();
                }
                finally
                {
                    AppContext.SetSwitch(Openat2Probe.DisableSwitchName, wasSet && previous);
                }

            case DarwinWalk when OperatingSystem.IsMacOS():
            case Windows when OperatingSystem.IsWindows():
                return PlatformOps.Host;

            default:
                throw new ArgumentOutOfRangeException(nameof(backend), backend, "Not a backend this host has.");
        }
    }
}

/// <summary>
/// One backend in force, and a check on the way out that it was the one that ran.
/// </summary>
internal sealed class BackendScope : IDisposable
{
    private readonly IPlatformOps _ops;
    private readonly IDisposable _substitution;
    private readonly long _confinedOpensBefore;

    public BackendScope(string name, IPlatformOps ops, IDisposable substitution)
    {
        Name = name;
        _ops = ops;
        _substitution = substitution;
        _confinedOpensBefore = OperatingSystem.IsLinux() && ops is LinuxPlatformOps linux ? linux.ConfinedOpenAttempts : 0;
    }

    /// <summary>The backend's name.</summary>
    public string Name { get; }

    /// <summary>
    /// Asserts that the walk really was the walk.
    /// </summary>
    /// <remarks>
    /// A walk leg that quietly kept using the kernel's confined open would pass every case and
    /// test nothing the other leg did not, and nothing about the results would show it.
    /// </remarks>
    public void AssertItRan()
    {
        if (OperatingSystem.IsLinux() && Name == Backends.LinuxWalk && _ops is LinuxPlatformOps linux)
        {
            Assert.Equal(_confinedOpensBefore, linux.ConfinedOpenAttempts);
        }
    }

    public void Dispose() => _substitution.Dispose();
}
