namespace Cap.Std.Tests;

/// <summary>
/// Groups the handle tests so that they run one at a time.
/// </summary>
/// <remarks>
/// The implementation a handle dispatches to lives in a single process-wide slot, and the
/// counters that say which of its two strategies ran are process-wide with it. Two classes
/// opening directories at once would each see the other's opens, so a test asserting that a
/// path cost one kernel operation and no per-name opens would fail for reasons having
/// nothing to do with the code it is about. Left to the default that is an occasional
/// failure in whichever test happened to be running, which is the worst way to learn about
/// it.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class DirTestGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "directory handles";
}
