namespace Cap.Primitives.Tests;

/// <summary>
/// Groups every test that reads the host implementation's counters or replaces the host, so
/// that they run one at a time.
/// </summary>
/// <remarks>
/// The host is one implementation for the whole process, because what it stands in for, the
/// operating system, is one thing for the whole process too. A test that replaces it changes
/// where every root opened by path comes from, on every thread. A test that counts its opens
/// counts everyone else's opens as well. Left to the default, these classes run in parallel
/// and the failure shows up as an unrelated test failing occasionally, which is the worst way
/// to learn about it. Tests that open their root through a simulated implementation directly
/// touch neither, and stay out of this group.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class PlatformOpsTestGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "platform implementation";
}
