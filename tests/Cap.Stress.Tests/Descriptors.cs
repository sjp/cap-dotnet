using System.Diagnostics;

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
/// for. macOS and Windows offer only the process's total, which the runtime moves for its own
/// reasons, so there the total is compared against the one taken before and allowed a small
/// margin. A leak on any path a race exercises repeats on every attempt that takes it, and
/// thousands of attempts clear the margin by orders of magnitude.
/// </para>
/// </remarks>
internal sealed class Descriptors
{
    /// <summary>
    /// How far the process total may drift on the platforms where only the total is known.
    /// </summary>
    private const int Margin = 32;

    private readonly string _beneath;
    private readonly int _totalBefore;

    private Descriptors(string beneath)
    {
        _beneath = beneath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _totalBefore = OperatingSystem.IsLinux() ? 0 : Total();
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

        int after = Total();
        Assert.True(
            after - _totalBefore <= Margin,
            $"{context}: the process held {_totalBefore} descriptors or handles before the race and " +
            $"{after} after it, more than the {Margin} the runtime can account for by itself.");
    }
}
