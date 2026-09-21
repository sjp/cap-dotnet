namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// How long a confined open keeps trying after the kernel reports that resolution lost a
/// race with a concurrent rename.
/// </summary>
/// <remarks>
/// <para>
/// Confined resolution can fail for a reason that is not a failure. When a rename moves a
/// directory the kernel is in the middle of resolving through, the kernel abandons the walk
/// and says so, because the position it had reached is no longer the position the caller
/// asked about. Nothing is wrong, nothing has escaped, and the operation has simply not
/// happened yet. The response is to ask again.
/// </para>
/// <para>
/// It is reached more narrowly than it looks. The kernel only reports the lost race where
/// resolution has had to reconsider which directory it is standing in — which is what a
/// <c>..</c> component makes it do — so a path of plain names resolving through the very same
/// rename never produces one. The retry therefore matters exactly for the paths that carry
/// parent links through to the resolver, and not at all for those that refuse them at the
/// parse.
/// </para>
/// <para>
/// Asking again cannot be unconditional. Anything able to write inside the subtree can keep
/// a rename loop running for as long as it likes, and an unbounded retry would hand it the
/// power to hang the calling thread — turning a containment guarantee into a denial of
/// service. So the retries are counted, and a caller that exhausts them is told that
/// resolution kept losing rather than being made to wait indefinitely.
/// </para>
/// <para>
/// The bound is deliberately small. A handful of attempts absorbs the ordinary case of a
/// busy directory, and no number of attempts wins against an adversary that is renaming in a
/// loop, so raising it buys patience against nobody and costs latency against everybody.
/// What it must never do is fall through to a different resolution strategy: a caller that
/// asked for the guarantee the kernel makes has to be told it was not available, not quietly
/// given a weaker one.
/// </para>
/// </remarks>
internal static class ConfinedRetryPolicy
{
    /// <summary>How many times resolution may be restarted before giving up.</summary>
    public const int RetryLimit = 16;

    /// <summary>
    /// Decides whether an attempt that failed with <paramref name="errno"/> should be made
    /// again.
    /// </summary>
    /// <param name="errno">The code the attempt failed with.</param>
    /// <param name="attempt">
    /// How many attempts have already been made, counting from zero for the first.
    /// </param>
    public static bool ShouldRetry(int errno, int attempt) =>
        errno == LinuxErrno.EAGAIN && attempt < RetryLimit;
}
