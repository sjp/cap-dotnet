using Cap.Escape.Tests;
using Cap.Primitives;
using Cap.Std;
using WasiHost.Preview1;

namespace WasiHost.Tests;

/// <summary>
/// The escape corpus, driven through the WASI layer: every case, through the WASI call for
/// every operation, on every backend this host has, under both symbolic-link policies.
/// </summary>
/// <remarks>
/// <para>
/// The corpus says what each attack must come to when made on the library directly. Made by a
/// guest, it must come to the same thing: the adapter adds a descriptor table and a
/// translation from exceptions to error codes, and neither may let a path reach anything the
/// preopened directory does not. So each test asserts, as the direct corpus does, that the
/// operation came to the expected outcome, and — whatever it came to — that nothing outside
/// the sandbox changed and nothing the guest was shown came from there.
/// </para>
/// <para>
/// Where the WASI call genuinely means something different from the library call, the
/// expectation is adjusted here and the reason given; see <see cref="Expected"/>.
/// </para>
/// </remarks>
[Collection(CorpusGroup.Name)]
public sealed class WasiEscapeCorpusTests
{
    /// <summary>Every row the WASI layer can drive.</summary>
    public static TheoryData<string, SymlinkPolicy, string, string> Matrix
    {
        get
        {
            TheoryData<string, SymlinkPolicy, string, string> rows = [];
            foreach (string backend in Backends.OnThisHost)
            {
                foreach (SymlinkPolicy policy in new[] { SymlinkPolicy.FollowWithinSandbox, SymlinkPolicy.Deny })
                {
                    foreach (EscapeCase entry in EscapeCorpus.Cases.Where(entry => entry.AppliesToHost))
                    {
                        foreach (Operation operation in Enum.GetValues<Operation>().Where(op => op != Operation.DeleteTree))
                        {
                            rows.Add(backend, policy, entry.Name, operation.ToString());
                        }
                    }
                }
            }

            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Every_attack_made_by_a_guest_comes_to_its_expected_outcome_and_reaches_nothing_outside(
        string backend, SymlinkPolicy policy, string caseName, string operationName)
    {
        Operation operation = Enum.Parse<Operation>(operationName);
        EscapeCase entry = EscapeCorpus.Named(caseName);
        HostFeature features = HostFeatures.Current;

        HostFeature missing = entry.Requires & ~features;
        if (missing != HostFeature.None)
        {
            Assert.Skip($"'{entry.Name}' needs {missing}, which this host or volume does not have.");
        }

        if (operation == Operation.CreateSymlinkTo && (features & HostFeature.Symlinks) == 0)
        {
            Assert.Skip("This volume cannot hold symbolic links, so there is no link to follow.");
        }

        using Arena arena = new();
        arena.Plant(entry.Setup);
        string path = arena.Expand(entry.Path);
        if (!TrampolineGuest.CanCarry(path))
        {
            Assert.Skip(
                $"'{entry.Name}' has no UTF-8 spelling, and WASI paths are UTF-8, so a guest cannot " +
                "express it; the direct corpus covers it.");
        }

        Oracle oracle = new(arena);
        HashSet<(ulong, ulong)> outside = OutsideIdentities(arena);
        string context =
            $"'{entry.Name}' ({entry.ExpectationFor(features).Shape}), {operation} through WASI, {backend}, {policy}";

        WasiObservation observed;
        using (BackendScope scope = Backends.Enter(backend))
        {
            Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), policy);
            using TrampolineGuest guest = new(root);
            observed = WasiOperationRunner.Run(guest, operation, path);
            scope.AssertItRan();
        }

        oracle.AssertContained(observed.Observation, context);
        foreach ((ulong device, ulong inode) in observed.Reached)
        {
            Assert.False(
                outside.Contains((device, inode)),
                $"{context}: reached device {device} inode {inode}, which is an object outside the sandbox.");
        }

        (Outcome[] expected, string? why) = Expected(entry, backend, policy, operation, features);
        Outcome outcome = observed.Observation.Outcome;
        Assert.True(
            expected.Contains(outcome),
            $"{context}: expected {string.Join(" or ", expected)}, got {outcome} (errno {observed.Errno})" +
            (why is null ? "." : $"; {why}"));

        if (outcome != Outcome.Success && !observed.Observation.LinkCreated)
        {
            oracle.AssertUnchangedInside(context);
        }
    }

    /// <summary>
    /// What a guest's attempt may come to: the direct corpus's expectation, adjusted where the
    /// WASI call means something the library call does not.
    /// </summary>
    private static (Outcome[] Expected, string? Why) Expected(
        EscapeCase entry, string backend, SymlinkPolicy policy, Operation operation, HostFeature features)
    {
        bool deny = policy == SymlinkPolicy.Deny;

        // A rooted target is refused before a link is made, whichever policy the descriptor
        // has, as it is when the library is called directly.
        if (operation == Operation.CreateSymlinkTo && EscapeCorpus.IsRootedTarget(entry.Path))
        {
            return ([Outcome.Escape], null);
        }

        Expectation expectation = entry.ExpectationFor(features);
        string? why = null;
        foreach (KnownDifference known in entry.Differences)
        {
            if (known.Backends.Contains(backend))
            {
                expectation = known.Expected;
                why = $"a known difference on this backend: {known.Reason}";
                break;
            }
        }

        Outcome direct = expectation.Resolve(operation, deny, features);

        // A WASI path of nothing but "." names the directory it is resolved against, and a
        // guest may open that directory again and describe it; the library refuses such a
        // path as naming nothing beneath the handle. So opening or describing it succeeds,
        // opening it as a file to read or write is refused because it is a directory, and
        // everything else — removing it, renaming it, linking it — is refused as before.
        if (NamesItself(entry.Path) && operation is
                Operation.OpenDir or Operation.GetMetadata or Operation.Exists or Operation.OpenFile or Operation.CreateFile)
        {
            Outcome itself = operation is Operation.OpenFile or Operation.CreateFile ? Outcome.Refused : Outcome.Success;
            return ([itself], "a WASI path of only '.' names the preopened directory itself");
        }

        // A path ending in `..` names a directory by where it sits and leaves no name in its
        // parent, so the library refuses to create, remove, rename or link one as a request it
        // cannot carry out, and the adapter answers EINVAL. POSIX answers the same requests
        // with a different code per call -- EEXIST, EISDIR, ENOTEMPTY, EBUSY -- so no single
        // code would be more faithful, and each is a refusal.
        if (EndsInParentStep(entry.Path) && direct == Outcome.Refused && operation is not
                (Operation.OpenFile or Operation.CreateFile or Operation.CreateSymlinkTo))
        {
            return ([Outcome.Refused, Outcome.Malformed], "a path ending in '..' leaves no name to act on, which the adapter reports as EINVAL");
        }

        // rename(2) replaces what holds the destination name, and a guest's rename is that call.
        // The library's rename, as the corpus makes it, refuses a taken name instead. So where
        // the corpus expects that refusal, a guest's rename may instead succeed by replacing
        // the name — which is inside, and which the containment checks still cover.
        if (operation == Operation.RenameTo && direct == Outcome.Refused)
        {
            return ([Outcome.Refused, Outcome.Success], "WASI's rename replaces a name that is taken");
        }

        // link(2) reports a directory given a second name as EPERM, and the adapter answers
        // as it does, where the library refuses the request as naming a directory.
        if (operation == Operation.HardLinkFrom && direct == Outcome.Refused)
        {
            return ([Outcome.Refused, Outcome.Denied], "link reports a directory given a second name as EPERM");
        }

        // readlink(2) reports a name that holds no link as EINVAL, which is also the code a
        // malformed path gets. Where the corpus expects the library's refusal to read
        // something that is not a link, a guest may see either.
        if (operation == Operation.ReadLink && direct == Outcome.Refused)
        {
            return ([Outcome.Refused, Outcome.Malformed], "readlink reports a name that holds no link as EINVAL");
        }

        return ([direct], why);
    }

    private static bool EndsInParentStep(string path) =>
        path.Split('/').LastOrDefault(component => component is not ("" or ".")) == "..";

    private static bool NamesItself(string path) =>
        path.Length > 0 && path[0] != '/' && path.Split('/').All(component => component is "" or ".");

    /// <summary>
    /// The device and inode of everything outside the sandbox, as a guest would see them in a
    /// <c>filestat</c> record. Taken before the backend under test is installed.
    /// </summary>
    private static HashSet<(ulong, ulong)> OutsideIdentities(Arena arena)
    {
        using Dir host = Dir.Open(arena.HostPath, AmbientAuthority.Acquire());
        HashSet<(ulong, ulong)> identities = [Identity(host.GetMetadata())];
        foreach (string name in new[]
        {
            EscapeCorpus.OutsideDirectory,
            $"{EscapeCorpus.OutsideDirectory}/{EscapeCorpus.OutsideFile}",
            $"{EscapeCorpus.OutsideDirectory}/{EscapeCorpus.OutsideSubdirectory}",
            $"{EscapeCorpus.OutsideDirectory}/{EscapeCorpus.OutsideSubdirectory}/{EscapeCorpus.OutsideNestedFile}",
        })
        {
            identities.Add(Identity(host.GetMetadata(name)));
        }

        return identities;

        static (ulong, ulong) Identity(CapMetadata metadata) => (metadata.FileId.VolumeId, (ulong)metadata.FileId.NodeId);
    }
}
