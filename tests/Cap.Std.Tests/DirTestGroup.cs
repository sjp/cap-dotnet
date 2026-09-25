namespace Cap.Std.Tests;

/// <summary>
/// Groups the handle tests so that they run one at a time.
/// </summary>
/// <remarks>
/// Every handle on the disk resolves through the one host implementation, and the counters
/// that say which of its two strategies ran belong to it, so they are process-wide. Some of
/// these classes also replace the host to run each strategy in turn. Two classes opening
/// directories at once would each see the other's opens, so a test asserting that a path cost
/// one kernel operation and no per-name opens would fail for reasons having nothing to do
/// with the code it is about. Tests on a simulated tree have their own implementation and
/// counters, and do not need to be here. Left to the default that is an occasional
/// failure in whichever test happened to be running, which is the worst way to learn about
/// it.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class DirTestGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "directory handles";
}
