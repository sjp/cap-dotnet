namespace Cap.Primitives.Tests;

/// <summary>
/// Groups every test that reads or replaces the process-wide platform implementation, so
/// that they run one at a time.
/// </summary>
/// <remarks>
/// The slot holding the platform implementation is one field for the whole process, because
/// what it stands in for — the operating system — is one thing for the whole process too. A
/// test that substitutes a simulated filesystem therefore substitutes it for every thread,
/// and a test running alongside it would find its handles belonging to a filesystem that
/// does not exist. Left to the default, these classes run in parallel and the failure shows
/// up as an unrelated test failing occasionally, which is the worst way to learn about it.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class PlatformOpsTestGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "platform implementation";
}
