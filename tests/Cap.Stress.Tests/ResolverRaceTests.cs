using System.Collections.Concurrent;
using System.Text;
using Cap.Primitives;
using Cap.Std;

namespace Cap.Stress.Tests;

/// <summary>
/// Resolution through a tree that an attacker is changing while it runs.
/// </summary>
/// <remarks>
/// <para>
/// The escape corpus holds the tree still and asks whether each hostile shape is refused. These
/// races hold nothing still: an attacker with write access inside the sandbox changes the tree
/// in a tight loop on another thread, and a path is resolved through it over and over. Each
/// attempt either fails or reaches an object, and every object reached is identified afterwards
/// by the volume and file number the operating system gives it, and compared against the
/// objects the race put inside the sandbox and the ones it planted outside. No race here passes
/// by not throwing.
/// </para>
/// <para>
/// Every race runs on every backend this host has. On the kernel's confined open the resolution
/// is one system call and the kernel keeps it beneath the root; on the name-at-a-time walk the
/// tree can change between two of its steps, and each step is only safe because it is taken
/// relative to a handle the walk already holds. Both must never reach outside. What differs is
/// how often a resolution is steered to a different object inside, and the races count that.
/// </para>
/// </remarks>
public sealed class ResolverRaceTests(ITestOutputHelper output)
{
    /// <summary>The backends this host has, as the test framework's rows.</summary>
    public static TheoryData<string> OnThisHost => new(Backends.OnThisHost);

    /// <summary>
    /// A directory swapped back and forth with a link pointing outside, in a tight loop, is
    /// never followed out — as the last component of a path or in the middle of one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The classic race against a resolver that checks a name and then uses it: check that
    /// <c>slot</c> is a directory, lose the processor, and open <c>slot/f</c> after it has become
    /// a link to somewhere else. Two links take turns, one with an absolute target and one
    /// climbing out with a parent step, because the two are refused by different rules.
    /// </para>
    /// <para>
    /// The race counts as fought only if some attempt met the link and was refused for it. A run
    /// where every attempt found the directory has shown nothing about the swap.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(OnThisHost))]
    public void A_directory_swapped_for_a_link_out_is_never_followed_out(string backend)
    {
        using StressArena arena = new();
        LinkSwap swap = LinkSwap.Stage(arena);

        swap.Fight(
            output,
            "directory swapped for a link out",
            backend,
            arena,
            () => new ThreadAdversary(() =>
            {
                HostOps.Exchange(swap.Slot, swap.AbsoluteLink);
                HostOps.Exchange(swap.Slot, swap.AbsoluteLink);
                HostOps.Exchange(swap.Slot, swap.ClimbingLink);
                HostOps.Exchange(swap.Slot, swap.ClimbingLink);
            }));
    }

    /// <summary>
    /// The same swap made by another process rather than another thread is never followed out
    /// either.
    /// </summary>
    /// <remarks>
    /// See <see cref="AdversaryChild"/> for why the attacker is also run outside the process.
    /// </remarks>
    [Theory]
    [MemberData(nameof(OnThisHost))]
    public void A_directory_swapped_for_a_link_out_by_another_process_is_never_followed_out(string backend)
    {
        using StressArena arena = new();
        LinkSwap swap = LinkSwap.Stage(arena);

        swap.Fight(
            output,
            "directory swapped for a link out, by another process",
            backend,
            arena,
            () => new ProcessAdversary(swap.Slot, swap.AbsoluteLink, swap.ClimbingLink));
    }

    /// <summary>
    /// Renaming directories in the middle of a path can steer the walk to a different object
    /// inside the sandbox, and never to one outside; how often it does is counted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the race that measures the window. The path is <c>p/q/f</c>, and two directories
    /// take turns at <c>p</c>. Whichever is not at <c>p</c> has its <c>q</c> swapped with a stale
    /// sibling and swapped back, so that at every instant the directory at <c>p</c> holds its
    /// fresh <c>q</c> — the stale one is only ever in place while its directory is elsewhere.
    /// A resolution that reaches a stale file therefore saw <c>p</c> at one moment and
    /// <c>q</c> at another: it reached something the path never named at any single instant.
    /// </para>
    /// <para>
    /// That is the residual window a component-at-a-time walk leaves open, and it is permitted:
    /// every object reached this way is inside the sandbox, because each step was taken relative
    /// to a directory handle the walk already held. The race asserts that and counts the
    /// redirections, so the size of the window is a number rather than a description.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(OnThisHost))]
    public void A_rename_in_the_middle_of_a_path_redirects_only_to_objects_inside(string backend)
    {
        using StressArena arena = new();

        string p = arena.Inside("p");
        string spare = arena.Inside("spare");
        HashSet<CapFileId> fresh = [];
        HashSet<CapFileId> stale = [];
        foreach (string directory in new[] { p, spare })
        {
            Directory.CreateDirectory(Path.Join(directory, "q"));
            Directory.CreateDirectory(Path.Join(directory, "q-stale"));
            File.WriteAllText(Path.Join(directory, "q", StressArena.FileName), "fresh");
            File.WriteAllText(Path.Join(directory, "q-stale", StressArena.FileName), "stale");
            fresh.Add(StressArena.IdentityOf(Path.Join(directory, "q", StressArena.FileName)));
            stale.Add(StressArena.IdentityOf(Path.Join(directory, "q-stale", StressArena.FileName)));
        }

        string spareQ = Path.Join(spare, "q");
        string spareStale = Path.Join(spare, "q-stale");
        bool bothSeen = false;

        Tally tally = Race.Fight(
            output,
            "directory renamed mid-path",
            backend,
            arena,
            () => new ThreadAdversary(() =>
            {
                // Bring the other directory in; the one now at the spare name is out of reach.
                HostOps.Exchange(p, spare);
                HostOps.Exchange(spareQ, spareStale);
                HostOps.Exchange(spareQ, spareStale);
            }),
            (root, _) => Race.OpenFile(root, "p/q/f"),
            identity => fresh.Contains(identity) ? Outcome.Consistent
                : stale.Contains(identity) ? Outcome.Redirected
                : arena.IsOutside(identity) ? Outcome.Escaped
                : Outcome.Unidentified,
            StressSettings.Iterations,
            beforeClassifying: counts => bothSeen = fresh.All(identity => counts.TimesReached(identity) > 0));

        if (!bothSeen)
        {
            Assert.Skip(
                $"In {tally.Attempts} attempts only one of the two directories was ever reached through p, " +
                $"so the renames never landed while the race was being run. {tally}");
        }
    }

    /// <summary>
    /// A component deleted and recreated as a different kind of object, over and over, is
    /// never resolved to anything outside.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name <c>c</c> holds in turn a directory, a plain file and a link pointing outside,
    /// each one new: made under another name, moved into place, and the one it replaced moved
    /// away and removed. A resolver that decided what <c>c</c> was on one look and acted on it
    /// on another meets each kind where it expected the one before.
    /// </para>
    /// <para>
    /// Every object is identified before it is moved into place, so the race knows every object
    /// that was ever inside and can tell them from anything else.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(OnThisHost))]
    public void A_component_recreated_as_another_kind_is_never_followed_out(string backend)
    {
        using StressArena arena = new();
        arena.RequireSymbolicLinks();

        string c = arena.Inside("c");
        string stage = arena.Inside("stage");
        string trash = arena.Inside("trash");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(trash);
        Directory.CreateDirectory(c);
        File.WriteAllText(Path.Join(c, StressArena.FileName), "inside");

        ConcurrentDictionary<CapFileId, byte> inside = new();
        inside.TryAdd(StressArena.IdentityOf(c), 0);
        inside.TryAdd(StressArena.IdentityOf(Path.Join(c, StressArena.FileName)), 0);

        long generation = 0;
        void Recreate()
        {
            long n = generation++;
            string staged = Path.Join(stage, n.ToString(System.Globalization.CultureInfo.InvariantCulture));
            switch (n % 3)
            {
                case 0:
                    Directory.CreateDirectory(staged);
                    File.WriteAllText(Path.Join(staged, StressArena.FileName), "inside");
                    inside.TryAdd(StressArena.IdentityOf(staged), 0);
                    inside.TryAdd(StressArena.IdentityOf(Path.Join(staged, StressArena.FileName)), 0);
                    break;

                case 1:
                    File.WriteAllText(staged, "inside");
                    inside.TryAdd(StressArena.IdentityOf(staged), 0);
                    break;

                default:
                    Directory.CreateSymbolicLink(staged, arena.Outside(StressArena.DirectoryName));
                    break;
            }

            string removed = Path.Join(trash, n.ToString(System.Globalization.CultureInfo.InvariantCulture));
            HostOps.TryRename(c, removed);
            HostOps.Rename(staged, c);
            HostOps.RemoveWithoutFollowing(removed);
        }

        Tally tally = Race.Fight(
            output,
            "component recreated as another kind",
            backend,
            arena,
            () => new ThreadAdversary(Recreate),
            (root, i) => (i % 3) switch
            {
                0 => Race.OpenFile(root, "c/f"),
                1 => Race.OpenDir(root, "c"),
                _ => Race.OpenFile(root, "c"),
            },
            identity => inside.ContainsKey(identity) ? Outcome.Consistent
                : arena.IsOutside(identity) ? Outcome.Escaped
                : Outcome.Unidentified,
            StressSettings.Iterations);

        tally.RequireContest("component recreated as another kind on " + backend, Outcome.RefusedAsEscape);
    }

    /// <summary>
    /// The last component swapped for a link to a file outside, between whatever check the
    /// resolver makes and the open itself, is never written through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The race that matters most for a write: an operation that looks at <c>d/target</c>, sees
    /// a plain file, and then opens it for writing after it has become a link to a file outside
    /// has written outside. Every attempt opens for writing, one in two emptying the file first,
    /// and writes through the handle it got — so an escape here is not only seen in the identity
    /// reached but in the file outside being changed, which the race checks afterwards byte for
    /// byte.
    /// </para>
    /// <para>
    /// The two modes differ in what they do with a link at the name. Opening what is there
    /// follows it, and is the mode that could be written through to somewhere else, so it must
    /// be refused for where the link leads. Emptying what is there refuses the link without
    /// reading it, so an attempt that meets the link in place answers with that refusal rather
    /// than as an escape.
    /// </para>
    /// <para>
    /// Both modes need the file to be there already, and that is deliberate. Where the host
    /// swaps two names in three renames rather than one, the name is briefly missing; a mode
    /// that created a file there would leave behind an object that the attacker's next rename
    /// removes from the sandbox before the race is over, so nothing could say afterwards what
    /// had been reached — and for a check of containment, an object that cannot be vouched for
    /// is as bad as one outside. A mode that only opens what it finds is turned away instead,
    /// and the turning away is counted.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(OnThisHost))]
    public void The_last_component_swapped_for_a_link_out_is_never_written_through(string backend)
    {
        using StressArena arena = new();
        arena.RequireSymbolicLinks();

        string target = arena.Inside("d/target");
        string link = arena.Inside("d/link");
        Directory.CreateDirectory(arena.Inside("d"));
        File.WriteAllText(target, "inside");
        File.CreateSymbolicLink(link, arena.Outside(StressArena.FileName));
        CapFileId file = StressArena.IdentityOf(target);

        byte[] written = Encoding.UTF8.GetBytes("written from inside the sandbox");

        CapFileId WriteThrough(Dir root, int i)
        {
            (FileMode mode, FileAccess access) = i % 2 == 0
                ? (FileMode.Open, FileAccess.ReadWrite)
                : (FileMode.Truncate, FileAccess.Write);

            using CapFile opened = root.OpenFile("d/target", mode, access);
            opened.Write(written, 0);
            return opened.GetMetadata().FileId;
        }

        Tally tally = Race.Fight(
            output,
            "last component swapped for a link out",
            backend,
            arena,
            () => new ThreadAdversary(() => HostOps.Exchange(target, link)),
            WriteThrough,
            identity => identity == file ? Outcome.Consistent
                : arena.IsOutside(identity) ? Outcome.Escaped
                : Outcome.Unidentified,
            StressSettings.Iterations);

        string context = "last component swapped for a link out on " + backend;
        if (HostOps.ExchangesAtomically)
        {
            tally.AssertOnly(context, Outcome.Consistent, Outcome.RefusedAsEscape, Outcome.OtherRefusal);
        }

        tally.RequireContest(context, Outcome.RefusedAsEscape, Outcome.OtherRefusal);
    }

    /// <summary>
    /// A name swapped back and forth between a file and a directory, while it is opened
    /// without saying which kind it holds, is always reported as the kind of the object the
    /// open reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The kind is read from the handle the open produced, not from the name, so however the
    /// name changes around the open, the kind reported and the handle given agree, and the
    /// handle is one of the two objects the race put there. That the name is looked up only
    /// once is shown exactly against the simulated filesystem, where a second lookup can be
    /// seen; this race holds the real backends to the part a kernel lets a test observe.
    /// </para>
    /// <para>
    /// The race counts as fought only if both objects were reached, so that each kind was
    /// opened while the other was being swapped in.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(OnThisHost))]
    public void A_name_swapped_between_a_file_and_a_directory_is_opened_as_the_kind_it_reports(string backend)
    {
        using StressArena arena = new();

        string slot = arena.Inside("slot");
        string other = arena.Inside("other");
        Directory.CreateDirectory(slot);
        File.WriteAllText(other, "inside");
        CapFileId directory = StressArena.IdentityOf(slot);
        CapFileId file = StressArena.IdentityOf(other);

        long mismatches = 0;
        bool bothSeen = false;

        Tally tally = Race.Fight(
            output,
            "name swapped between a file and a directory",
            backend,
            arena,
            () => new ThreadAdversary(() => HostOps.Exchange(slot, other)),
            (root, _) =>
            {
                using CapOpened opened = root.OpenAny("slot");
                CapMetadata metadata;
                if (opened.IsDirectory)
                {
                    using Dir taken = opened.TakeDir();
                    metadata = taken.GetMetadata();
                }
                else
                {
                    using CapFile taken = opened.TakeFile();
                    metadata = taken.GetMetadata();
                }

                CapFileType reported = opened.IsDirectory ? CapFileType.Directory : CapFileType.File;
                if (metadata.Type != reported)
                {
                    Interlocked.Increment(ref mismatches);
                }

                return metadata.FileId;
            },
            identity => identity == directory || identity == file ? Outcome.Consistent
                : arena.IsOutside(identity) ? Outcome.Escaped
                : Outcome.Unidentified,
            StressSettings.Iterations,
            beforeClassifying: counts => bothSeen = counts.TimesReached(directory) > 0 && counts.TimesReached(file) > 0);

        string context = "name swapped between a file and a directory on " + backend;
        Assert.True(
            Interlocked.Read(ref mismatches) == 0,
            $"{context}: {mismatches} opens reported a kind other than that of the handle they gave. {tally}");

        if (HostOps.ExchangesAtomically)
        {
            tally.AssertOnly(context, Outcome.Consistent);
        }

        if (!bothSeen)
        {
            Assert.Skip(
                $"In {tally.Attempts} attempts only one of the two objects was ever reached, so the " +
                $"swaps never landed while the race was being run. {tally}");
        }
    }

    /// <summary>
    /// Asserts that a race whose attacker swaps a name between an object inside and a link out
    /// got only the two answers those deserve: the object, or a refusal as an escape.
    /// </summary>
    /// <remarks>
    /// Anything else is a lost race reported to the caller as though it were about their path —
    /// the walk reading a link that has just been put back as a directory, say, and passing on
    /// the read's complaint. Where the host swaps in three renames rather than one, the name is
    /// briefly missing and the renames can collide with the operation, so the check is made
    /// only where the swap is a single step.
    /// </remarks>
    private static void AssertAnswersForASwap(Tally tally, string context)
    {
        if (HostOps.ExchangesAtomically)
        {
            tally.AssertOnly(context, Outcome.Consistent, Outcome.RefusedAsEscape);
        }
    }

    /// <summary>
    /// A directory and two links pointing outside, one absolute and one climbing out through a
    /// parent step, ready to be swapped with it.
    /// </summary>
    private sealed record LinkSwap(string Slot, string AbsoluteLink, string ClimbingLink, HashSet<CapFileId> Inside)
    {
        public static LinkSwap Stage(StressArena arena)
        {
            arena.RequireSymbolicLinks();

            string slot = arena.Inside("slot");
            Directory.CreateDirectory(slot);
            File.WriteAllText(Path.Join(slot, StressArena.FileName), "inside");
            string absoluteLink = arena.Inside("absolute-link");
            string climbingLink = arena.Inside("climbing-link");
            Directory.CreateSymbolicLink(absoluteLink, arena.Outside(StressArena.DirectoryName));
            Directory.CreateSymbolicLink(climbingLink, Path.Join("..", "outside", StressArena.DirectoryName));

            return new LinkSwap(
                slot,
                absoluteLink,
                climbingLink,
                [StressArena.IdentityOf(slot), StressArena.IdentityOf(Path.Join(slot, StressArena.FileName))]);
        }

        /// <summary>
        /// Opens the file through the directory, the directory itself, and the file's metadata
        /// by path, in turn, while the attacker swaps.
        /// </summary>
        public void Fight(ITestOutputHelper output, string race, string backend, StressArena arena, Func<Adversary> adversary)
        {
            Tally tally = Race.Fight(
                output,
                race,
                backend,
                arena,
                adversary,
                (root, i) => (i % 3) switch
                {
                    0 => Race.OpenFile(root, "slot/f"),
                    1 => Race.OpenDir(root, "slot"),
                    _ => root.GetMetadata("slot/f").FileId,
                },
                identity => Inside.Contains(identity) ? Outcome.Consistent
                    : arena.IsOutside(identity) ? Outcome.Escaped
                    : Outcome.Unidentified,
                StressSettings.Iterations);

            AssertAnswersForASwap(tally, $"{race} on {backend}");
            tally.RequireContest($"{race} on {backend}", Outcome.RefusedAsEscape);
        }
    }
}
