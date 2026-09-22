using System.Runtime.CompilerServices;
using Cap.Primitives;
using Cap.Std;

namespace Cap.Fs.Ext.Tests;

/// <summary>
/// Publishes one large file and is killed part of the way through.
/// </summary>
/// <remarks>
/// <para>
/// Half of a test, in a process of its own because the other half is a kill. There is no way
/// to stop a thread between two syscalls from inside the same process — and if there were, the
/// thing being tested would be an interruption this library had agreed to rather than the
/// machine going away. So the publish happens in a second copy of this program, and the parent
/// ends it without warning.
/// </para>
/// <para>
/// It answers before the test platform starts, for the same reason the process exists: it is
/// not running a suite, it is running one operation that is not expected to finish.
/// </para>
/// </remarks>
internal static class AtomicWriteCrashChild
{
    /// <summary>Set to the directory to publish into, which makes this process the child.</summary>
    public const string RequestVariable = "CAPDOTNET_TEST_CRASH_CHILD";

    /// <summary>The name the child publishes.</summary>
    public const string TargetName = "report";

    /// <summary>The byte the child's payload is made of.</summary>
    public const byte PayloadByte = 0xA5;

    /// <summary>
    /// How large the payload is.
    /// </summary>
    /// <remarks>
    /// Large enough that writing it and committing it take long enough for the parent to
    /// notice the scratch file and end the process while the write is still going on. Small
    /// enough to be written to a build agent's disk without thinking about it.
    /// </remarks>
    public const int PayloadBytes = 64 * 1024 * 1024;

    [ModuleInitializer]
    internal static void PublishIfRequested()
    {
        string? directory = Environment.GetEnvironmentVariable(RequestVariable);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        byte[] payload = new byte[PayloadBytes];
        Array.Fill(payload, PayloadByte);

        using (Dir dir = Dir.Open(directory, AmbientAuthority.Acquire()))
        {
            dir.WriteAllBytesAtomic(TargetName, payload, Durability.FileAndDirectory);
        }

        // Only reached when the parent was too slow to interrupt it. The parent is told, so
        // that a run which proved nothing is not counted as a run which proved something.
        Console.Out.WriteLine("published");
        Console.Out.Flush();
        Environment.Exit(0);
    }
}
