using System.Collections.Concurrent;
using Cap.Std;

namespace Cap.Stress.Tests;

/// <summary>What one attempt in a race came to.</summary>
internal enum Outcome
{
    /// <summary>
    /// Reached an object inside the sandbox that the path named at some instant: the answer an
    /// atomic resolution could have given.
    /// </summary>
    Consistent,

    /// <summary>
    /// Reached an object inside the sandbox that the path never named at any single instant.
    /// Resolution was steered by a change made while it was part-way through — the residual
    /// window the name-at-a-time walk leaves open, and the thing the races here are built to
    /// count.
    /// </summary>
    Redirected,

    /// <summary>Refused because the path led outside the sandbox.</summary>
    RefusedAsEscape,

    /// <summary>Refused because a name on the way was not there when it was looked for.</summary>
    Missing,

    /// <summary>Refused for some other reason a filesystem gives: the wrong kind of object, say.</summary>
    OtherRefusal,

    /// <summary>Reached an object outside the sandbox. Never acceptable, on any backend.</summary>
    Escaped,

    /// <summary>
    /// Reached an object that is neither one the race put inside the sandbox nor one outside
    /// it. Never acceptable either: it means the result cannot be vouched for, which for a
    /// check of containment is the same as failing it.
    /// </summary>
    Unidentified,
}

/// <summary>
/// The outcomes of a race, counted.
/// </summary>
/// <remarks>
/// <para>
/// An attempt that succeeds is counted by the identity of what it reached — the volume and
/// file number the operating system gives the open object — and only classified once the race
/// is over. Deciding then rather than on each attempt keeps the attempt itself as short as the
/// operation it is timing, and means the classification can use everything the attacker created
/// during the run, including objects it created after the attempt that reached them had started.
/// </para>
/// <para>
/// A failure is counted by what kind of refusal it was. Only the refusals a filesystem gives are
/// accepted; anything else is a fault in the code under test and is let through to fail the
/// race, because a race whose failures were all counted as refusals could pass by crashing.
/// </para>
/// <para>
/// Safe to record into from several threads at once.
/// </para>
/// </remarks>
internal sealed class Tally
{
    private readonly ConcurrentDictionary<CapFileId, long> _reached = new();
    private readonly ConcurrentDictionary<string, long> _refusals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _examples = new(StringComparer.Ordinal);
    private readonly long[] _counts = new long[Enum.GetValues<Outcome>().Length];
    private long _attempts;

    /// <summary>Every attempt made.</summary>
    public long Attempts => Interlocked.Read(ref _attempts);

    /// <summary>How many attempts came to an outcome. Meaningful once <see cref="Classify"/> has run.</summary>
    public long this[Outcome outcome] => Interlocked.Read(ref _counts[(int)outcome]);

    /// <summary>The refusals other than an escape or a missing name, by exception type.</summary>
    public IReadOnlyDictionary<string, long> OtherRefusals => _refusals;

    /// <summary>How many cycles the attacker made, when it can say.</summary>
    public long? AdversaryCycles { get; set; }

    /// <summary>How many attempts reached one object. Meaningful until <see cref="Classify"/> has run.</summary>
    public long TimesReached(CapFileId identity) => _reached.TryGetValue(identity, out long count) ? count : 0;

    /// <summary>Counts an attempt that reached something, by what it reached.</summary>
    public void Reached(CapFileId identity)
    {
        Interlocked.Increment(ref _attempts);
        _reached.AddOrUpdate(identity, 1, static (_, count) => count + 1);
    }

    /// <summary>Counts an attempt that was refused, or rethrows one that failed some other way.</summary>
    public void Refused(Exception failure)
    {
        Outcome outcome = failure switch
        {
            SandboxEscapeException => Outcome.RefusedAsEscape,
            FileNotFoundException or DirectoryNotFoundException => Outcome.Missing,
            IOException or UnauthorizedAccessException => Outcome.OtherRefusal,
            _ => throw new InvalidOperationException(
                "An attempt failed with something other than a refusal a filesystem gives.", failure),
        };

        if (outcome == Outcome.OtherRefusal)
        {
            _refusals.AddOrUpdate(failure.GetType().Name, 1, static (_, count) => count + 1);
            _examples.TryAdd(failure.GetType().Name, failure.Message);
        }

        Interlocked.Increment(ref _attempts);
        Interlocked.Increment(ref _counts[(int)outcome]);
    }

    /// <summary>Counts an attempt that came to an outcome decided on the spot.</summary>
    public void Record(Outcome outcome)
    {
        Interlocked.Increment(ref _attempts);
        Interlocked.Increment(ref _counts[(int)outcome]);
    }

    /// <summary>
    /// Sorts every identity reached into an outcome, once the race is over.
    /// </summary>
    /// <param name="classify">
    /// What an identity means for this race. Given every identity reached, including ones the
    /// race does not know, which it must call <see cref="Outcome.Unidentified"/>.
    /// </param>
    public void Classify(Func<CapFileId, Outcome> classify)
    {
        foreach ((CapFileId identity, long count) in _reached)
        {
            Outcome outcome = classify(identity);
            if (outcome is Outcome.RefusedAsEscape or Outcome.Missing or Outcome.OtherRefusal)
            {
                throw new ArgumentException("An identity that was reached cannot have been refused.", nameof(classify));
            }

            Interlocked.Add(ref _counts[(int)outcome], count);
        }

        _reached.Clear();
    }

    /// <summary>
    /// Asserts the two things no backend is ever allowed: reaching outside, and reaching
    /// something that cannot be vouched for.
    /// </summary>
    public void AssertContained(string context)
    {
        Assert.True(
            this[Outcome.Escaped] == 0,
            $"{context}: {this[Outcome.Escaped]} attempt(s) out of {Attempts} reached an object outside the sandbox. {this}");
        Assert.True(
            this[Outcome.Unidentified] == 0,
            $"{context}: {this[Outcome.Unidentified]} attempt(s) out of {Attempts} reached an object the race did not " +
            $"create inside the sandbox, so what they reached cannot be vouched for. {this}");
    }

    /// <summary>
    /// Asserts that every attempt came to one of the outcomes a race allows, and to nothing
    /// else.
    /// </summary>
    /// <remarks>
    /// For a race whose attacker only ever puts one of a few known things under a name: each
    /// attempt has a right answer for each of them, and any other answer — a refusal for a
    /// reason none of them would give — is a lost race reaching the caller as an error about
    /// their request.
    /// </remarks>
    public void AssertOnly(string context, params Outcome[] allowed)
    {
        foreach (Outcome outcome in Enum.GetValues<Outcome>().Except(allowed))
        {
            Assert.True(
                this[outcome] == 0,
                $"{context}: {this[outcome]} attempt(s) came to {outcome}, which nothing the attacker put in place " +
                $"should produce. {this}");
        }
    }

    /// <summary>
    /// Stops a race that the attacker never got into, which has checked nothing.
    /// </summary>
    /// <remarks>
    /// Whether the two sides interleave is up to the scheduler, and a race where they never did
    /// has shown only that the operation works on a still tree. That is reported as a skip with
    /// the reason, rather than passed as though the attack had been fought.
    /// </remarks>
    public void RequireContest(string context, params Outcome[] seenUnderAttack)
    {
        long underAttack = seenUnderAttack.Sum(outcome => this[outcome]);
        if (underAttack == 0)
        {
            Assert.Skip(
                $"{context}: in {Attempts} attempts the attacker's change was never seen " +
                $"({string.Join(", ", seenUnderAttack)} all zero), so the race was not fought on this run. {this}");
        }
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        IEnumerable<string> counts = Enum.GetValues<Outcome>().Select(outcome => $"{outcome}={this[outcome]}");
        IEnumerable<string> others = _refusals.Select(pair => $"{pair.Key}={pair.Value}, e.g. \"{_examples[pair.Key]}\"");
        return $"[attempts={Attempts} {string.Join(' ', counts)}" +
            (_refusals.IsEmpty ? string.Empty : $" ({string.Join(' ', others)})") +
            (AdversaryCycles is { } cycles ? $" adversary-cycles={cycles}" : string.Empty) + "]";
    }
}
