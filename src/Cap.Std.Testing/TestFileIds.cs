namespace Cap.Std.Testing;

/// <summary>
/// Makes <see cref="CapFileId"/> values for test doubles.
/// </summary>
/// <remarks>
/// <para>
/// <strong>For doubles only.</strong> A stub of <see cref="IDir"/> or <see cref="IDirEntry"/>
/// has to report an identity, and <see cref="CapFileId"/> has no public constructor, so this
/// is where a test gets one. Production code gets identities from a handle, through
/// <see cref="CapMetadata.FileId"/> or <see cref="DirEntry.FileId"/>, and nothing else.
/// </para>
/// <para>
/// That is why the constructor stays closed in <c>Cap.Std</c>. An identity is only worth
/// comparing because the filesystem issued it: <see cref="CapMetadata.IsSameFileAs"/> tells a
/// caller it has reached one object under two names, and a copy or a walk trusts that answer
/// to decide what to skip. Letting production code make identities up would add nothing it
/// needs and give it a way to make that answer wrong.
/// </para>
/// <para>
/// A made-up identity is an ordinary value: two made from the same numbers are equal and hash
/// alike, and are equal to one a real handle reports with those numbers. Tests that want
/// distinct objects should give them distinct numbers.
/// </para>
/// </remarks>
public static class TestFileIds
{
    /// <summary>
    /// The volume <see cref="Next"/> puts its identities on.
    /// </summary>
    /// <remarks>
    /// The bytes spell <c>TEST</c>. No in-memory filesystem uses it, so an identity from
    /// <see cref="Next"/> never equals one a handle on such a filesystem reports.
    /// </remarks>
    public const ulong DefaultVolumeId = 0x5445_5354;

    private static long s_nextNodeId;

    /// <summary>Makes the identity of the object <paramref name="nodeId"/> on <paramref name="volumeId"/>.</summary>
    /// <param name="volumeId">The filesystem the object is on, reported as <see cref="CapFileId.VolumeId"/>.</param>
    /// <param name="nodeId">The object's identity within it, reported as <see cref="CapFileId.NodeId"/>.</param>
    /// <returns>The identity.</returns>
    public static CapFileId Create(ulong volumeId, UInt128 nodeId) => new(volumeId, nodeId);

    /// <summary>
    /// Makes an identity on <see cref="DefaultVolumeId"/> that no earlier call to this method
    /// in the process has returned.
    /// </summary>
    /// <returns>The identity.</returns>
    /// <remarks>
    /// For a test that needs objects to be told apart and does not care what they are called.
    /// Safe to call from any number of threads at once.
    /// </remarks>
    public static CapFileId Next() =>
        new(DefaultVolumeId, (ulong)Interlocked.Increment(ref s_nextNodeId));
}
