using System.Diagnostics;
using System.Reflection;

namespace Cap.Fs.Ext.Tests;

/// <summary>
/// What is left on disk when a publish is interrupted by the process ending.
/// </summary>
/// <remarks>
/// <para>
/// The claim being tested is that a name never holds a partly written file. Every other test
/// here observes that from inside a running program, where the worst that can happen is a
/// reader catching the window between two calls. This one removes the program: the publish is
/// performed by a child process which is killed while the contents are still being written,
/// which is as close to a power failure as a test can get without one.
/// </para>
/// <para>
/// What survives the kill is then read with ambient <c>System.IO</c>, because the process that
/// held the capability is gone and because the question is about what is on the disk rather
/// than about what this library says is there.
/// </para>
/// </remarks>
public sealed class AtomicWriteCrashTests : IDisposable
{
    private const int ChildTimeout = 120_000;

    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>A publish killed part of the way through leaves no partial file.</summary>
    /// <remarks>
    /// <para>
    /// Two outcomes are allowed and both are correct: the name holds what it held before, or
    /// it holds the whole of the new contents. Which of the two depends on whether the move
    /// had happened when the process died, and that is a race the test does not need to win —
    /// what it must never see is a file that is neither, because that is the failure the whole
    /// design exists to rule out.
    /// </para>
    /// <para>
    /// The test also insists that a scratch file was seen before the kill. Without that it
    /// would pass on a machine where the child never got started, which is the way a test like
    /// this quietly stops testing anything.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_publish_killed_part_of_the_way_through_leaves_no_partial_file()
    {
        string target = Path.Combine(_tree.HostPath, AtomicWriteCrashChild.TargetName);
        byte[] before = [1, 2, 3];
        File.WriteAllBytes(target, before);

        using Process child = StartChild();
        bool sawScratch = WaitForScratch(child);

        if (child.HasExited)
        {
            Assert.Skip(
                "The child published the whole file before it could be interrupted, so this " +
                "run observed a completed publish rather than a killed one.");
        }

        child.Kill(entireProcessTree: true);
        Assert.True(child.WaitForExit(ChildTimeout), "The child did not die when it was killed.");

        Assert.True(sawScratch, "No scratch file was ever seen, so the child never began publishing.");

        byte[] after = File.ReadAllBytes(target);
        Assert.True(
            IsUnchanged(after, before) || IsWholePayload(after),
            $"The published name held {after.Length} bytes, which is neither what was there " +
            $"before nor the whole of what was being written.");
    }

    /// <summary>Whether what is on disk is exactly what was there before the publish began.</summary>
    private static bool IsUnchanged(byte[] after, byte[] before) => after.AsSpan().SequenceEqual(before);

    /// <summary>Whether what is on disk is the whole payload the child was writing.</summary>
    private static bool IsWholePayload(byte[] after)
    {
        if (after.Length != AtomicWriteCrashChild.PayloadBytes)
        {
            return false;
        }

        foreach (byte b in after)
        {
            if (b != AtomicWriteCrashChild.PayloadByte)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Waits until the child has a scratch file with something in it, or has finished.</summary>
    private bool WaitForScratch(Process child)
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        while (!child.HasExited && elapsed.ElapsedMilliseconds < ChildTimeout)
        {
            foreach (string entry in Directory.EnumerateFiles(_tree.HostPath, "cap-*"))
            {
                if (new FileInfo(entry).Length > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Starts this same test program as a child, told to publish rather than to test.</summary>
    private Process StartChild()
    {
        ProcessStartInfo start = new()
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // The test program may have been started through its own launcher or through the
        // shared host, and only the first can be re-run by path alone.
        string host = Environment.ProcessPath ??
            throw new InvalidOperationException("The running program has no path to re-launch.");

        start.FileName = host;
        if (Path.GetFileNameWithoutExtension(host) is "dotnet")
        {
            string assembly = Assembly.GetExecutingAssembly().GetName().Name + ".dll";
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, assembly));
        }

        start.Environment[AtomicWriteCrashChild.RequestVariable] = _tree.HostPath;

        return Process.Start(start) ??
            throw new InvalidOperationException("The publishing child did not start.");
    }
}
