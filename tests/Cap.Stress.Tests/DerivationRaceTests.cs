using Cap.Primitives;
using Cap.Std;

namespace Cap.Stress.Tests;

/// <summary>
/// Deriving handles from a directory handle on several threads while another thread disposes
/// it.
/// </summary>
/// <remarks>
/// <para>
/// The danger here is not a path at all. A descriptor is a small number, and the operating
/// system hands out the lowest one free: close one, open something else, and the something
/// else very likely gets the number just released. A wrapper that read the number, lost the
/// processor while another thread closed it, and then used it would resolve its path relative
/// to whatever had taken the number in between — which, if another thread had just opened a
/// directory outside the sandbox, is outside the sandbox. Nothing about the path is wrong and
/// no check on it can catch this.
/// </para>
/// <para>
/// So a third kind of thread runs alongside the derivers: one that opens and closes a directory
/// outside the sandbox as fast as it can, holding a directory of the same name as the one being
/// derived, so that a recycled number would resolve successfully and hand back a handle on the
/// wrong object. Every handle derived is identified afterwards. Each must be the directory it
/// names inside the sandbox, or the attempt must have been refused because the handle was
/// disposed; nothing else is acceptable.
/// </para>
/// </remarks>
public sealed class DerivationRaceTests(ITestOutputHelper output)
{
    /// <summary>How many single attempts one round of opening, racing and disposing is worth.</summary>
    private const int AttemptsPerRound = 10;

    /// <summary>How many threads derive from the handle at once.</summary>
    private const int Derivers = 4;

    /// <summary>The backends this host has, as the test framework's rows.</summary>
    public static TheoryData<string> OnThisHost => new(Backends.OnThisHost);

    /// <summary>
    /// Every handle derived while its parent is being disposed is the one it names inside the
    /// sandbox, and never one on an object that took the parent's number.
    /// </summary>
    [Theory]
    [MemberData(nameof(OnThisHost))]
    public void Deriving_while_the_parent_is_disposed_never_reaches_what_took_its_place(string backend)
    {
        using StressArena arena = new();

        string sub = arena.Inside(StressArena.DirectoryName);
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Join(sub, StressArena.FileName), "inside");
        HashSet<CapFileId> inside =
        [
            StressArena.IdentityOf(arena.SandboxPath),
            StressArena.IdentityOf(sub),
            StressArena.IdentityOf(Path.Join(sub, StressArena.FileName)),
        ];

        Tally tally = new();
        long disposedRefusals = 0;
        System.Collections.Concurrent.ConcurrentQueue<Exception> faults = new();
        string context = "derivation while disposed on " + backend;
        Descriptors descriptors = Descriptors.Before(arena.HostPath);
        int rounds = StressSettings.Rounds(AttemptsPerRound);

        using (BackendScope scope = Backends.Enter(backend))
        {
            for (int round = 0; round < rounds; round++)
            {
                Dir parent = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire());
                using Barrier start = new(Derivers + 2);
                bool stop = false;

                Thread[] derivers = new Thread[Derivers];
                for (int d = 0; d < Derivers; d++)
                {
                    int kind = d;
                    derivers[d] = new Thread(() =>
                    {
                        start.SignalAndWait();
                        while (true)
                        {
                            try
                            {
                                tally.Reached(Derive(parent, kind));
                            }
                            catch (ObjectDisposedException)
                            {
                                Interlocked.Increment(ref disposedRefusals);
                                return;
                            }
                            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                            {
                                tally.Refused(e);
                            }
                            catch (Exception e)
                            {
                                // Anything else would end the thread and take the process with
                                // it; it is carried back to fail the test instead.
                                faults.Enqueue(e);
                                return;
                            }
                        }
                    });
                    derivers[d].Start();
                }

                Thread recycler = new(() =>
                {
                    start.SignalAndWait();
                    while (!Volatile.Read(ref stop))
                    {
                        using Dir elsewhere = Dir.Open(arena.OutsidePath, AmbientAuthority.Acquire());
                    }
                });
                recycler.Start();

                start.SignalAndWait(TestContext.Current.CancellationToken);

                // Long enough for the derivers to be mid-call when the disposal lands, and
                // varied so that it lands at a different point in the call each round.
                Thread.SpinWait(50 * (round % 200));
                parent.Dispose();

                foreach (Thread deriver in derivers)
                {
                    deriver.Join();
                }

                Volatile.Write(ref stop, true);
                recycler.Join();
            }

            scope.AssertItRan();
        }

        Assert.True(faults.IsEmpty, $"{context}: a derivation failed with something other than a refusal: {faults.FirstOrDefault()}");

        tally.Classify(identity => inside.Contains(identity) ? Outcome.Consistent
            : arena.IsOutside(identity) ? Outcome.Escaped
            : Outcome.Unidentified);
        StressReport.Publish(output, "derivation while disposed", backend, tally);

        tally.AssertContained(context);
        descriptors.AssertNoneLeaked(context);

        // Every deriver ends by being told the handle was disposed, since that is the only
        // thing that stops it. One that ended any other way would have hung the test instead.
        Assert.Equal((long)rounds * Derivers, disposedRefusals + faults.Count);
        Assert.True(tally[Outcome.OtherRefusal] == 0, $"{context}: a derivation was refused for a reason other than disposal. {tally}");
    }

    /// <summary>One derivation, of one of the kinds a handle offers, returning what it reached.</summary>
    private static CapFileId Derive(Dir parent, int kind)
    {
        switch (kind)
        {
            case 0:
                using (Dir child = parent.OpenDir(StressArena.DirectoryName))
                {
                    return child.GetMetadata().FileId;
                }

            case 1:
                using (Dir copy = parent.Clone())
                {
                    return copy.GetMetadata().FileId;
                }

            case 2:
                using (CapFile file = parent.OpenFile(StressArena.DirectoryName + "/" + StressArena.FileName))
                {
                    return file.GetMetadata().FileId;
                }

            default:
                return parent.GetMetadata(StressArena.DirectoryName).FileId;
        }
    }
}
