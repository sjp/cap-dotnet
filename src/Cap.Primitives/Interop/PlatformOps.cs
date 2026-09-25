using Cap.Primitives.Interop.Unix;
using Cap.Primitives.Interop.Windows;

namespace Cap.Primitives.Interop;

/// <summary>
/// Holds the <see cref="IPlatformOps"/> implementation for the host: the one a handle is
/// opened through when there is no handle yet to take one from.
/// </summary>
/// <remarks>
/// <para>
/// Every operation on an existing handle goes through the implementation recorded on that
/// handle (<see cref="SafeDirHandle.Backend"/>), never through this. What is left here is
/// the first step: opening a directory by an ordinary path, finding the system's temporary
/// location, and the few other places that start from the process rather than from a
/// handle. Anything else that reads this is a bug, because it would send a handle from one
/// filesystem to another's methods. The name says "host" so that such a read stands out in
/// review, and a test scans the shipped assemblies for readers outside the entry points.
/// </para>
/// <para>
/// The slot is writable so that a test can stand a differently configured or simulated
/// implementation in for the host itself. That is a deliberate hole in an otherwise closed
/// design, and it is why the type is internal and the substitution is scoped:
/// <see cref="Substitute"/> hands back something that puts the real implementation back, so a
/// test that replaces the host cannot leak that replacement into the next one. A test that
/// only needs a simulated tree does not substitute anything; it opens its root through the
/// simulated implementation directly, and runs alongside tests using the disk.
/// </para>
/// </remarks>
internal static class PlatformOps
{
    private static IPlatformOps s_current = CreateForHostPlatform();

    // Initialised with the platform rather than on first query, so that a listener attached
    // to the meter sees the instruments as soon as anything has been resolved.
    private static readonly bool s_metricsPublished = ResolutionMetrics.Publish();

    /// <summary>The implementation for the host, for opening a first handle.</summary>
    public static IPlatformOps Host => s_current;

    /// <summary>
    /// Replaces the host implementation until the returned scope is disposed.
    /// </summary>
    /// <remarks>
    /// Process-wide, not per-thread, because the host is one thing for the whole process and
    /// a per-thread override would make two threads disagree about what it is. A test that
    /// substitutes changes what every root opened by path during its run resolves through,
    /// so it must not run in parallel with tests that open roots on the real host. Handles
    /// already open are unaffected: each keeps the implementation that issued it.
    /// </remarks>
    public static SubstitutionScope Substitute(IPlatformOps replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        IPlatformOps previous = s_current;
        s_current = replacement;
        return new SubstitutionScope(previous);
    }

    /// <summary>
    /// Builds the implementation for the running operating system.
    /// </summary>
    /// <remarks>
    /// Linux and macOS are separate implementations rather than one Unix implementation with
    /// branches inside it. They share the POSIX calls that are genuinely identical and
    /// nothing else: the confined open exists only on Linux, the stat call they use is a
    /// different syscall with a different structure, and even constants as ordinary as the
    /// flag meaning "this must be a directory" hold different values. Branching all of that
    /// inside one type would put Linux-only structure layouts in code reachable on macOS and
    /// leave every layout assertion having to say which platform it was talking about.
    /// </remarks>
    private static IPlatformOps CreateForHostPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsPlatformOps();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxPlatformOps();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new DarwinPlatformOps();
        }

        throw new PlatformNotSupportedException(
            "cap-dotnet has no filesystem backend for this operating system. Containment " +
            "depends on handle-relative syscalls that have to be implemented per platform, " +
            "so there is no portable fallback to degrade to.");
    }

    /// <summary>Restores the previous implementation when disposed.</summary>
    internal readonly struct SubstitutionScope : IDisposable
    {
        private readonly IPlatformOps _previous;

        internal SubstitutionScope(IPlatformOps previous) => _previous = previous;

        /// <summary>Puts the previous implementation back.</summary>
        public void Dispose()
        {
            if (_previous is not null)
            {
                s_current = _previous;
            }
        }
    }
}
