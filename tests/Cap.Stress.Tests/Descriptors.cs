using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cap.Stress.Tests;

/// <summary>
/// A count of the descriptors or handles the process holds, taken before a race and checked
/// after it.
/// </summary>
/// <remarks>
/// <para>
/// Every exit a lost race can take is an exit that has to close what it opened, and the lost
/// races are the exits no ordinary test reaches. So each race ends by asking the operating
/// system, rather than the library's own bookkeeping, whether anything was left open.
/// </para>
/// <para>
/// On Linux the question can be asked exactly: the kernel names what each descriptor refers to,
/// so the count is of descriptors on anything beneath the directory the race was fought in, and
/// it must be zero. Nothing else in the process holds one there, so there is no noise to allow
/// for. macOS and Windows offer only a count for the whole process, which the runtime moves for
/// its own reasons, so there the count is compared against the one taken before and allowed a
/// small margin. A leak on any path a race exercises repeats on every attempt that takes it,
/// and thousands of attempts clear the margin by orders of magnitude.
/// </para>
/// <para>
/// On Windows the count is of handles on files and directories alone, not of every handle.
/// The process total there includes a handle for each thread and several events the runtime
/// keeps for it, and those outlive the thread until the collector reclaims the thread's
/// object. A race that starts fresh threads every round therefore grows the total by a few
/// handles a round with nothing leaked at all, and over a thousand rounds that is thousands of
/// handles. Only a file or directory handle can be something the library failed to close.
/// </para>
/// </remarks>
internal sealed partial class Descriptors
{
    /// <summary>
    /// How far the process total may drift on the platforms where only the total is known.
    /// </summary>
    private const int Margin = 32;

    private readonly string _beneath;
    private readonly int _heldBefore;

    private Descriptors(string beneath)
    {
        _beneath = beneath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _heldBefore = OperatingSystem.IsLinux() ? 0 : HeldOnFiles();
    }

    /// <summary>Starts counting, for a race fought beneath <paramref name="directory"/>.</summary>
    public static Descriptors Before(string directory) => new(directory);

    /// <summary>How many descriptors this process holds, as the kernel lists them.</summary>
    /// <remarks>
    /// Not usable while the process is out of descriptors: listing them takes one.
    /// </remarks>
    public static int Total()
    {
        if (OperatingSystem.IsLinux())
        {
            return Directory.GetFileSystemEntries("/proc/self/fd").Length;
        }

        if (OperatingSystem.IsMacOS())
        {
            return Directory.GetFileSystemEntries("/dev/fd").Length;
        }

        using Process self = Process.GetCurrentProcess();
        return self.HandleCount;
    }

    /// <summary>
    /// The descriptors this process holds on anything beneath a directory, by what the kernel
    /// says each refers to. Linux only.
    /// </summary>
    public static IReadOnlyList<string> HeldBeneath(string directory)
    {
        List<string> held = [];
        foreach (string descriptor in Directory.GetFileSystemEntries("/proc/self/fd"))
        {
            string? target;
            try
            {
                target = new FileInfo(descriptor).LinkTarget;
            }
            catch (IOException)
            {
                // Closed between the listing and the read, which is the listing's own
                // descriptor more often than not.
                continue;
            }

            if (target is not null && target.StartsWith(directory, StringComparison.Ordinal))
            {
                held.Add($"{Path.GetFileName(descriptor)} -> {target}");
            }
        }

        return held;
    }

    /// <summary>Asserts that nothing the race opened is still open.</summary>
    /// <remarks>
    /// Finalisers are deliberately not run first. A handle that was dropped without being
    /// disposed is closed eventually by its finaliser, and a leak that relies on that is still a
    /// leak: under load the finaliser runs long after the descriptor limit was reached. Counting
    /// before the collector has had a chance is what makes such a handle show.
    /// </remarks>
    public void AssertNoneLeaked(string context)
    {
        if (OperatingSystem.IsLinux())
        {
            IReadOnlyList<string> held = HeldBeneath(_beneath);
            Assert.True(
                held.Count == 0,
                $"{context}: {held.Count} descriptor(s) were still open on the race's tree after it ended: " +
                string.Join(", ", held.Take(10)));
            return;
        }

        int after = HeldOnFiles();
        Assert.True(
            after - _heldBefore <= Margin,
            $"{context}: the process held {_heldBefore} descriptors or handles on files before the race and " +
            $"{after} after it, more than the {Margin} the runtime can account for by itself.");
    }

    /// <summary>
    /// How many descriptors or handles this process holds that could be on a file or directory.
    /// </summary>
    /// <remarks>
    /// Every descriptor on macOS, since threads and events there are not descriptors; on
    /// Windows, the handles whose object is of the kind files and directories are.
    /// </remarks>
    private static int HeldOnFiles() => OperatingSystem.IsWindows() ? FileHandles() : Total();

    /// <summary>
    /// How many handles this process holds on objects of the kind files and directories are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The system lists a process's handles with a number for each one's kind of object, and
    /// the numbers are assigned at boot rather than fixed, so the one for files is learned from
    /// a handle known to be on a file: the running program's own, opened for the purpose. Nothing is
    /// asked of any other handle, only its kind read off the list, so a handle the runtime is
    /// blocked on cannot hold this up.
    /// </para>
    /// <para>
    /// The kind covers everything the file system driver stack opens, pipes and the console
    /// included, and those the runtime holds are few and settled by the time a race starts.
    /// </para>
    /// </remarks>
    private static int FileHandles()
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle known = File.OpenHandle(
            Environment.ProcessPath ?? throw new InvalidOperationException("The running program has no file to open."), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        byte[] snapshot = HandleSnapshot();
        int entrySize = (3 * IntPtr.Size) + (4 * sizeof(uint));
        int typeOffset = (3 * IntPtr.Size) + sizeof(uint);
        int count = (int)ReadPointer(snapshot, 0);
        nint knownValue = known.DangerousGetHandle();

        uint? fileType = null;
        for (int i = 0; i < count; i++)
        {
            int entry = (2 * IntPtr.Size) + (i * entrySize);
            if ((nint)ReadPointer(snapshot, entry) == knownValue)
            {
                fileType = BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(entry + typeOffset));
                break;
            }
        }

        if (fileType is null)
        {
            throw new InvalidOperationException("A handle this process had just opened was missing from its own handle list.");
        }

        int files = 0;
        for (int i = 0; i < count; i++)
        {
            int entry = (2 * IntPtr.Size) + (i * entrySize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(entry + typeOffset)) == fileType)
            {
                files++;
            }
        }

        // Less the one opened here to learn the kind, which is closed before the count is used.
        return files - 1;
    }

    /// <summary>
    /// The system's list of this process's handles: a count, then an entry for each giving its
    /// value and the kind of object it is on.
    /// </summary>
    private static byte[] HandleSnapshot()
    {
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int status = NtQueryInformationProcess(CurrentProcess, ProcessHandleInformation, buffer, buffer.Length, out int needed);
            if (status == StatusInfoLengthMismatch)
            {
                // Handles opened between the two calls would make the stated size short again,
                // so the retry allows for some.
                buffer = new byte[Math.Max(needed, buffer.Length) + (16 * 1024)];
                continue;
            }

            if (status < 0)
            {
                throw new InvalidOperationException($"The system would not list this process's handles (status 0x{status:X8}).");
            }

            return buffer;
        }
    }

    private static ulong ReadPointer(byte[] buffer, int offset) => IntPtr.Size == 8
        ? BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset))
        : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));

    /// <summary>The value that stands for the calling process wherever a process handle is asked for.</summary>
    private const nint CurrentProcess = -1;

    /// <summary>The query that lists a process's handles and the kind of object each is on.</summary>
    private const int ProcessHandleInformation = 51;

    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        nint process, int informationClass, byte[] information, int length, out int returnLength);
}
