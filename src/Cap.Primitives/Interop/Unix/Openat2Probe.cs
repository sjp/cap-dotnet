using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// The one-time answer to whether this kernel will serve a confined, atomic open.
/// </summary>
/// <remarks>
/// <para>
/// Asked once and cached for the life of the process. Asking per call would be a syscall on
/// every resolution in exactly the deployments that cannot have it: a host without the
/// syscall, or one whose sandbox filter blocks it, is the case where the extra call is pure
/// loss and where it is repeated most.
/// </para>
/// <para>
/// The probe is also an override point, because the fallback is not a legacy path. A kernel
/// new enough to have the syscall can still have it filtered away by a container runtime, so
/// a significant share of real deployments run the walk, and it has to be exercised as a
/// first-class configuration rather than as the leg nobody tests. Two switches force it, in
/// this order: the <c>Cap.Primitives.DisableOpenat2</c> application context switch, and the
/// <c>CAPDOTNET_DISABLE_OPENAT2</c> environment variable. Neither is a compile-time
/// condition, so a test run with the fallback forced is running the same binary a user
/// would ship.
/// </para>
/// <para>
/// Whichever answer comes back, the reason is kept. A silent demotion from the atomic
/// backend to the walk leaves callers with a weaker guarantee than the one they think they
/// have, so the reason has to be inspectable — particularly the difference between a kernel
/// that lacks the syscall and one that rejected the request because this library described
/// it wrongly.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal readonly struct Openat2Probe
{
    /// <summary>The application context switch that forces the fallback.</summary>
    public const string DisableSwitchName = "Cap.Primitives.DisableOpenat2";

    /// <summary>The environment variable that forces the fallback.</summary>
    public const string DisableVariableName = "CAPDOTNET_DISABLE_OPENAT2";

    private Openat2Probe(bool supported, int errno, string? reason)
    {
        Supported = supported;
        Errno = errno;
        Reason = reason;
    }

    /// <summary>True when the confined open may be used.</summary>
    public bool Supported { get; }

    /// <summary>The <c>errno</c> the probe saw, or zero.</summary>
    public int Errno { get; }

    /// <summary>Why the confined open is unavailable, or <see langword="null"/> when it is.</summary>
    public string? Reason { get; }

    /// <summary>Runs the probe.</summary>
    public static Openat2Probe Run()
    {
        if (AppContext.TryGetSwitch(DisableSwitchName, out bool disabled) && disabled)
        {
            return new Openat2Probe(false, 0, $"disabled by the {DisableSwitchName} application context switch");
        }

        string? variable = Environment.GetEnvironmentVariable(DisableVariableName);
        if (variable is "1" or "true" or "TRUE")
        {
            return new Openat2Probe(false, 0, $"disabled by the {DisableVariableName} environment variable");
        }

        return Attempt();
    }

    /// <summary>
    /// Issues one real confined open, of the working directory against itself.
    /// </summary>
    /// <remarks>
    /// A real call rather than a kernel version check. The version says whether the syscall
    /// was compiled in, which is not the question: the question is whether this process, in
    /// this container, under whatever filter is installed, can actually make it.
    /// </remarks>
    private static Openat2Probe Attempt()
    {
        ReadOnlySpan<byte> self = ".\0"u8;

        OpenHow how = new()
        {
            Flags = (ulong)(uint)(LinuxConstants.O_RDONLY | LinuxConstants.O_DIRECTORY | LinuxConstants.O_CLOEXEC),
            Mode = 0,
            Resolve = (ulong)(ResolveFlags.Beneath | ResolveFlags.NoMagicLinks),
        };

        long result;
        int errno = 0;
        unsafe
        {
            fixed (byte* path = self)
            {
                result = LinuxNative.OpenAt2(
                    LinuxConstants.SYS_openat2, LinuxConstants.AT_FDCWD, path, &how, OpenHow.Size);
                if (result < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }

        if (result >= 0)
        {
            using SafeDirHandle probeHandle = new((nint)result, ownsHandle: true);
            return new Openat2Probe(true, 0, null);
        }

        return new Openat2Probe(false, errno, DescribeFailure(errno));
    }

    private static string DescribeFailure(int errno) => errno switch
    {
        LinuxErrno.ENOSYS =>
            "the kernel does not implement openat2; it was added in Linux 5.6",
        PosixErrno.EPERM =>
            "openat2 exists but is blocked, most likely by a seccomp filter installed by the container runtime",
        PosixErrno.EINVAL =>
            "the kernel rejected the openat2 request as malformed. This is not a property of " +
            "the host: it means the open_how structure declared by this library does not " +
            "match the one the kernel expects, and the atomic backend has been disabled by a bug",
        _ => $"openat2 failed with errno {errno}",
    };
}
