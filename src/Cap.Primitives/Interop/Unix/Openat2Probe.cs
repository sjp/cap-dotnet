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

        if (EnvironmentSwitch.IsEnabled(DisableVariableName))
        {
            return new Openat2Probe(false, 0, $"disabled by the {DisableVariableName} environment variable");
        }

        return Attempt();
    }

    /// <summary>
    /// Issues one real confined open, of the root directory against itself, and falls back to
    /// the working directory if the root could not be asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A real call rather than a kernel version check. The version says whether the syscall
    /// was compiled in, which is not the question: the question is whether this process, in
    /// this container, under whatever filter is installed, can actually make it.
    /// </para>
    /// <para>
    /// And only that question. The open asks for a path-only descriptor, which needs no
    /// permission on the directory it names, and is anchored at the root rather than the
    /// working directory, whose permissions and very existence belong to whoever launched
    /// the process. A service started from a directory it may search but not list would
    /// otherwise have read "you may not list this" as "this kernel has no confined open",
    /// and run every resolution on the walk while the kernel was ready to do it atomically.
    /// </para>
    /// <para>
    /// Only the answers that describe the syscall itself demote: its absence, a filter's
    /// refusal, or a malformed request. Anything else is not a fact about the host that the
    /// walk is an answer to, and the process is stopped rather than quietly weakened.
    /// </para>
    /// </remarks>
    private static Openat2Probe Attempt()
    {
        int rootErrno = 0;
        int rootFd;
        unsafe
        {
            fixed (byte* root = "/\0"u8)
            {
                rootFd = LinuxNative.OpenAt(
                    LinuxConstants.AT_FDCWD, root, LinuxConstants.O_PATH | LinuxConstants.O_DIRECTORY | LinuxConstants.O_CLOEXEC);
                if (rootFd < 0)
                {
                    rootErrno = Marshal.GetLastPInvokeError();
                }
            }
        }

        int rootAnswer = 0;
        if (rootFd >= 0)
        {
            rootAnswer = ConfinedOpenErrno(rootFd);
            _ = LinuxNative.Close(rootFd);
            if (rootAnswer == 0 || IsDemotion(rootAnswer))
            {
                return Conclude(rootAnswer);
            }
        }

        // The root could not be opened, or the confined open beneath it failed for a reason
        // that says nothing about the syscall. Asked once more against the working directory
        // before giving up on an explanation.
        int cwdAnswer = ConfinedOpenErrno(LinuxConstants.AT_FDCWD);
        if (cwdAnswer == 0 || IsDemotion(cwdAnswer))
        {
            return Conclude(cwdAnswer);
        }

        string rootOutcome = rootFd < 0
            ? $"opening / failed with errno {rootErrno}"
            : $"openat2 beneath / failed with errno {rootAnswer}";
        throw new PlatformNotSupportedException(
            $"cap-dotnet could not determine whether this kernel serves a confined open: " +
            $"{rootOutcome}, and openat2 beneath the working directory failed with errno " +
            $"{cwdAnswer}. Neither is a reason the syscall is unavailable (ENOSYS, EPERM or " +
            $"EACCES from a filter, or EINVAL), so falling back to the weaker component walk " +
            $"would be a silent demotion. The process has been stopped instead.");
    }

    /// <summary>
    /// Issues the confined open of <paramref name="anchorFd"/> against itself and returns the
    /// <c>errno</c> it failed with, or zero.
    /// </summary>
    private static int ConfinedOpenErrno(int anchorFd)
    {
        OpenHow how = new()
        {
            Flags = (ulong)(uint)(LinuxConstants.O_PATH | LinuxConstants.O_DIRECTORY | LinuxConstants.O_CLOEXEC),
            Mode = 0,
            Resolve = (ulong)(ResolveFlags.Beneath | ResolveFlags.NoMagicLinks),
        };

        long result;
        int errno = 0;
        unsafe
        {
            fixed (byte* path = ".\0"u8)
            {
                result = LinuxNative.OpenAt2(LinuxConstants.SYS_openat2, anchorFd, path, &how, OpenHow.Size);
                if (result < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }

        if (result >= 0)
        {
            _ = LinuxNative.Close(checked((int)result));
        }

        return errno;
    }

    /// <summary>
    /// True for the answers that describe the syscall rather than the directory it was
    /// asked about.
    /// </summary>
    /// <remarks>
    /// Seccomp filters are written to answer with either permission code, so both are read
    /// as a filter. A path-only open of a directory needs no permission on it, so neither
    /// can come from the directory itself.
    /// </remarks>
    private static bool IsDemotion(int errno) =>
        errno is LinuxErrno.ENOSYS or PosixErrno.EPERM or PosixErrno.EACCES or PosixErrno.EINVAL;

    private static Openat2Probe Conclude(int errno) =>
        errno == 0 ? new Openat2Probe(true, 0, null) : new Openat2Probe(false, errno, DescribeFailure(errno));

    private static string DescribeFailure(int errno) => errno switch
    {
        LinuxErrno.ENOSYS =>
            "the kernel does not implement openat2; it was added in Linux 5.6",
        PosixErrno.EPERM or PosixErrno.EACCES =>
            "openat2 exists but is blocked, most likely by a seccomp filter installed by the container runtime",
        PosixErrno.EINVAL =>
            "the kernel rejected the openat2 request as malformed. This is not a property of " +
            "the host: it means the open_how structure declared by this library does not " +
            "match the one the kernel expects, and the atomic backend has been disabled by a bug",
        _ => throw new ArgumentOutOfRangeException(nameof(errno), errno, "Not an errno the probe demotes on."),
    };
}
