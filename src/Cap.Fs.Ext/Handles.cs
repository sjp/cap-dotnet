using Cap.Primitives;
using Cap.Std;

namespace Cap.Fs.Ext;

/// <summary>
/// What the helpers need to know about a handle that the interface does not say.
/// </summary>
/// <remarks>
/// <para>
/// Every helper here takes an <see cref="IDir"/>, so that code written against the interface —
/// to be handed a stub, or a wrapper that records or restricts what passes through it — keeps
/// the convenience layer. Almost every handle that actually arrives is a <see cref="Dir"/>, and
/// a few things are known about one of those that no interface member reports: how it spells a
/// path, and which filesystem it belongs to. Those answers are taken from the handle when it is
/// a <see cref="Dir"/>, and fall back to what can be assumed of anything else.
/// </para>
/// </remarks>
internal static class Handles
{
    /// <summary>How a handle spells a path.</summary>
    /// <remarks>
    /// For a <see cref="Dir"/> this is its filesystem's own syntax, which a filesystem held in
    /// memory can set independently of the machine. Anything else is assumed to spell paths as
    /// the machine does, since nothing it exposes says otherwise.
    /// </remarks>
    public static CapPathSyntax SyntaxOf(IDir dir) =>
        dir is Dir concrete ? concrete.PathSyntax : CapPath.HostSyntax;

    /// <summary>
    /// Whether identities read through the two handles can be compared, because both describe
    /// objects on one filesystem.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two <see cref="Dir"/> handles answer this exactly. For anything else only the backend
    /// each reports is available: handles on the host's filesystem share one set of
    /// identities, the one the kernel assigns, so two of those can be compared. A filesystem
    /// held in memory numbers its own objects, and two such filesystems can hand out the same
    /// number, so the answer there is no. A handle that reports no backend at all, as a stub
    /// that was not told one does, is never compared.
    /// </para>
    /// <para>
    /// A false answer costs a check that could have been made; a true one between unrelated
    /// filesystems would refuse an operation because two numbers happened to match.
    /// </para>
    /// </remarks>
    public static bool ShareIdentities(IDir first, IDir second)
    {
        if (first is Dir a && second is Dir b)
        {
            return a.SharesBackendWith(b);
        }

        return first.Backend == second.Backend &&
               first.Backend is not (ResolutionBackend.None or ResolutionBackend.InMemory);
    }
}
