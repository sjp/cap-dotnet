using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cap.Tests;

/// <summary>
/// Takes a syscall away from the running process, so that code which has to cope without it
/// can be run on a machine that has it.
/// </summary>
/// <remarks>
/// <para>
/// The fallback resolver is not a legacy path. A kernel new enough to offer the confined
/// open can still have it filtered away by whatever installed the sandbox the process is
/// running in, and for a long stretch after the syscall was introduced the default profile
/// of a widely used container runtime did exactly that. So the case where the syscall is
/// refused is an ordinary deployment, and a suite that can only reach it by being run on an
/// old kernel is a suite that never reaches it.
/// </para>
/// <para>
/// Asking this library to stand down is not the same test. That path is deliberate and it is
/// already covered; what it cannot cover is the capability probe itself — the code that
/// decides, from what the kernel actually answers, that the fast path is unavailable. A
/// filter is the only way to make the kernel give that answer on a host where the syscall
/// works.
/// </para>
/// <para>
/// A filter cannot be lifted once installed, which is deliberate in the kernel and awkward
/// here: it applies to the whole process for the rest of its life. So a test that wants one
/// runs a child process rather than filtering itself, and only a run deliberately configured
/// to exercise the fallback filters the process it is already in.
/// </para>
/// </remarks>
internal static unsafe partial class SeccompFilter
{
    /// <summary>
    /// Set to <c>EPERM</c> or <c>ENOSYS</c> to deny the confined open for the whole process
    /// before any test runs.
    /// </summary>
    /// <remarks>
    /// The two answers are not interchangeable. A kernel without the syscall reports that it
    /// is not implemented; a filter that refuses one the kernel has reports a permission
    /// failure. Both mean "unavailable" and both must be recognised, and a probe that
    /// handled only the first would leave every filtered deployment taking an unexplained
    /// error instead of the fallback.
    /// </remarks>
    public const string DenyOpenat2Variable = "CAPDOTNET_TEST_DENY_OPENAT2";

    private const int PR_SET_NO_NEW_PRIVS = 38;
    private const int SECCOMP_SET_MODE_FILTER = 1;

    private const uint SECCOMP_RET_ERRNO = 0x00050000;
    private const uint SECCOMP_RET_ALLOW = 0x7FFF0000;

    /// <summary>Load a 32-bit word from a fixed offset in the syscall description.</summary>
    private const ushort BPF_LD_W_ABS = 0x20;

    /// <summary>Compare the loaded word against a constant.</summary>
    private const ushort BPF_JEQ_K = 0x15;

    /// <summary>Return a verdict.</summary>
    private const ushort BPF_RET_K = 0x06;

    /// <summary>Offset of the syscall number within the structure the filter is shown.</summary>
    private const uint SyscallNumberOffset = 0;

    /// <summary>Offset of the architecture token within that same structure.</summary>
    private const uint ArchitectureOffset = 4;

    /// <summary><c>openat2</c>, which carries the same number on both architectures here.</summary>
    private const uint Openat2 = 437;

    /// <summary>
    /// Installs the filter the environment asked for, if it asked for one.
    /// </summary>
    /// <remarks>
    /// Runs before the first test, because the capability probe answers once and caches the
    /// answer for the life of the process. A filter installed after the first resolution
    /// would change what the kernel does without changing what this library believes, which
    /// is the one configuration that tests nothing and looks like it tests something.
    /// </remarks>
    [ModuleInitializer]
    internal static void ApplyRequestedDenial()
    {
        string? requested = Environment.GetEnvironmentVariable(DenyOpenat2Variable);
        if (string.IsNullOrEmpty(requested))
        {
            return;
        }

        if (!TryParseErrno(requested, out int errno))
        {
            throw new InvalidOperationException(
                $"{DenyOpenat2Variable} was set to '{requested}', which is not an error this " +
                "harness can make the kernel report. Use EPERM or ENOSYS.");
        }

        if (!TryDenyOpenat2(errno))
        {
            throw new InvalidOperationException(
                $"{DenyOpenat2Variable} asked for the confined open to be denied, and the " +
                "filter could not be installed. Continuing would run the suite on the fast " +
                "path while reporting that it had exercised the fallback.");
        }
    }

    /// <summary>
    /// Makes <c>openat2</c> fail with <paramref name="errno"/> for this process and every
    /// process it starts, permanently.
    /// </summary>
    /// <returns>False when this host cannot install the filter at all.</returns>
    public static bool TryDenyOpenat2(int errno)
    {
        if (!OperatingSystem.IsLinux() || !TryGetArchitectureToken(out uint architecture))
        {
            return false;
        }

        return Install(architecture, errno);
    }

    /// <summary>Translates the name of an error into the number the kernel reports.</summary>
    public static bool TryParseErrno(string name, out int errno)
    {
        switch (name)
        {
            case "EPERM":
                errno = 1;
                return true;

            case "ENOSYS":
                errno = 38;
                return true;

            default:
                errno = 0;
                return false;
        }
    }

    [SupportedOSPlatform("linux")]
    private static bool Install(uint architecture, int errno)
    {
        // A filter may only be installed by a process that has given up the ability to gain
        // privilege through an exec. That is the kernel's protection against a filter being
        // used to lie to a program that is about to become more privileged than the one that
        // installed it, and it is a one-way door for this process.
        if (Prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0)
        {
            return false;
        }

        // Every filter has to check which architecture the call arrived on before it looks at
        // the number, because syscall numbers mean different things per architecture and a
        // process can be asked to run code for more than one of them. A filter that skipped
        // the check would be denying whatever happened to share the number elsewhere.
        SockFilter* program = stackalloc SockFilter[6];
        program[0] = new SockFilter(BPF_LD_W_ABS, 0, 0, ArchitectureOffset);
        program[1] = new SockFilter(BPF_JEQ_K, jt: 0, jf: 3, architecture);
        program[2] = new SockFilter(BPF_LD_W_ABS, 0, 0, SyscallNumberOffset);
        program[3] = new SockFilter(BPF_JEQ_K, jt: 0, jf: 1, Openat2);
        program[4] = new SockFilter(BPF_RET_K, 0, 0, SECCOMP_RET_ERRNO | ((uint)errno & 0xFFFF));
        program[5] = new SockFilter(BPF_RET_K, 0, 0, SECCOMP_RET_ALLOW);

        SockFprog description = new() { Length = 6, Filter = program };
        return Syscall(SeccompSyscallNumber, SECCOMP_SET_MODE_FILTER, 0, &description) == 0;
    }

    /// <summary>The number of the syscall that installs a filter, which differs per architecture.</summary>
    private static long SeccompSyscallNumber =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 277 : 317;

    /// <summary>
    /// The token the kernel stamps on a syscall to say which architecture's numbering it
    /// used.
    /// </summary>
    private static bool TryGetArchitectureToken(out uint token)
    {
        switch (RuntimeInformation.ProcessArchitecture)
        {
            case Architecture.X64:
                token = 0xC000003E;
                return true;

            case Architecture.Arm64:
                token = 0xC00000B7;
                return true;

            default:
                token = 0;
                return false;
        }
    }

    /// <summary>One instruction of the filter program.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SockFilter
    {
        public SockFilter(ushort code, byte jt, byte jf, uint k)
        {
            Code = code;
            Jt = jt;
            Jf = jf;
            K = k;
        }

        public ushort Code;

        /// <summary>Instructions to skip when the comparison holds.</summary>
        public byte Jt;

        /// <summary>Instructions to skip when it does not.</summary>
        public byte Jf;

        public uint K;
    }

    /// <summary>The program and its length, as the kernel is handed them.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SockFprog
    {
        public ushort Length;
        public SockFilter* Filter;
    }

    [LibraryImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static partial int Prctl(int option, ulong a, ulong b, ulong c, ulong d);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long Syscall(long number, uint operation, uint flags, SockFprog* description);
}
