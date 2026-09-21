using Cap.Primitives.Interop.Unix;
using Cap.Primitives.Interop.Windows;

namespace Cap.Primitives.Interop;

/// <summary>
/// Holds the <see cref="IPlatformOps"/> implementation the rest of the library calls.
/// </summary>
/// <remarks>
/// <para>
/// A single mutable slot rather than a constructor parameter threaded through every type.
/// The resolver is called from static helpers and from struct methods that cannot carry an
/// extra field without changing their size, and an interface call per syscall is not
/// measurable next to the syscall itself.
/// </para>
/// <para>
/// The slot is writable so that a test can substitute a simulated filesystem. That is a
/// deliberate hole in an otherwise closed design, and it is why the type is internal and the
/// substitution is scoped: <see cref="Substitute"/> hands back something that puts the real
/// implementation back, so a test that replaces the platform cannot leak that replacement
/// into the next one.
/// </para>
/// </remarks>
internal static class PlatformOps
{
    private static IPlatformOps s_current = CreateForHostPlatform();

    /// <summary>The implementation in use.</summary>
    public static IPlatformOps Current => s_current;

    /// <summary>
    /// Replaces the implementation until the returned scope is disposed.
    /// </summary>
    /// <remarks>
    /// Process-wide, not per-thread, because the implementation it replaces is process-wide
    /// too and a per-thread override would make the two disagree about which backend is
    /// active. Tests that substitute must therefore not run in parallel with tests that use
    /// the real platform.
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
