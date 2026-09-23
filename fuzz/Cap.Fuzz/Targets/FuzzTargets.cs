namespace Cap.Fuzz.Targets;

/// <summary>Every fuzz target, by the name it is run under.</summary>
/// <remarks>
/// The name is also the directory its saved inputs live in, under <c>fuzz/regressions</c>,
/// so the harness, the nightly job and the replay tests all find a target's inputs the same
/// way.
/// </remarks>
internal static class FuzzTargets
{
    /// <summary>A target: something that takes one input and throws if it finds a fault.</summary>
    internal delegate void Target(ReadOnlySpan<byte> input);

    public static IReadOnlyDictionary<string, Target> All { get; } = new Dictionary<string, Target>(StringComparer.Ordinal)
    {
        [CapPathTarget.Name] = CapPathTarget.Run,
        [ReparseDataTarget.Name] = ReparseDataTarget.Run,
        [ResolutionWalkTarget.Name] = ResolutionWalkTarget.Run,
    };
}
