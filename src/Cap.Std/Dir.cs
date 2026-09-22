using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Cap.Primitives;
using Cap.Primitives.Interop;

namespace Cap.Std;

/// <summary>
/// An open directory, and the authority to reach what is beneath it.
/// </summary>
/// <remarks>
/// <para>
/// This is the capability. Code holding one can reach everything under the directory it was
/// opened on and nothing above it, whatever string it is handed: a path that climbs out, a
/// path that was absolute all along, or a path whose middle component turns out to be a
/// symbolic link pointing elsewhere are all refused rather than resolved. Code that does not
/// hold one cannot reach any of it — there is no ambient lookup to fall back on and no way
/// to conjure a handle from a path.
/// </para>
/// <para>
/// <strong>Derivation is the only way to get one.</strong> <see cref="Open"/> is the sole
/// exception and the reason it demands an <see cref="AmbientAuthority"/> token: it is the
/// point where authority enters from outside, and the token exists so that point can be
/// found by searching. Every other handle comes from one that already existed and can only
/// ever cover a subtree of it, so the set of things a component can reach is bounded by the
/// handles it was given.
/// </para>
/// <para>
/// <strong>It does not expose its own path.</strong> There is no property that answers "where
/// is this?", and that is deliberate rather than an omission. A handle is an unforgeable
/// reference to an object; a path is a name that something else may hold by the time it is
/// read. Publishing one invites callers back into string reasoning — joining, comparing,
/// checking a prefix — which is precisely the technique this type exists to replace.
/// <see cref="TryGetPath"/> answers the question for a log line, demands the ambient token
/// to do it, and is documented as the approximation it is.
/// </para>
/// <para>
/// <strong>Thread safety.</strong> Instances are safe for concurrent use by any number of
/// threads. The underlying descriptor or kernel handle is, and the wrapper keeps it alive
/// across every call it is used in, so a disposal racing an operation on another thread ends
/// as a failed operation and never as a call landing on an unrelated object that has taken
/// the handle's number. This is worth stating because the opposite assumption is the usual
/// one for a disposable type holding a native resource.
/// </para>
/// <para>
/// <strong>Disposal is not a tree.</strong> Disposing a handle closes that handle and nothing
/// else. Handles derived from it stay open and keep working, because each owns a separate
/// open object rather than a reference into its parent — the parent was a place to resolve
/// from, not a container. A component handed a derived handle therefore cannot have it
/// revoked by whoever handed it over, which is what makes passing one a genuine transfer of
/// authority rather than a loan.
/// </para>
/// </remarks>
public sealed class Dir : IDisposable
{
    private readonly SafeDirHandle _handle;
    private readonly ConfinedResolveOptions _options;

    private Dir(SafeDirHandle handle, ConfinedResolveOptions options)
    {
        _handle = handle;
        _options = options;
    }

    /// <summary>
    /// Opens a directory by an ordinary path, using the authority the process already has.
    /// </summary>
    /// <param name="path">
    /// The directory to open, resolved exactly as any other program resolves a path: with the
    /// process's own privileges, following links, and against the working directory if it is
    /// relative.
    /// </param>
    /// <param name="authority">
    /// Proof that taking authority from outside the capability graph is intended here. Must
    /// come from <see cref="AmbientAuthority.Acquire"/>; a default value is refused.
    /// </param>
    /// <returns>A handle on the directory.</returns>
    /// <remarks>
    /// The one call in this type that is not confined to anything, and the only one that can
    /// produce a handle from nothing. Everything the containment guarantee promises begins
    /// after it returns: this step is exposed to whatever the host's own path resolution is
    /// exposed to, and a caller that resolves an attacker-controlled string here has handed
    /// over the sandbox before it existed.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty, or <paramref name="authority"/> was never acquired.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">There is no such directory.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the open.</exception>
    /// <exception cref="CapIOException">The open failed for another reason.</exception>
    public static Dir Open(string path, AmbientAuthority authority)
    {
        CapError error = OpenRootCore(path, authority, ConfinedResolveOptions.None, out Dir? dir);
        return error.IsSuccess ? dir! : throw FailureTranslation.ToException(error, path);
    }

    /// <summary>
    /// Opens a directory by an ordinary path, reporting failure rather than throwing.
    /// </summary>
    /// <param name="path">The directory to open. See <see cref="Open"/>.</param>
    /// <param name="authority">An acquired ambient-authority token.</param>
    /// <param name="dir">The handle, when this returns true.</param>
    /// <returns>True when the directory was opened.</returns>
    /// <remarks>
    /// A missing directory is an expected answer rather than an exceptional one, and building
    /// an exception to say so costs more than the open. Arguments that are wrong rather than
    /// unlucky — a null path, a token that was never acquired — still throw, because no
    /// retry or fallback can be the right response to either.
    /// </remarks>
    public static bool TryOpen(string path, AmbientAuthority authority, [NotNullWhen(true)] out Dir? dir) =>
        OpenRootCore(path, authority, ConfinedResolveOptions.None, out dir).IsSuccess;

    /// <summary>
    /// Opens a directory beneath this one.
    /// </summary>
    /// <param name="path">
    /// A relative path of one or more components. Absolute paths, paths naming a drive or a
    /// network location, and paths containing <c>..</c> are refused: none of them names
    /// something this handle covers.
    /// </param>
    /// <returns>A handle on the directory, owning its own open object.</returns>
    /// <remarks>
    /// Resolution is confined to the subtree this handle was opened on. Where the kernel can
    /// resolve the whole path in one operation that cannot leave it, it does; elsewhere the
    /// path is walked a component at a time against handles already held, refusing to follow
    /// any link the policy does not allow and refusing any step that would climb out. Both
    /// answer the same way for the same tree.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">There is no such directory.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the open.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public Dir OpenDir(string path)
    {
        CapPathError pathError = OpenDirCore(path, out Dir? dir, out CapError error);
        if (pathError != CapPathError.None)
        {
            throw FailureTranslation.ToException(pathError, path, nameof(path));
        }

        return error.IsSuccess ? dir! : throw FailureTranslation.ToException(error, path);
    }

    /// <summary>
    /// Opens a directory beneath this one, reporting failure rather than throwing.
    /// </summary>
    /// <param name="path">A relative path. See <see cref="OpenDir"/>.</param>
    /// <param name="dir">The handle, when this returns true.</param>
    /// <returns>True when the directory was opened.</returns>
    /// <remarks>
    /// False covers every reason the path did not open, containment refusals included. An
    /// application that audits escape attempts should call <see cref="OpenDir"/> and catch
    /// <see cref="SandboxEscapeException"/>; this overload deliberately reports no reason,
    /// so that the failure path builds nothing.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryOpenDir(string path, [NotNullWhen(true)] out Dir? dir) =>
        OpenDirCore(path, out dir, out CapError error) == CapPathError.None && error.IsSuccess;

    /// <summary>
    /// Produces a second handle on the same directory, with a lifetime of its own.
    /// </summary>
    /// <returns>A handle carrying exactly the authority this one carries.</returns>
    /// <remarks>
    /// <para>
    /// How a capability is handed to a component that will outlive, or be outlived by, the
    /// code handing it over. Both handles refer to the same open directory, and closing
    /// either leaves the other working, so neither side has to know the other's lifetime.
    /// </para>
    /// <para>
    /// The copy reproduces this handle's authority rather than asking the filesystem what it
    /// would grant. A handle deliberately opened with less than the directory's permissions
    /// would allow keeps that narrowing when it is copied; a copy that re-derived the answer
    /// would be a promotion dressed as a duplicate.
    /// </para>
    /// </remarks>
    /// <exception cref="CapIOException">The handle could not be duplicated.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public Dir Clone()
    {
        CapError error = CloneCore(out Dir? clone);
        return error.IsSuccess
            ? clone!
            : throw new CapIOException($"The directory handle could not be duplicated. ({error})");
    }

    /// <summary>
    /// Produces a second handle on the same directory, reporting failure rather than throwing.
    /// </summary>
    /// <param name="clone">The copy, when this returns true.</param>
    /// <returns>True when the handle was duplicated.</returns>
    /// <remarks>
    /// Duplication fails only when the process is out of handles, which is a condition a
    /// server may well want to shed load for rather than unwind a stack over.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryClone([NotNullWhen(true)] out Dir? clone) => CloneCore(out clone).IsSuccess;

    /// <summary>
    /// Asks the operating system what path this handle is currently reachable by.
    /// </summary>
    /// <param name="authority">
    /// Proof that stepping outside the capability graph is intended. The answer names the
    /// directory from a filesystem root this handle confers no authority over, so producing
    /// it is itself a use of ambient authority.
    /// </param>
    /// <param name="path">The path, when this returns true.</param>
    /// <returns>True when the platform could answer.</returns>
    /// <remarks>
    /// <para>
    /// <strong>For diagnostics only, and never to be acted on.</strong> The answer is a name
    /// the directory answers to at the instant it is read, not the directory itself. By the
    /// time it is used the name may belong to something else, the directory may have been
    /// renamed or unlinked, and it may always have had several names of which this is one.
    /// Reopening it would reopen whatever holds the name then, with the process's ambient
    /// authority and none of the containment this type exists to provide.
    /// </para>
    /// <para>
    /// False is an ordinary outcome, not an error: some hosts have no mechanism to answer at
    /// all — a Linux container without the process filesystem mounted, for one — and a
    /// caller must have something to log in that case too.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="authority"/> was never acquired.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryGetPath(AmbientAuthority authority, [NotNullWhen(true)] out string? path)
    {
        authority.Demand(nameof(authority));
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        CapResult<string> result = PlatformOps.Current.GetHandlePath(_handle);
        path = result.IsSuccess ? result.Value : null;
        return path is not null;
    }

    /// <summary>
    /// Hands out the underlying descriptor or kernel handle.
    /// </summary>
    /// <returns>The live handle this instance holds, not a copy.</returns>
    /// <remarks>
    /// <para>
    /// <strong>This leaks authority.</strong> Whatever receives it can issue any operation
    /// the handle permits, including ones that resolve names with no confinement at all, and
    /// nothing in this library can observe or restrain that. It exists because interoperating
    /// with code that takes a native handle is sometimes unavoidable, and a documented escape
    /// hatch is better than the alternative, which is callers deciding they cannot use this
    /// type at all.
    /// </para>
    /// <para>
    /// The handle is the one this instance uses, so closing it breaks this instance too.
    /// Anything that wants a lifetime of its own should be given <see cref="Clone"/>'s result
    /// instead, whose handle it may close freely.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public SafeHandle UnsafeGetHandle()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        return _handle;
    }

    /// <summary>
    /// Closes this handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Closes this handle alone. Handles derived from it are unaffected and stay usable; so
    /// does the handle it was itself derived from.
    /// </para>
    /// <para>
    /// Not asynchronous, and no <see cref="IAsyncDisposable"/> to go with it: closing a
    /// descriptor does not wait for anything, so an asynchronous form would add a state
    /// machine to a call that completes immediately.
    /// </para>
    /// </remarks>
    public void Dispose() => _handle.Dispose();

    /// <summary>
    /// Opens a root handle with an explicit resolution policy.
    /// </summary>
    /// <remarks>
    /// The policy a subtree is resolved under is fixed when its root is opened, and every
    /// handle derived from that root copies it unchanged. Widening it is not expressible:
    /// derivation copies the field and nothing else writes it, so a component handed a
    /// handle cannot grant itself a looser policy than the one it was given — which is the
    /// only arrangement under which the word "policy" means anything.
    /// </remarks>
    internal static CapError OpenRootCore(
        string path,
        AmbientAuthority authority,
        ConfinedResolveOptions options,
        out Dir? dir)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        authority.Demand(nameof(authority));

        dir = null;

        // Read rather than traversal alone: a handle a caller was given in order to work in
        // a directory is expected to be able to list it, and narrowing that is a choice for
        // whoever opens the root to make rather than a default to impose on them.
        CapResult<SafeDirHandle> opened = PlatformOps.Current.OpenAmbientDirectory(path, CapAccess.Read);
        if (!opened.IsSuccess)
        {
            return opened.Error;
        }

        dir = new Dir(opened.Value, options);
        return CapError.Success;
    }

    /// <summary>The policy this handle resolves under, for a handle derived from it to copy.</summary>
    internal ConfinedResolveOptions Options => _options;

    private CapPathError OpenDirCore(string path, out Dir? dir, out CapError error)
    {
        ArgumentNullException.ThrowIfNull(path);
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        dir = null;
        error = CapError.Success;

        // Refusing `..` here rather than walking it is the rule for every path arriving from
        // a caller. The resolvers beneath can take an upward step safely, by moving back
        // through a handle they already hold, but a path that asks to climb above the root
        // is one this handle has no answer for, and the refusal is more useful than a
        // resolution that happens to stay inside.
        if (!CapPath.TryParse(path, out CapPath parsed, out CapPathError pathError))
        {
            return pathError;
        }

        CapResult<SafeDirHandle> opened = Resolver.OpenDirectory(_handle, in parsed, CapAccess.Read, _options);
        if (!opened.IsSuccess)
        {
            error = opened.Error;
            return CapPathError.None;
        }

        dir = new Dir(opened.Value, _options);
        return CapPathError.None;
    }

    private CapError CloneCore(out Dir? clone)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        clone = null;

        CapResult<SafeDirHandle> copy = PlatformOps.Current.DuplicateDirectory(_handle);
        if (!copy.IsSuccess)
        {
            return copy.Error;
        }

        clone = new Dir(copy.Value, _options);
        return CapError.Success;
    }
}
