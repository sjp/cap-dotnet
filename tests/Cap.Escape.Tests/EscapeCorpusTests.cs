using Cap.Primitives;
using Cap.Std;

namespace Cap.Escape.Tests;

/// <summary>
/// Every case in the corpus, through every operation, on every backend this host has, under
/// both symbolic-link policies.
/// </summary>
/// <remarks>
/// <para>
/// This is the definition of the containment guarantee as far as a test can state it. Each
/// test asserts two things, and the second matters more. The first is that the operation came
/// to the outcome the case says it must — refused as an escape, not found, refused for another
/// reason, or done. The second holds whatever the first says: nothing outside the sandbox
/// changed, and nothing the operation let the caller see — a name listed, a byte read, the
/// identity of an object opened — came from there. A case whose expected outcome was written
/// down wrong still cannot pass by leaking.
/// </para>
/// <para>
/// Under the stricter policy the expectation is derived rather than written out again: a link
/// that would have been followed is refused as a link instead, and everything else is
/// unchanged. That derivation is the policy's contract, so writing it once here is also
/// asserting it for every case.
/// </para>
/// </remarks>
[Collection(CorpusGroup.Name)]
public sealed class EscapeCorpusTests
{
    /// <summary>The product this host runs, as the test framework's rows.</summary>
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
                        foreach (Operation operation in Enum.GetValues<Operation>())
                        {
                            rows.Add(backend, policy, entry.Name, operation.ToString());
                        }
                    }
                }
            }

            return rows;
        }
    }

    /// <summary>
    /// The operation comes to the outcome the case says, and nothing outside is reached.
    /// </summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public void Every_attack_comes_to_its_expected_outcome_and_reaches_nothing_outside(
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
            // The link cannot be made, so what following it would do cannot be observed. That
            // a volume without links refuses to make one is covered by creating one at the path.
            Assert.Skip("This volume cannot hold symbolic links, so there is no link to follow.");
        }

        using Arena arena = new();
        arena.Plant(entry.Setup);

        (Outcome expected, string? difference) = Expected(entry, backend, policy, operation, features);
        Oracle oracle = new(arena);
        string context =
            $"'{entry.Name}' ({entry.ExpectationFor(features).Shape}), {operation}, {backend}, {policy}";

        Observation observation;
        using (BackendScope scope = Backends.Enter(backend))
        {
            using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), policy);
            observation = OperationRunner.Run(root, operation, arena.Expand(entry.Path));
            scope.AssertItRan();
        }

        oracle.AssertContained(observation, context);

        Assert.True(
            expected == observation.Outcome,
            $"{context}: expected {expected}, got {observation.Outcome}" +
            (difference is null ? string.Empty : $" (a known difference on this backend: {difference})") +
            (observation.Detail is null ? "." : $": {observation.Detail.GetType().Name}: {observation.Detail.Message}"));

        if (observation.Outcome != Outcome.Success && !observation.LinkCreated)
        {
            oracle.AssertUnchangedInside(context);
        }
    }

    /// <summary>
    /// The outcome a case must come to on one backend, under one policy, and the reason when
    /// that backend is known to differ from the rest.
    /// </summary>
    internal static (Outcome Expected, string? Difference) Expected(
        EscapeCase entry, string backend, SymlinkPolicy policy, Operation operation, HostFeature features)
    {
        bool deny = policy == SymlinkPolicy.Deny;
        foreach (KnownDifference known in entry.Differences)
        {
            if (known.Backends.Contains(backend))
            {
                return (known.Expected.Resolve(operation, deny, features), known.Reason);
            }
        }

        return (entry.ExpectationFor(features).Resolve(operation, deny, features), null);
    }
}
