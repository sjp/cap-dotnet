using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;
using Cap.Std;

namespace Cap.Stress.Tests;

/// <summary>
/// Reading a directory on one thread while another disposes the reader, and dropping a reader
/// without disposing it.
/// </summary>
/// <remarks>
/// <para>
/// A directory reader holds more than a descriptor: a buffer the kernel writes entries into on
/// Linux, and on macOS a C-library stream whose descriptor it looks names up through. A
/// disposal that freed either while a read on another thread was still using it would leave
/// that read writing into memory someone else now owns, or looking a name up relative to
/// whatever had since been given the descriptor's number.
/// </para>
/// <para>
/// The race is fought at the reader rather than through <see cref="Dir.EnumerateEntries"/>,
/// because an enumerator disposed while its <c>MoveNext</c> is running on another thread does
/// nothing at all: the reader is only disposed once the enumeration yields or ends. The reader
/// is where a disposal can land part-way through a read, so that is where it is made to.
/// </para>
/// </remarks>
public sealed class DirectoryReaderRaceTests(ITestOutputHelper output)
{
    /// <summary>How many single attempts one round of opening, reading and disposing is worth.</summary>
    private const int AttemptsPerRound = 10;

    /// <summary>How many entries the directory being read holds.</summary>
    private const int Entries = 256;

    /// <summary>How many readers are dropped undisposed between collections.</summary>
    private const int AbandonedPerCollection = 100;

    /// <summary>How many collections the abandonment test runs.</summary>
    private const int Collections = 10;

    /// <summary>The name of the directory that is read, inside the sandbox and planted outside it.</summary>
    private const string ListedName = "listed";

    /// <summary>
    /// Every entry read while the reader is disposed on another thread is reported as what it
    /// is inside the sandbox, and never as what took the descriptor's number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every entry's kind is looked up rather than taken from the read, so that each one is a
    /// lookup relative to the reader's descriptor. The directory read holds only files, and a
    /// directory outside the sandbox holds a directory under each of the same names; another
    /// thread opens and closes that directory as fast as it can, so a lookup made against a
    /// recycled number finds it and reports a directory.
    /// </para>
    /// <para>
    /// The only failure a read may report is that the reader was closed. Anything else — an
    /// exception, a failure the platform reported for a stream freed under it — is a read
    /// that the disposal reached.
    /// </para>
    /// </remarks>
    [Fact]
    public void Disposing_a_directory_read_on_another_thread_never_reads_a_reissued_descriptor()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("The Linux and macOS readers are the ones that hold a buffer or a stream a disposal could free.");
        }

        using StressArena arena = new();

        string inside = arena.Inside(ListedName);
        string outside = Path.Join(arena.OutsidePath, ListedName);
        Directory.CreateDirectory(inside);
        for (int i = 0; i < Entries; i++)
        {
            string name = EntryName(i);
            File.WriteAllText(Path.Join(inside, name), "inside");
            Directory.CreateDirectory(Path.Join(outside, name));
        }

        long files = 0;
        long directories = 0;
        long unknown = 0;
        long closedMidRead = 0;
        ConcurrentQueue<string> faults = new();
        string context = "directory read while disposed";
        Descriptors descriptors = Descriptors.Before(arena.HostPath);
        int rounds = StressSettings.Rounds(AttemptsPerRound);

        bool lookedUp = AppContext.TryGetSwitch(UnixFileTypes.AlwaysLookUpKindSwitchName, out bool wasSet) && wasSet;
        AppContext.SetSwitch(UnixFileTypes.AlwaysLookUpKindSwitchName, true);
        try
        {
            using Dir listed = Dir.Open(inside, AmbientAuthority.Acquire());
            bool stop = false;

            Thread recycler = new(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    using Dir elsewhere = Dir.Open(outside, AmbientAuthority.Acquire());
                }
            });
            recycler.Start();

            for (int round = 0; round < rounds; round++)
            {
                CapResult<DirectoryReader> opened = PlatformOps.Host.OpenDirectoryReader(listed.Handle);
                Assert.True(opened.IsSuccess, $"{context}: the directory could not be opened for reading: {opened.Error.FailureDescription}");

                DirectoryReader reader = opened.Value;
                using Barrier start = new(2);

                Thread reading = new(() =>
                {
                    start.SignalAndWait();
                    bool advancedOnce = false;
                    try
                    {
                        while (true)
                        {
                            CapError error = reader.Read(out bool advanced);
                            if (error.IsFailure)
                            {
                                if (error.Category != CapErrorCategory.Closed)
                                {
                                    faults.Enqueue($"a read failed with {error.FailureDescription}");
                                }
                                else if (advancedOnce)
                                {
                                    Interlocked.Increment(ref closedMidRead);
                                }

                                return;
                            }

                            if (!advanced)
                            {
                                return;
                            }

                            advancedOnce = true;
                            switch (reader.CurrentType)
                            {
                                case CapFileType.File:
                                    Interlocked.Increment(ref files);
                                    break;
                                case CapFileType.Directory:
                                    Interlocked.Increment(ref directories);
                                    break;
                                default:
                                    Interlocked.Increment(ref unknown);
                                    break;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        // Anything thrown would end the thread and take the process with it;
                        // it is carried back to fail the test instead.
                        faults.Enqueue(e.ToString());
                    }
                });
                reading.Start();

                start.SignalAndWait(TestContext.Current.CancellationToken);

                // Long enough for the read to be part-way through the directory when the
                // disposal lands, and varied so that it lands at a different point each round.
                Thread.SpinWait(50 * (round % 200));
                reader.Dispose();
                reading.Join();
            }

            Volatile.Write(ref stop, true);
            recycler.Join();
        }
        finally
        {
            AppContext.SetSwitch(UnixFileTypes.AlwaysLookUpKindSwitchName, lookedUp);
        }

        output.WriteLine(
            $"{rounds} rounds: {files} files, {directories} directories, {unknown} of no known kind; " +
            $"the disposal landed part-way through the read in {closedMidRead}.");

        Assert.True(faults.IsEmpty, $"{context}: a read failed with something other than the reader being closed: {faults.FirstOrDefault()}");
        Assert.True(directories == 0, $"{context}: {directories} entries were reported as directories, which only a lookup through a reissued descriptor could find.");
        Assert.True(closedMidRead > 0, $"{context}: the disposal never landed part-way through a read in {rounds} rounds, so the race was never fought.");
        descriptors.AssertNoneLeaked(context);
    }

    /// <summary>
    /// A directory enumeration dropped part-way through without being disposed has what it
    /// held open closed by the finalizer.
    /// </summary>
    /// <remarks>
    /// Collected every so often rather than once at the end, so that a reader the finalizer
    /// reclaims never piles up past a low descriptor limit; one it does not reclaim runs the
    /// process out of descriptors, or is counted at the end.
    /// </remarks>
    [Fact]
    public void An_abandoned_enumeration_is_reclaimed_by_the_finalizer()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("Only the Unix descriptor count is exact enough to see a reader left open.");
        }

        using StressArena arena = new();
        File.WriteAllText(arena.Inside("a"), "a");
        File.WriteAllText(arena.Inside("b"), "b");

        Collect();
        Descriptors descriptors = Descriptors.Before(arena.HostPath);

        using (Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire()))
        {
            for (int collection = 0; collection < Collections; collection++)
            {
                for (int i = 0; i < AbandonedPerCollection; i++)
                {
                    Abandon(root);
                }

                Collect();
            }
        }

        Collect();
        descriptors.AssertNoneLeaked("abandoned enumeration");
    }

    /// <summary>Starts an enumeration, moves it on one entry and drops it.</summary>
    /// <remarks>
    /// Out of line so that nothing in the caller's frame keeps the enumerator reachable.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Abandon(Dir root)
    {
        IEnumerator<DirEntry> enumerator = root.EnumerateEntries().GetEnumerator();
        Assert.True(enumerator.MoveNext());
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string EntryName(int index) => "e" + index.ToString("D3", CultureInfo.InvariantCulture);
}
