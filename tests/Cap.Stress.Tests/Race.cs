using Cap.Primitives;
using Cap.Std;

namespace Cap.Stress.Tests;

/// <summary>
/// One operation, repeated through a sandbox handle while an attacker changes the tree beneath
/// it.
/// </summary>
internal static class Race
{
    /// <summary>
    /// Runs a race on one backend and asserts what every race must: nothing outside reached,
    /// nothing outside changed, nothing reached that cannot be vouched for, nothing left open.
    /// </summary>
    /// <param name="output">Where the counts are written.</param>
    /// <param name="race">The race's name, as the report shows it.</param>
    /// <param name="backend">The backend every handle dispatches to for the length of the race.</param>
    /// <param name="arena">The ground the race is fought over.</param>
    /// <param name="startAdversary">Starts the attack.</param>
    /// <param name="attempt">One attempt, returning the identity of what it reached.</param>
    /// <param name="classify">What an identity means for this race, once it is over.</param>
    /// <param name="attempts">How many attempts to make.</param>
    /// <param name="beforeClassifying">
    /// Looks at the counts while they are still by identity, for a race whose own checks need
    /// to know which of several acceptable objects was reached.
    /// </param>
    /// <returns>The classified counts, for the race's own further checks.</returns>
    public static Tally Fight(
        ITestOutputHelper output,
        string race,
        string backend,
        StressArena arena,
        Func<Adversary> startAdversary,
        Func<Dir, int, CapFileId> attempt,
        Func<CapFileId, Outcome> classify,
        int attempts,
        Action<Tally>? beforeClassifying = null)
    {
        string context = $"{race} on {backend}";
        string outsideBefore = arena.SnapshotOutside();
        Descriptors descriptors = Descriptors.Before(arena.HostPath);

        Tally tally = Run(backend, arena, startAdversary, attempt, attempts);
        beforeClassifying?.Invoke(tally);
        tally.Classify(classify);
        StressReport.Publish(output, race, backend, tally);

        tally.AssertContained(context);
        Assert.True(
            outsideBefore == arena.SnapshotOutside(),
            $"{context}: the directory outside the sandbox was changed. {tally}");
        descriptors.AssertNoneLeaked(context);

        return tally;
    }

    /// <summary>
    /// Runs a race on one backend and counts what each attempt came to.
    /// </summary>
    /// <param name="backend">The backend every handle dispatches to for the length of the race.</param>
    /// <param name="arena">The ground the race is fought over.</param>
    /// <param name="startAdversary">Starts the attack. Called once the sandbox handle is open.</param>
    /// <param name="attempt">
    /// One attempt: the operation under test, returning the identity of what it reached. A
    /// refusal is thrown, and is counted by its kind.
    /// </param>
    /// <param name="attempts">How many attempts to make.</param>
    /// <returns>
    /// The counts, with successes not yet classified: the caller knows what the identities mean.
    /// </returns>
    public static Tally Run(
        string backend,
        StressArena arena,
        Func<Adversary> startAdversary,
        Func<Dir, int, CapFileId> attempt,
        int attempts)
    {
        Tally tally = new();

        using BackendScope scope = Backends.Enter(backend);
        using (Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire()))
        using (Adversary adversary = startAdversary())
        {
            for (int i = 0; i < attempts; i++)
            {
                CapFileId reached;
                try
                {
                    reached = attempt(root, i);
                }
                catch (Exception e)
                {
                    tally.Refused(e);
                    continue;
                }

                tally.Reached(reached);
            }

            tally.AdversaryCycles = adversary.Stop();
        }

        scope.AssertItRan();
        return tally;
    }

    /// <summary>The identity of the file a path names, reached by opening it.</summary>
    public static CapFileId OpenFile(Dir root, string path)
    {
        using CapFile file = root.OpenFile(path);
        return file.GetMetadata().FileId;
    }

    /// <summary>The identity of the directory a path names, reached by opening it.</summary>
    public static CapFileId OpenDir(Dir root, string path)
    {
        using Dir directory = root.OpenDir(path);
        return directory.GetMetadata().FileId;
    }
}
