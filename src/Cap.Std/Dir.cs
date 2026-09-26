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
/// <strong>The symbolic-link policy is part of the capability.</strong> A handle carries
/// what resolution beneath it does with a link met on the way to the thing a path names,
/// the value is chosen when a root is opened, and every handle derived from it inherits it.
/// <see cref="Restrict"/> can hand on a stricter one; nothing can hand on a looser one. So
/// the rule a subtree is read under travels with the authority to read it, and cannot be
/// changed by the code that was given both.
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
/// as that operation throwing <see cref="ObjectDisposedException"/> — the same answer it
/// would have had if the disposal had come first — and never as a call landing on an
/// unrelated object that has taken the handle's number. This is worth stating because the
/// opposite assumption is the usual one for a disposable type holding a native resource.
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
public sealed partial class Dir : IDir
{
    private readonly SafeDirHandle _handle;
    private readonly ConfinedResolveOptions _options;

    private Dir(SafeDirHandle handle, ConfinedResolveOptions options)
    {
        _handle = handle;
        _options = options;
    }

    /// <summary>
    /// The implementation every operation on this handle goes through: the one that issued
    /// it, which every handle derived from it shares.
    /// </summary>
    private IPlatformOps Ops => _handle.Backend;

    /// <summary>
    /// The handle itself, whichever backend issued it, for the library's own tests to ask that
    /// backend about it.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="UnsafeGetHandle"/> this also gives out a handle on a filesystem held in
    /// memory, since what it gives out is only ever handed back to the backend that issued it.
    /// </remarks>
    internal SafeDirHandle Handle => _handle;

    /// <summary>
    /// What resolution beneath this handle does with a symbolic link on the way to the thing
    /// a path names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Readable so that a component handed a handle can find out what it was given rather
    /// than having to discover it from a refusal. There is no setter: the value is fixed
    /// when the handle is created, and <see cref="Restrict"/> produces a new handle rather
    /// than changing this one, so a handle that has been passed on cannot have its policy
    /// altered underneath its holder.
    /// </para>
    /// <para>
    /// This says nothing about the last component of a path. Whether an operation acts on a
    /// link or on what it points at is decided by the operation — removing a name removes
    /// the name — whatever this reports.
    /// </para>
    /// </remarks>
    public SymlinkPolicy SymlinkPolicy => _options.ToSymlinkPolicy();

    /// <summary>
    /// The implementation this process resolves paths through beneath every handle opened
    /// from the host's filesystem: what <see cref="Open(string, AmbientAuthority, SymlinkPolicy)"/> gives you.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The backends keep the same containment promise and differ in how much of it holds
    /// while the tree is being changed underneath them.
    /// <see cref="Cap.Primitives.ResolutionBackend.ConfinedOpen"/> resolves a whole path in
    /// one kernel operation, so nothing can be substituted part-way through.
    /// <see cref="Cap.Primitives.ResolutionBackend.PortableWalk"/> and
    /// <see cref="Cap.Primitives.ResolutionBackend.WindowsRelativeOpen"/> open one name at a
    /// time against the handle the last step produced: resolution still cannot leave the
    /// tree, but someone able to write inside it can, with the right timing, steer an
    /// operation to a different object that is also inside it.
    /// </para>
    /// <para>
    /// Settled once, the first time anything is resolved, and fixed for the life of the
    /// process; reading it before then settles it. It describes the host rather than any one
    /// handle, which is why it is static. A handle from another filesystem, such as a
    /// simulated one in a test, reports its own through <see cref="Backend"/>. On Linux the kernel-atomic backend can be
    /// unavailable — an old kernel, or a syscall filter such as a container runtime's — and a
    /// process that depends on it should check this at start-up and refuse to run on
    /// anything else, rather than discover the difference from a report.
    /// </para>
    /// <para>
    /// The same answer, and counts of the opens each backend performs, are published as
    /// instruments on the <c>Cap.Primitives</c> meter, for a process watched from outside.
    /// </para>
    /// <para>Safe to read from any thread.</para>
    /// </remarks>
    public static ResolutionBackend ResolutionBackend => ResolutionMetrics.ActiveBackend;

    /// <summary>
    /// The implementation paths beneath this handle are resolved through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a handle on the host's filesystem, which is every handle
    /// <see cref="Open(string, AmbientAuthority, SymlinkPolicy)"/> and the handles derived from it produce,
    /// this is the same as <see cref="ResolutionBackend"/>. A handle on a filesystem that
    /// lives in memory reports <see cref="Cap.Primitives.ResolutionBackend.InMemory"/>
    /// instead, and so does every handle derived from it: a handle stays on the filesystem
    /// it came from.
    /// </para>
    /// <para>
    /// Two handles on different backends cannot be used together. Moving or linking an entry
    /// from one to the other fails as a move across devices would, before either backend is
    /// asked to do anything.
    /// </para>
    /// <para>Fixed for the life of the handle, and safe to read from any thread, even after
    /// the handle has been disposed.</para>
    /// </remarks>
    public ResolutionBackend Backend => Ops.Capabilities.Backend;

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
    /// <param name="policy">
    /// What resolution beneath the returned handle does with a symbolic link it meets on the
    /// way to the thing a path names. Travels with that handle and with everything derived
    /// from it; see <see cref="SymlinkPolicy"/>.
    /// </param>
    /// <returns>A handle on the directory.</returns>
    /// <remarks>
    /// <para>
    /// The one call in this type that is not confined to anything, and the only one that can
    /// produce a handle from nothing. Everything the containment guarantee promises begins
    /// after it returns: this step is exposed to whatever the host's own path resolution is
    /// exposed to, and a caller that resolves an attacker-controlled string here has handed
    /// over the sandbox before it existed.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> <paramref name="path"/> is resolved by the host,
    /// which follows a link anywhere in it, the last component included, wherever the link
    /// points. <paramref name="policy"/> plays no part in this step; it governs only
    /// resolution beneath the handle that comes back.
    /// </para>
    /// <para>Safe to call from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty, or <paramref name="authority"/> was never acquired.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="policy"/> is not a value the enumeration defines.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">There is no such directory.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the open.</exception>
    /// <exception cref="CapIOException">The open failed for another reason.</exception>
    public static Dir Open(
        string path,
        AmbientAuthority authority,
        SymlinkPolicy policy = SymlinkPolicy.FollowWithinSandbox) =>
        OpenThrough(PlatformOps.Host, path, authority, policy);

    /// <summary>
    /// Opens a directory by path through <paramref name="backend"/>, as
    /// <see cref="Open(string, AmbientAuthority, SymlinkPolicy)"/> does through the host.
    /// </summary>
    internal static Dir OpenThrough(
        IPlatformOps backend,
        string path,
        AmbientAuthority authority,
        SymlinkPolicy policy = SymlinkPolicy.FollowWithinSandbox)
    {
        CapError error = OpenRootCore(backend, path, authority, Demand(policy), out Dir? dir);
        return error.IsSuccess ? dir! : throw FailureTranslation.ToException(error, path);
    }

    /// <summary>
    /// Opens a directory by an ordinary path, reporting failure rather than throwing.
    /// </summary>
    /// <param name="path">The directory to open. See <see cref="Open"/>.</param>
    /// <param name="authority">An acquired ambient-authority token.</param>
    /// <param name="dir">The handle, when this returns true.</param>
    /// <param name="policy">The symbolic-link policy the subtree is resolved under.</param>
    /// <returns>True when the directory was opened.</returns>
    /// <remarks>
    /// <para>
    /// A missing directory is an expected answer rather than an exceptional one, and building
    /// an exception to say so costs more than the open. Arguments that are wrong rather than
    /// unlucky — a null path, a token that was never acquired, a policy that is not one of
    /// the defined values — still throw, because no retry or fallback can be the right
    /// response to any of them.
    /// </para>
    /// <para>
    /// Symbolic links in <paramref name="path"/> are followed by the host, as
    /// <see cref="Open"/> describes. Safe to call from any thread.
    /// </para>
    /// </remarks>
    public static bool TryOpen(
        string path,
        AmbientAuthority authority,
        [NotNullWhen(true)] out Dir? dir,
        SymlinkPolicy policy = SymlinkPolicy.FollowWithinSandbox) =>
        OpenRootCore(PlatformOps.Host, path, authority, Demand(policy), out dir).IsSuccess;

    /// <summary>
    /// Opens a directory beneath this one.
    /// </summary>
    /// <param name="path">
    /// A relative path of one or more components. Absolute paths and paths naming a drive or
    /// a network location are refused: none of them names something this handle covers. A
    /// <c>..</c> component is resolved beneath this handle, as a step back to the directory
    /// the walk came from, and refused if it would climb above this directory.
    /// </param>
    /// <param name="noFollow">
    /// Whether a symbolic link at the last component is refused rather than followed, as
    /// <c>O_NOFOLLOW</c> asks of a POSIX open. It changes nothing about links before the last
    /// component, and nothing about this handle's policy or the policy of the handle returned.
    /// </param>
    /// <returns>A handle on the directory, owning its own open object.</returns>
    /// <remarks>
    /// <para>
    /// Resolution is confined to the subtree this handle was opened on. Where the kernel can
    /// resolve the whole path in one operation that cannot leave it, it does; elsewhere the
    /// path is walked a component at a time against handles already held, refusing to follow
    /// any link the policy does not allow and refusing any step that would climb out. Both
    /// answer the same way for the same tree.
    /// </para>
    /// <para>
    /// <strong><c>..</c> is walked, never collapsed.</strong> <c>a/../b</c> enters <c>a</c>,
    /// steps back out of it and enters <c>b</c>; if <c>a</c> is a link, the step back lands
    /// beside wherever the link led, as it would for the operating system's own resolution.
    /// A <c>..</c> taken at this directory is refused with <see cref="SandboxEscapeException"/>
    /// whatever follows it, even when the rest of the path would lead back inside: the step
    /// itself is the one this handle grants no authority for. A path that ends in <c>..</c>
    /// names the directory the walk climbed back to, and opens it.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> A link met on the way is followed only under
    /// <see cref="Cap.Primitives.SymlinkPolicy.FollowWithinSandbox"/>, and only while its
    /// target stays beneath this handle; one that leaves — an absolute target, or one that
    /// climbs above this directory — is refused with <see cref="SandboxEscapeException"/>.
    /// Under <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> every link is refused with
    /// <see cref="CapIOException"/>, wherever it points. The last component is treated the
    /// same way as the others, because opening a link is opening what it names: a link to a
    /// directory inside the subtree opens that directory under the default policy and is
    /// refused under the stricter one.
    /// </para>
    /// <para>
    /// With <paramref name="noFollow"/> set, a link at the last component is refused with
    /// <see cref="CapIOException"/> under either policy, wherever it points, while links
    /// before it are treated as above. That is the question "is this name a directory, rather
    /// than something that leads to one", asked without having to restrict the handle, which
    /// would also refuse every link on the way and would pass the restriction on to every
    /// handle derived from the result. A path ending in a separator asks for what a final link
    /// leads to, and follows it even then, as POSIX resolution does.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">There is no such directory.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the open.</exception>
    /// <exception cref="CapIOException">
    /// A symbolic link the policy will not follow is in the way, a component is not a
    /// directory, or the open failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public Dir OpenDir(string path, bool noFollow = false)
    {
        CapPathError pathError = OpenDirCore(path, out Dir? dir, out CapError error, followFinalLink: !noFollow);
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
    /// <para>
    /// False covers every reason the path did not open, containment refusals included. An
    /// application that audits escape attempts should call <see cref="OpenDir"/> and catch
    /// <see cref="SandboxEscapeException"/>; this overload deliberately reports no reason,
    /// so that the failure path builds nothing.
    /// </para>
    /// <para>
    /// Symbolic links, the last component included, are followed or refused exactly as
    /// <see cref="OpenDir"/> describes; a refusal is reported as false. Safe to call
    /// concurrently with any other member of this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryOpenDir(string path, [NotNullWhen(true)] out Dir? dir) =>
        TryOpenDir(path, noFollow: false, out dir);

    /// <summary>
    /// Opens a directory beneath this one, choosing whether a final symbolic link is
    /// followed, and reports failure rather than throwing.
    /// </summary>
    /// <param name="path">A relative path. See <see cref="OpenDir"/>.</param>
    /// <param name="noFollow">
    /// Whether a symbolic link at the last component is refused. See <see cref="OpenDir"/>.
    /// </param>
    /// <param name="dir">The handle, when this returns true.</param>
    /// <returns>True when the directory was opened.</returns>
    /// <remarks>
    /// Symbolic links are followed or refused exactly as <see cref="OpenDir"/> describes for
    /// the same <paramref name="noFollow"/>, and every refusal, containment included, is
    /// reported as false. Safe to call concurrently with any other member of this handle, from
    /// any thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryOpenDir(string path, bool noFollow, [NotNullWhen(true)] out Dir? dir) =>
        OpenDirCore(path, out dir, out CapError error, followFinalLink: !noFollow) == CapPathError.None &&
        error.IsSuccess;

    /// <summary>
    /// Creates a directory beneath this one, and opens it.
    /// </summary>
    /// <param name="path">
    /// A relative path. Every component but the last must already exist; the last must not.
    /// </param>
    /// <returns>A handle on the new directory, carrying this handle's policy.</returns>
    /// <remarks>
    /// <para>
    /// Fails if the name is already taken, by anything at all — a directory, a file, a
    /// symbolic link. The name is what is being claimed, so what currently holds it does not
    /// change the answer, and a caller that wants the directory whether or not it was there
    /// already should say so by calling <see cref="OpenOrCreateDir"/> rather than by
    /// catching this.
    /// </para>
    /// <para>
    /// The directory is made, and then opened, which is two operations and not one. Nothing
    /// guarantees that the handle returned refers to the directory this call created: between
    /// the two, something else holding the same subtree could remove that name and put
    /// another directory there. What is guaranteed is that the handle refers to a directory
    /// beneath this one, because the second step refuses to follow a link and refuses to
    /// leave — so the worst case is a handle on somebody else's directory inside the same
    /// sandbox, and never a handle on anything outside it.
    /// </para>
    /// <para>
    /// Permissions are the system's to decide. The directory is asked for with the same
    /// permissions any other program's directory creation asks for, which the process umask
    /// then narrows; a capability bounds what can be reached and is not a substitute for the
    /// filesystem's own access control.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> A link met before the last component is followed or
    /// refused under this handle's <see cref="SymlinkPolicy"/> exactly as
    /// <see cref="OpenDir"/> describes — refused with <see cref="SandboxEscapeException"/> if
    /// its target leaves the subtree, and with <see cref="CapIOException"/> under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/>. The last component is never followed,
    /// under either policy: a link already holding the name, whatever it points at and
    /// whether or not its target exists, makes the name taken, and nothing is created where
    /// it points.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the new one is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the creation.</exception>
    /// <exception cref="CapIOException">The name is taken, or the creation failed otherwise.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public Dir CreateDir(string path) =>
        Produce(
            CreateDirCore(
                path,
                exclusive: true,
                CreationVisibility.SystemDefault,
                out Dir? dir,
                out CapError error,
                out ExpectedTarget expected),
            path,
            dir,
            error,
            expected);

    /// <summary>
    /// Creates a directory beneath this one, reporting failure rather than throwing.
    /// </summary>
    /// <param name="path">A relative path. See <see cref="CreateDir"/>.</param>
    /// <param name="dir">A handle on the new directory, when this returns true.</param>
    /// <returns>True when the directory was created.</returns>
    /// <remarks>
    /// <para>
    /// False covers every reason it was not created, a name already taken included. A caller
    /// that needs to tell those apart wants <see cref="CreateDir"/>; this form deliberately
    /// reports no reason, so that the failure path builds no message and no exception.
    /// </para>
    /// <para>
    /// Symbolic links are treated exactly as <see cref="CreateDir"/> describes: followed or
    /// refused on the way by this handle's policy, never followed at the last component, and
    /// a refusal is reported as false. Safe to call concurrently with any other member of
    /// this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryCreateDir(string path, [NotNullWhen(true)] out Dir? dir) =>
        Succeeded(
            CreateDirCore(
                path, exclusive: true, CreationVisibility.SystemDefault, out dir, out CapError error, out _),
            error);

    /// <summary>
    /// Opens a directory beneath this one, creating it if it is not there.
    /// </summary>
    /// <param name="path">A relative path. Every component but the last must already exist.</param>
    /// <returns>A handle on the directory, carrying this handle's policy.</returns>
    /// <remarks>
    /// <para>
    /// For the common case of making sure a directory is there, where whether this call or an
    /// earlier one put it there is of no interest. It is not a way to create a whole chain of
    /// directories: only the last component is created, and a missing one above it is
    /// reported as missing. Creating a chain means deciding what to do about the ones already
    /// made when a later one fails, which is a policy a caller should choose rather than
    /// inherit.
    /// </para>
    /// <para>
    /// The name being taken by something that is not a directory is still a failure. So is a
    /// symbolic link there, whatever it points at, because the open that follows refuses to
    /// follow one — which is what stops this from being a way to have a link decide where the
    /// caller's directory really is.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> A link met before the last component is followed or
    /// refused under this handle's <see cref="SymlinkPolicy"/> exactly as
    /// <see cref="OpenDir"/> describes — refused with <see cref="SandboxEscapeException"/> if
    /// its target leaves the subtree, and with <see cref="CapIOException"/> under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/>. A link holding the last component is
    /// refused with <see cref="CapIOException"/> under either policy, as above. That makes
    /// this stricter than <see cref="OpenDir"/>, which follows a link to a directory inside
    /// the subtree under the default policy.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above this one is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the operation.</exception>
    /// <exception cref="CapIOException">The name is held by something that is not a directory.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public Dir OpenOrCreateDir(string path) =>
        Produce(
            CreateDirCore(
                path,
                exclusive: false,
                CreationVisibility.SystemDefault,
                out Dir? dir,
                out CapError error,
                out ExpectedTarget expected),
            path,
            dir,
            error,
            expected);

    /// <summary>
    /// Opens or creates a directory beneath this one, reporting failure rather than throwing.
    /// </summary>
    /// <param name="path">A relative path. See <see cref="OpenOrCreateDir"/>.</param>
    /// <param name="dir">A handle on the directory, when this returns true.</param>
    /// <returns>True when the directory is there and was opened.</returns>
    /// <remarks>
    /// Symbolic links are treated exactly as <see cref="OpenOrCreateDir"/> describes: followed
    /// or refused on the way by this handle's policy, refused at the last component under
    /// either policy, and a refusal is reported as false. Safe to call concurrently with any
    /// other member of this handle, from any thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryOpenOrCreateDir(string path, [NotNullWhen(true)] out Dir? dir) =>
        Succeeded(
            CreateDirCore(
                path, exclusive: false, CreationVisibility.SystemDefault, out dir, out CapError error, out _),
            error);

    /// <summary>
    /// Removes a name beneath this handle. The name must not be a directory.
    /// </summary>
    /// <param name="path">A relative path to the name to remove.</param>
    /// <remarks>
    /// <para>
    /// <strong>It removes the name, never what the name points at.</strong> A symbolic link
    /// here is unlinked itself; whatever it refers to is not touched, and is not even looked
    /// up — so this works, and does the same thing, on a link pointing anywhere at all,
    /// including outside the subtree. That is deliberate and it is the only reading that is
    /// safe: a caller clearing out a directory of untrusted content must be able to remove
    /// the links in it, and a removal that followed them would be a way to make the caller
    /// delete a file somewhere else.
    /// </para>
    /// <para>
    /// For the same reason this keeps working under the strictest symbolic-link policy. What
    /// the policy governs is whether resolution walks <em>through</em> a link on the way to
    /// something else; whether the last component is acted on or followed is fixed by the
    /// operation, and this one acts on it.
    /// </para>
    /// <para>
    /// A name that is not there is a failure rather than a silent success. The framework is
    /// of two minds about this — removing a missing file succeeds there while removing a
    /// missing directory does not — and copying that inconsistency into a new API would only
    /// spread it. A caller that genuinely does not care whether it was there calls
    /// <see cref="TryDeleteFile"/>, which costs nothing.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> A link met before the last component is a different
    /// matter: it is followed or refused under this handle's <see cref="SymlinkPolicy"/>
    /// exactly as <see cref="OpenDir"/> describes — refused with
    /// <see cref="SandboxEscapeException"/> if its target leaves the subtree, and with
    /// <see cref="CapIOException"/> under <see cref="Cap.Primitives.SymlinkPolicy.Deny"/>.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no such name.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above it is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the removal.</exception>
    /// <exception cref="CapIOException">The name is a directory, or removal failed otherwise.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public void DeleteFile(string path) =>
        Complete(
            DeleteCore(path, directory: false, out CapError error, out ExpectedTarget expected),
            path,
            nameof(path),
            error,
            expected);

    /// <summary>
    /// Removes a name beneath this handle, reporting failure rather than throwing.
    /// </summary>
    /// <param name="path">A relative path to the name to remove. See <see cref="DeleteFile"/>.</param>
    /// <returns>True when the name was removed by this call.</returns>
    /// <remarks>
    /// <para>
    /// False when there was nothing there as well as when the removal was refused, so this is
    /// not quite "make sure it is gone": a caller that wants that treats false as success
    /// once it has satisfied itself the name is absent. The two are kept apart because some
    /// callers audit deletions and need to know which ones did something.
    /// </para>
    /// <para>
    /// Symbolic links are treated exactly as <see cref="DeleteFile"/> describes: a link at the
    /// last component is removed itself under either policy, one on the way is followed or
    /// refused by this handle's policy, and a refusal is reported as false. Safe to call
    /// concurrently with any other member of this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryDeleteFile(string path) =>
        Succeeded(DeleteCore(path, directory: false, out CapError error, out _), error);

    /// <summary>
    /// Removes a directory beneath this handle. It must be empty.
    /// </summary>
    /// <param name="path">A relative path to the directory to remove.</param>
    /// <remarks>
    /// <para>
    /// Emptiness is the filesystem's judgement, made as part of the removal, not a count
    /// taken beforehand — anything added between a count and a removal would be destroyed by
    /// a call that had satisfied itself there was nothing to destroy.
    /// </para>
    /// <para>
    /// A symbolic link that points at a directory is not a directory here, and is refused.
    /// Removing it is removing a name, which is <see cref="DeleteFile"/>'s business, and the
    /// distinction matters: if a link counted as the directory it names, a link planted in
    /// place of an empty directory would redirect a removal to somewhere else's contents.
    /// </para>
    /// <para>
    /// There is no recursive form. Removing a tree means walking it, and a walk that is safe
    /// under a sandbox has to descend by handles and remove by name at each level rather than
    /// rebuild paths — which makes it a walk the caller drives, over an enumeration, and not
    /// something a single call can be quietly allowed to do.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> The link at the last component is refused as above,
    /// under either policy, and is never followed. A link met before the last component is
    /// followed or refused under this handle's <see cref="SymlinkPolicy"/> exactly as
    /// <see cref="OpenDir"/> describes — refused with <see cref="SandboxEscapeException"/> if
    /// its target leaves the subtree, and with <see cref="CapIOException"/> under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/>.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no such name.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above it is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the removal.</exception>
    /// <exception cref="CapIOException">
    /// The directory is not empty, the name is not a directory, or removal failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public void DeleteDir(string path) =>
        Complete(
            DeleteCore(path, directory: true, out CapError error, out ExpectedTarget expected),
            path,
            nameof(path),
            error,
            expected);

    /// <summary>
    /// Removes an empty directory beneath this handle, reporting failure rather than throwing.
    /// </summary>
    /// <param name="path">A relative path to the directory. See <see cref="DeleteDir"/>.</param>
    /// <returns>True when the directory was removed by this call.</returns>
    /// <remarks>
    /// Symbolic links are treated exactly as <see cref="DeleteDir"/> describes: a link at the
    /// last component is refused rather than followed, one on the way is followed or refused
    /// by this handle's policy, and every refusal is reported as false. Safe to call
    /// concurrently with any other member of this handle, from any thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryDeleteDir(string path) =>
        Succeeded(DeleteCore(path, directory: true, out CapError error, out _), error);

    /// <summary>
    /// Moves an entry to a name beneath another handle, or beneath this one.
    /// </summary>
    /// <param name="from">A relative path, beneath this handle, to the entry to move.</param>
    /// <param name="toDir">
    /// The handle the destination name is beneath. May be this one. Must be a <see cref="Dir"/>
    /// on the same filesystem as this one; any other <see cref="IDir"/> is refused as a move
    /// across devices.
    /// </param>
    /// <param name="to">A relative path, beneath <paramref name="toDir"/>, to give it.</param>
    /// <param name="replaceExisting">
    /// Whether an entry already holding the destination name is replaced. False by default,
    /// so the destructive reading is never the one a caller gets by not thinking about it.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>Both ends are capabilities.</strong> The destination is named by a handle and
    /// a path beneath it rather than by a path from the source, so joining two places
    /// together requires authority over both — which is the correct reading of what a move
    /// is. Passing this handle as <paramref name="toDir"/> is the ordinary case and is not a
    /// special one.
    /// </para>
    /// <para>
    /// <strong>Replacement, when asked for, is atomic.</strong> There is no instant in which
    /// neither name resolves, which is what makes writing to a temporary name and then moving
    /// it into place a way to publish a file without ever exposing a partial one. Refusal,
    /// when not asked for, is equally part of the one operation: it is not a check followed
    /// by a move, because something appearing between those two would be destroyed by the
    /// very call that was told not to destroy anything. A filesystem that cannot make that
    /// refusal part of the move reports so rather than falling back to the check.
    /// </para>
    /// <para>
    /// <strong>A move between filesystems fails.</strong> It is not quietly turned into a
    /// copy and a delete: that has different timing, different failure modes, and for a hard
    /// link or a device node a different result altogether, and doing it under the name of a
    /// move would silently remove the atomicity callers rely on.
    /// </para>
    /// <para>
    /// The entry moved is the name, not what it points at. A symbolic link is moved as
    /// itself, with its stored target untouched — which, for a relative target, means it may
    /// name something different once it has arrived.
    /// </para>
    /// <para>
    /// A path ending in a separator, on either end, asks for a directory, and the move is
    /// refused when the entry is anything else — a symbolic link to a directory included,
    /// since the link is what would be moved.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> Neither last component is followed, under either
    /// policy: a link at <paramref name="from"/> is moved as itself, and a link already
    /// holding <paramref name="to"/> is a name like any other — it makes the destination
    /// taken, or is replaced when replacement is asked for, and what it points at is not
    /// touched. On Windows a link to a directory, whether a directory symbolic link or a
    /// junction, is a directory entry that the filesystem will not move anything over, so
    /// replacing one fails with a <see cref="CapIOException"/> whose kind is
    /// <see cref="CapErrorKind.SymbolicLink"/>. It is not moved aside to make room: that would
    /// leave a moment in which the name holds nothing. A link met before the last component of either path is followed or refused
    /// under the policy of the handle that path is resolved against, exactly as
    /// <see cref="OpenDir"/> describes — refused with <see cref="SandboxEscapeException"/> if
    /// its target leaves that handle's subtree, and with <see cref="CapIOException"/> under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/>.
    /// </para>
    /// <para>
    /// Safe to call concurrently with any other member of this handle or of
    /// <paramref name="toDir"/>, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">A path, or <paramref name="toDir"/>, is null.</exception>
    /// <exception cref="ArgumentException">A path is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// A path named something outside the authority of the handle it was used against.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is nothing at <paramref name="from"/>.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above either name is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the move.</exception>
    /// <exception cref="CapIOException">
    /// The destination is taken and replacement was not asked for, the two names are on
    /// different filesystems or <paramref name="toDir"/> is not a <see cref="Dir"/>, on
    /// Windows a link to a directory holds the destination, or the
    /// move failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Either handle has been disposed.</exception>
    public void Rename(string from, IDir toDir, string to, bool replaceExisting = false)
    {
        CapError error = LinkCore(
            from, toDir, to, rename: true, replaceExisting, followLink: false,
            out CapPathError fromError, out CapPathError toError, out ExpectedTarget expected);

        ThrowForPaths(fromError, from, nameof(from), toError, to, nameof(to));
        if (error.IsFailure)
        {
            throw FailureTranslation.ToException(error, from, expected);
        }
    }

    /// <summary>
    /// Moves an entry to a name beneath another handle, reporting failure rather than throwing.
    /// </summary>
    /// <param name="from">A relative path to the entry to move. See <see cref="Rename"/>.</param>
    /// <param name="toDir">The handle the destination name is beneath.</param>
    /// <param name="to">The name to give it beneath that handle.</param>
    /// <param name="replaceExisting">Whether an existing destination is replaced.</param>
    /// <returns>True when the entry was moved.</returns>
    /// <remarks>
    /// Symbolic links are treated exactly as <see cref="Rename"/> describes: neither last
    /// component is followed, a link on the way is followed or refused by the policy of the
    /// handle that path is resolved against, and a refusal is reported as false. Safe to call
    /// concurrently with any other member of this handle or of <paramref name="toDir"/>, from
    /// any thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A path, or <paramref name="toDir"/>, is null.</exception>
    /// <exception cref="ObjectDisposedException">Either handle has been disposed.</exception>
    public bool TryRename(string from, IDir toDir, string to, bool replaceExisting = false)
    {
        CapError error = LinkCore(
            from, toDir, to, rename: true, replaceExisting, followLink: false,
            out CapPathError fromError, out CapPathError toError, out _);

        return fromError == CapPathError.None && toError == CapPathError.None && error.IsSuccess;
    }

    /// <summary>
    /// Creates a symbolic link to a file beneath this handle.
    /// </summary>
    /// <param name="linkPath">A relative path naming the link to create.</param>
    /// <param name="target">
    /// The text the link stores, kept exactly as given. Must be relative: a rooted target is
    /// refused.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>The target is text, not a path this call resolves.</strong> It is stored as
    /// written: not required to exist, not rewritten. Containment is enforced where a link is
    /// followed — a stored target that leaves the subtree is refused by resolution under every
    /// policy — but a link persists on disk, where programs that are not confined to this
    /// handle, such as a shell, a backup job or a web server serving the same tree, follow it
    /// wherever it points.
    /// </para>
    /// <para>
    /// <strong>A rooted target is refused</strong> with <see cref="SandboxEscapeException"/>,
    /// and nothing is created: <c>/etc</c>, and on Windows also <c>C:\dir</c>,
    /// <c>C:dir</c>, <c>\dir</c> and network or device paths. Such a target names
    /// somewhere outside the subtree from wherever the link sits, so there is no link it
    /// could make that anything beneath the handle would follow. Rootedness is read by the
    /// running platform's path rules, the same ones resolution applies to a link it meets.
    /// </para>
    /// <para>
    /// <strong>A relative target that climbs out is stored.</strong> Whether <c>../x</c>
    /// leaves the subtree depends on where the link sits, and a rename beneath the handle can
    /// later move it somewhere the same text does, so refusing it at creation would promise
    /// something no check here can keep. A caller that must not leave such links for other
    /// programs to find has to decide which targets it accepts itself.
    /// </para>
    /// <para>
    /// Creation is unaffected by <see cref="SymlinkPolicy"/>: the policy governs whether a
    /// link is followed, and creating one follows nothing. A handle that refuses every link
    /// can still make one, as it can still remove one.
    /// </para>
    /// <para>
    /// <strong>Windows records which kind of link this is, and this is the file kind.</strong>
    /// A link made as the wrong kind there cannot be traversed at all, by this library or by
    /// anything else, and cannot be corrected in place. Use <see cref="CreateDirSymlink"/>
    /// for a link that names a directory. Everywhere else links are untyped and the two
    /// members do the same thing, which is exactly why the choice has to be made in portable
    /// code rather than discovered on the platform that cares.
    /// </para>
    /// <para>
    /// Creating a link may require a privilege the process does not hold — on Windows it
    /// historically always did, and still does outside developer mode — which is reported as
    /// the filesystem refusing the operation.
    /// </para>
    /// <para>
    /// <strong>Symbolic links in <paramref name="linkPath"/>.</strong> The last component is
    /// never followed: a link already holding the name, wherever it points, makes the name
    /// taken. A link met before the last component is followed or refused under this
    /// handle's <see cref="SymlinkPolicy"/> exactly as <see cref="OpenDir"/> describes —
    /// refused with <see cref="SandboxEscapeException"/> if its target leaves the subtree,
    /// and with <see cref="CapIOException"/> under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/>.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">A path is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="linkPath"/> is not a usable name, or <paramref name="target"/> is empty or
    /// contains a NUL character.
    /// </exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="linkPath"/> named something outside this handle's authority, or
    /// <paramref name="target"/> is rooted.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the link is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the creation.</exception>
    /// <exception cref="CapIOException">
    /// The name is taken, the filesystem has no symbolic links, or creation failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public void CreateSymlink(string linkPath, string target) =>
        CompleteSymlink(
            SymlinkCore(linkPath, target, targetIsDirectory: false, out CapError error, out ExpectedTarget expected, out bool rootedTarget),
            linkPath,
            target,
            error,
            expected,
            rootedTarget);

    /// <summary>
    /// Creates a symbolic link to a file, reporting failure rather than throwing.
    /// </summary>
    /// <param name="linkPath">A relative path naming the link. See <see cref="CreateSymlink"/>.</param>
    /// <param name="target">The text the link stores.</param>
    /// <returns>True when the link was created.</returns>
    /// <remarks>
    /// Symbolic links in <paramref name="linkPath"/> are treated exactly as
    /// <see cref="CreateSymlink"/> describes: the last component is never followed, a link on
    /// the way is followed or refused by this handle's policy, and a refusal is reported as
    /// false. A rooted target is refused as <see cref="CreateSymlink"/> describes, and reported
    /// as false. Safe to call concurrently with any other member of this handle, from any thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A path is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="target"/> is empty or contains a NUL character.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryCreateSymlink(string linkPath, string target) =>
        Succeeded(SymlinkCore(linkPath, target, targetIsDirectory: false, out CapError error, out _, out _), error);

    /// <summary>
    /// Creates a symbolic link to a directory beneath this handle.
    /// </summary>
    /// <param name="linkPath">A relative path naming the link to create.</param>
    /// <param name="target">The text the link stores, kept exactly as given.</param>
    /// <remarks>
    /// <para>
    /// The directory-kind counterpart of <see cref="CreateSymlink"/>, and everything said
    /// there about the stored target applies unchanged: a rooted target is refused, and a
    /// relative one is stored as given. The two are separate members because
    /// Windows records the kind in the link and will not traverse one made as the wrong kind;
    /// on every other platform a link has no kind and these do the same thing. Choosing
    /// between them in portable code is therefore not pedantry — it is the only way the
    /// choice gets made before the platform that cares is reached.
    /// </para>
    /// <para>
    /// Symbolic links in <paramref name="linkPath"/> are treated exactly as
    /// <see cref="CreateSymlink"/> describes: the last component is never followed, and a
    /// link already holding it makes the name taken; a link on the way is followed or
    /// refused by this handle's policy. Safe to call concurrently with any other member of
    /// this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">A path is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="linkPath"/> is not a usable name, or <paramref name="target"/> is empty or
    /// contains a NUL character.
    /// </exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="linkPath"/> named something outside this handle's authority, or
    /// <paramref name="target"/> is rooted.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the link is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the creation.</exception>
    /// <exception cref="CapIOException">
    /// The name is taken, the filesystem has no symbolic links, or creation failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public void CreateDirSymlink(string linkPath, string target) =>
        CompleteSymlink(
            SymlinkCore(linkPath, target, targetIsDirectory: true, out CapError error, out ExpectedTarget expected, out bool rootedTarget),
            linkPath,
            target,
            error,
            expected,
            rootedTarget);

    /// <summary>
    /// Creates a symbolic link to a directory, reporting failure rather than throwing.
    /// </summary>
    /// <param name="linkPath">A relative path naming the link. See <see cref="CreateDirSymlink"/>.</param>
    /// <param name="target">The text the link stores.</param>
    /// <returns>True when the link was created.</returns>
    /// <remarks>
    /// Symbolic links in <paramref name="linkPath"/> are treated exactly as
    /// <see cref="CreateSymlink"/> describes, and a refusal, including of a rooted target, is
    /// reported as false. Safe to call concurrently with any other member of this handle, from
    /// any thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A path is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="target"/> is empty or contains a NUL character.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryCreateDirSymlink(string linkPath, string target) =>
        Succeeded(SymlinkCore(linkPath, target, targetIsDirectory: true, out CapError error, out _, out _), error);

    /// <summary>
    /// Gives an existing entry a second name, beneath another handle or beneath this one.
    /// </summary>
    /// <param name="path">A relative path, beneath this handle, to the entry to name again.</param>
    /// <param name="toDir">
    /// The handle the new name is beneath. May be this one. Must be a <see cref="Dir"/> on the
    /// same filesystem as this one; any other <see cref="IDir"/> is refused as a link across
    /// devices.
    /// </param>
    /// <param name="to">A relative path, beneath <paramref name="toDir"/>, for the new name.</param>
    /// <param name="followLink">
    /// Whether a symbolic link at <paramref name="path"/> is followed, so that the second
    /// name is for what the link leads to rather than for the link, as <c>linkat</c> does
    /// when asked to follow.
    /// </param>
    /// <remarks>
    /// <para>
    /// Both ends are capabilities, for the reason a move's are: one object reachable by two
    /// names is a join between two places, and making one should require authority over
    /// both. A hard link created into a handle held by somebody else makes the object
    /// reachable through their subtree for as long as the name survives, which is a transfer
    /// of reach and not merely a convenience.
    /// </para>
    /// <para>
    /// By default the name is taken as written. A hard link to a symbolic link is a second
    /// name for the link, not for whatever it points at. With <paramref name="followLink"/>
    /// set, a link at <paramref name="path"/> is followed as a link on the way would be —
    /// while it stays beneath this handle, through any further links, refused with
    /// <see cref="SandboxEscapeException"/> once it leaves and with
    /// <see cref="CapIOException"/> under <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> —
    /// and the second name is given to the entry the chain ends at. That entry is linked by
    /// name, in the directory confined resolution found it in, so the link is made without
    /// opening it.
    /// </para>
    /// <para>
    /// This never replaces anything: a second name for an object is always a new name, so
    /// there is no option to overwrite and a destination already in use is a failure.
    /// Directories cannot be linked on any filesystem this runs on, and filesystems that do
    /// not support hard links at all report so. On Windows that reaches one step further: a
    /// symbolic link made as the directory kind, and a junction, are directory entries there,
    /// so giving the link itself a second name is refused as naming a directory — where on
    /// Unix, links being untyped, the link gets one.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> Neither last component is followed unless
    /// <paramref name="followLink"/> asks for the one at <paramref name="path"/> to be: a link
    /// there gets the second name itself, as above, and a link already holding
    /// <paramref name="to"/> makes the new name taken, whatever is asked. A link met before
    /// the last component of either path is followed or refused under the policy of the
    /// handle that path is resolved against, exactly as <see cref="OpenDir"/> describes —
    /// refused with <see cref="SandboxEscapeException"/> if its target leaves that handle's
    /// subtree, and with <see cref="CapIOException"/> under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/>.
    /// </para>
    /// <para>
    /// Safe to call concurrently with any other member of this handle or of
    /// <paramref name="toDir"/>, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">A path, or <paramref name="toDir"/>, is null.</exception>
    /// <exception cref="ArgumentException">A path is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// A path named something outside the authority of the handle it was used against.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is nothing at <paramref name="path"/>.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above either name is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the operation.</exception>
    /// <exception cref="CapIOException">
    /// The new name is taken, the two names are on different filesystems or
    /// <paramref name="toDir"/> is not a <see cref="Dir"/>, the entry is a
    /// directory, the filesystem has no hard links, or the operation failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Either handle has been disposed.</exception>
    public void CreateHardLink(string path, IDir toDir, string to, bool followLink = false)
    {
        CapError error = LinkCore(
            path, toDir, to, rename: false, replaceExisting: false, followLink,
            out CapPathError fromError, out CapPathError toError, out ExpectedTarget expected);

        ThrowForPaths(fromError, path, nameof(path), toError, to, nameof(to));
        if (error.IsFailure)
        {
            throw FailureTranslation.ToException(error, path, expected);
        }
    }

    /// <summary>
    /// Gives an existing entry a second name, reporting failure rather than throwing.
    /// </summary>
    /// <param name="path">A relative path to the entry. See <see cref="CreateHardLink"/>.</param>
    /// <param name="toDir">The handle the new name is beneath.</param>
    /// <param name="to">The new name beneath that handle.</param>
    /// <param name="followLink">
    /// Whether a symbolic link at <paramref name="path"/> is followed. See
    /// <see cref="CreateHardLink"/>.
    /// </param>
    /// <returns>True when the second name was created.</returns>
    /// <remarks>
    /// Symbolic links are treated exactly as <see cref="CreateHardLink"/> describes: neither
    /// last component is followed unless <paramref name="followLink"/> asks for the source's
    /// to be, a link on the way is followed or refused by the policy of the handle that path
    /// is resolved against, and a refusal is reported as false. Safe to
    /// call concurrently with any other member of this handle or of <paramref name="toDir"/>,
    /// from any thread.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A path, or <paramref name="toDir"/>, is null.</exception>
    /// <exception cref="ObjectDisposedException">Either handle has been disposed.</exception>
    public bool TryCreateHardLink(string path, IDir toDir, string to, bool followLink = false)
    {
        CapError error = LinkCore(
            path, toDir, to, rename: false, replaceExisting: false, followLink,
            out CapPathError fromError, out CapPathError toError, out _);

        return fromError == CapPathError.None && toError == CapPathError.None && error.IsSuccess;
    }

    /// <summary>
    /// Reports whether a name beneath this handle is taken.
    /// </summary>
    /// <param name="path">A relative path to the name to ask about.</param>
    /// <returns>True when there is an entry of that name.</returns>
    /// <remarks>
    /// <para>
    /// <strong>It asks about the name, and does not follow a link that holds it.</strong> A
    /// symbolic link is an entry, so a link counts as present whether its target exists,
    /// does not exist, or lies outside the subtree entirely. This differs from the
    /// framework's path-based test, which answers about what a name leads to, and the
    /// difference shows up exactly on a link that dangles.
    /// </para>
    /// <para>
    /// The reason to answer this question rather than the other one is that this one has a
    /// stable answer. Whether a name leads anywhere depends on the symbolic-link policy of
    /// the handle it is asked through, so the same name would exist through one handle and
    /// not through another; whether the name is taken does not. It also matches every other
    /// member here, all of which act on the name they are given.
    /// </para>
    /// <para>
    /// A path spelled so that it must name a directory — one ending in a separator — is
    /// present only if a directory is what holds the name.
    /// </para>
    /// <para>
    /// There is no <c>Try</c> form, because this is one: it already answers with a value and
    /// builds nothing on the way to saying no. False covers a missing name, a missing
    /// directory above it, a refusal on containment grounds and a refusal by the filesystem
    /// alike. Anything that needs those told apart is asking a different question and should
    /// use the operation it actually intends.
    /// </para>
    /// <para>
    /// <strong>Symbolic links on the way.</strong> A link met before the last component is
    /// followed or refused under this handle's <see cref="SymlinkPolicy"/> exactly as
    /// <see cref="OpenDir"/> describes. A refusal — a target that leaves the subtree, or any
    /// link at all under <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> — answers false, so
    /// under the stricter policy a name reachable only through a link is reported absent.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool Exists(string path)
    {
        CapPathError pathError = Locate(path, out NameLookup lookup, out CapError error, describing: true);
        using (lookup)
        {
            if (pathError != CapPathError.None || error.IsFailure)
            {
                return false;
            }

            if (lookup.NamesDirectoryItself)
            {
                return true;
            }

            CapError stat = Ops.StatChild(lookup.Directory, lookup.Name, out CapNodeInfo info);
            return stat.IsSuccess && (!lookup.RequiresDirectory || info.Type == CapNodeType.Directory);
        }
    }

    /// <summary>
    /// Reads the target stored in a symbolic link beneath this handle.
    /// </summary>
    /// <param name="path">A relative path to the link.</param>
    /// <returns>The stored target, exactly as the filesystem holds it.</returns>
    /// <remarks>
    /// <para>
    /// The link is read rather than followed, which is the whole point: the answer is what
    /// the link says, and saying it reveals nothing about whether the place it names exists.
    /// </para>
    /// <para>
    /// <strong>What comes back is attacker-controlled data.</strong> Anything able to write
    /// inside the subtree can plant a link saying anything at all, so the result is a string
    /// to be inspected or logged and never a path to be joined onto something and reopened.
    /// Resolution parses it under the same rules as a caller's own path and refuses it if it
    /// leaves the subtree; a caller that re-derives a path from it has stepped outside that
    /// and is on its own.
    /// </para>
    /// <para>
    /// Works under every symbolic-link policy, including the one that refuses to follow any
    /// link. Refusing to walk through a link and refusing to say what it contains are
    /// different things, and a handle held precisely in order to audit the links in a subtree
    /// would be useless if the stricter policy took this away.
    /// </para>
    /// <para>
    /// That applies to the last component only. A link met before it is followed or refused
    /// under this handle's <see cref="SymlinkPolicy"/> exactly as <see cref="OpenDir"/>
    /// describes — refused with <see cref="SandboxEscapeException"/> if its target leaves the
    /// subtree, and with <see cref="CapIOException"/> under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/>.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no such name.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory above it is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the read.</exception>
    /// <exception cref="CapIOException">The name is not a symbolic link, or the read failed.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public string ReadLink(string path)
    {
        CapPathError pathError = ReadLinkCore(path, out string? target, out CapError error, out ExpectedTarget expected);
        if (pathError != CapPathError.None)
        {
            throw FailureTranslation.ToException(pathError, path, nameof(path));
        }

        return error.IsSuccess ? target! : throw FailureTranslation.ToException(error, path, expected);
    }

    /// <summary>
    /// Reads the target stored in a symbolic link, reporting failure rather than throwing.
    /// </summary>
    /// <param name="path">A relative path to the link. See <see cref="ReadLink"/>.</param>
    /// <param name="target">The stored target, when this returns true.</param>
    /// <returns>True when a link was read.</returns>
    /// <remarks>
    /// <para>
    /// False covers a name that is not a link at all, which is an ordinary answer to "is this
    /// a link, and what does it say?" and not worth an exception on a path that asks it of
    /// every entry in a directory.
    /// </para>
    /// <para>
    /// Symbolic links are treated exactly as <see cref="ReadLink"/> describes: the last
    /// component is read and never followed, under either policy, while a link on the way is
    /// followed or refused by this handle's policy, a refusal being reported as false. Safe
    /// to call concurrently with any other member of this handle, from any thread.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryReadLink(string path, [NotNullWhen(true)] out string? target) =>
        ReadLinkCore(path, out target, out CapError error, out _) == CapPathError.None && error.IsSuccess;

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
    /// would be a promotion dressed as a duplicate. The symbolic-link policy is copied
    /// unchanged along with it.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="CapIOException">The handle could not be duplicated.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public Dir Clone()
    {
        CapError error = CloneCore(out Dir? clone);
        FailureTranslation.ThrowIfClosed(error);
        return error.IsSuccess
            ? clone!
            : throw new CapIOException(FailureTranslation.KindOf(error.Category), $"The directory handle could not be duplicated. ({error})");
    }

    /// <summary>
    /// Produces a second handle on the same directory, reporting failure rather than throwing.
    /// </summary>
    /// <param name="clone">The copy, when this returns true.</param>
    /// <returns>True when the handle was duplicated.</returns>
    /// <remarks>
    /// <para>
    /// Duplication fails only when the process is out of handles, which is a condition a
    /// server may well want to shed load for rather than unwind a stack over.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryClone([NotNullWhen(true)] out Dir? clone) => CloneCore(out clone).IsSuccess;

    /// <summary>
    /// Produces a handle on the same directory that resolves under a stricter symbolic-link
    /// policy.
    /// </summary>
    /// <param name="policy">
    /// The policy the new handle resolves under. Must be at least as strict as this
    /// handle's; passing the policy this handle already has is allowed and yields a plain
    /// copy.
    /// </param>
    /// <returns>A handle carrying this handle's authority under the stricter policy.</returns>
    /// <remarks>
    /// <para>
    /// How a subtree is handed to code that should be held to a tighter rule than the code
    /// handing it over. The usual case is passing a directory to something that will read
    /// whatever an untrusted party has written into it, where a symbolic link is not part of
    /// the layout the format was ever supposed to contain.
    /// </para>
    /// <para>
    /// <strong>It can only tighten.</strong> Asking for a looser policy than this handle
    /// carries is refused rather than honoured or quietly ignored, and that refusal is what
    /// makes the policy worth stating: if a handle could be widened by deriving from it,
    /// whoever was given one in order to work inside a subtree could lift the restriction it
    /// came with in a single call. Every other way of deriving a handle —
    /// <see cref="OpenDir"/>, <see cref="Clone"/> — copies the policy unchanged, so this is
    /// the only place the value ever moves, and it moves in one direction.
    /// </para>
    /// <para>
    /// The result owns its own open directory, so it outlives this handle and can be closed
    /// without affecting it. It is a separate capability, not a view onto this one.
    /// </para>
    /// <para>
    /// Safe to call concurrently with any other member of this handle, from any thread. This
    /// handle's own policy is not changed, so an operation already running through it, or
    /// started later, resolves exactly as it would have.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="policy"/> is not a value the enumeration defines.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="policy"/> is looser than the policy this handle carries.
    /// </exception>
    /// <exception cref="CapIOException">The handle could not be duplicated.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public Dir Restrict(SymlinkPolicy policy)
    {
        CapError error = RestrictCore(policy, out Dir? restricted);
        FailureTranslation.ThrowIfClosed(error);
        return error.IsSuccess
            ? restricted!
            : throw new CapIOException(
                FailureTranslation.KindOf(error.Category),
                $"The directory handle could not be duplicated under the stricter policy. ({error})");
    }

    /// <summary>
    /// Produces a handle under a stricter symbolic-link policy, reporting failure rather
    /// than throwing.
    /// </summary>
    /// <param name="policy">The policy the new handle resolves under. See <see cref="Restrict"/>.</param>
    /// <param name="restricted">The handle, when this returns true.</param>
    /// <returns>True when the handle was produced.</returns>
    /// <remarks>
    /// <para>
    /// False means only that the process or the system is out of handles, which is a
    /// condition a server may prefer to shed load for rather than unwind a stack over. A
    /// policy looser than this handle's still throws: that is a mistake in the calling code,
    /// and a caller that treated it as a transient failure and retried would loop.
    /// </para>
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="policy"/> is not a value the enumeration defines.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="policy"/> is looser than the policy this handle carries.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryRestrict(SymlinkPolicy policy, [NotNullWhen(true)] out Dir? restricted) =>
        RestrictCore(policy, out restricted).IsSuccess;

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
    /// <para>Safe to call concurrently with any other member of this handle, from any thread.</para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="authority"/> was never acquired.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryGetPath(AmbientAuthority authority, [NotNullWhen(true)] out string? path)
    {
        authority.Demand(nameof(authority));
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        CapResult<string> result = Ops.GetHandlePath(_handle);
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
    /// <para>
    /// Safe to call from any thread. What is done with the handle afterwards is outside this
    /// type's reach: closing it, from any thread, disposes this instance for every other
    /// caller, which then sees <see cref="ObjectDisposedException"/>.
    /// </para>
    /// <para>
    /// <strong>Only for a directory on the host's filesystem.</strong> A directory on a
    /// filesystem held in memory has no descriptor or kernel handle, and the value that stands
    /// for one is an index into that filesystem's own table. Passed to the system, it would
    /// act on, or close, whichever real object has that number, so such a directory refuses
    /// instead.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The directory is not on the host's filesystem, so it has no operating-system handle.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public SafeHandle UnsafeGetHandle()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        if (!Ops.IssuesKernelHandles)
        {
            throw new NotSupportedException(
                "This directory is not on the host's filesystem, so it has no operating-system " +
                "handle to give out. Its handle value means something only to the filesystem " +
                "that issued it, and passed to the system it would reach whichever real object " +
                "has that number. Use the members of Dir instead.");
        }

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
    /// <para>
    /// Safe to call from any thread, more than once, and while other threads are using this
    /// handle. An operation already under way keeps the underlying handle alive until it
    /// finishes, and one that reaches it after the close throws
    /// <see cref="ObjectDisposedException"/>; none ever lands on an unrelated object that has
    /// since been given the same handle number. An enumeration already begun reads through an
    /// open object of its own and runs on, but the entries it goes on to yield can no longer
    /// be opened through this handle.
    /// </para>
    /// </remarks>
    public void Dispose() => _handle.Dispose();

    /// <summary>
    /// Opens a root handle with an explicit resolution policy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The policy a subtree is resolved under is fixed when its root is opened, and every
    /// handle derived from that root copies it unchanged. Widening it is not expressible:
    /// derivation copies the field, the only member that writes a different value writes a
    /// stricter one, and there is no setter — so a component handed a handle cannot grant
    /// itself a looser policy than the one it was given, which is the only arrangement
    /// under which the word "policy" means anything.
    /// </para>
    /// <para>
    /// Takes the resolution flags rather than the caller-facing policy because it is the
    /// seam the whole library opens roots through, and confinement carries more than the
    /// treatment of links.
    /// </para>
    /// <para>
    /// Takes the backend as well, which is the host for every public entry point. A test
    /// passes a simulated one to get a real handle on a simulated tree without replacing the
    /// host for the rest of the process; the handle, and everything derived from it, then
    /// resolves through that backend alone.
    /// </para>
    /// </remarks>
    internal static CapError OpenRootCore(
        IPlatformOps backend,
        string path,
        AmbientAuthority authority,
        ConfinedResolveOptions options,
        out Dir? dir)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentException.ThrowIfNullOrEmpty(path);
        authority.Demand(nameof(authority));

        dir = null;

        // Read rather than traversal alone: a handle a caller was given in order to work in
        // a directory is expected to be able to list it, and narrowing that is a choice for
        // whoever opens the root to make rather than a default to impose on them.
        CapResult<SafeDirHandle> opened = backend.OpenAmbientDirectory(path, CapAccess.Read);
        if (!opened.IsSuccess)
        {
            return opened.Error;
        }

        dir = new Dir(opened.Value, options);
        return CapError.Success;
    }

    /// <summary>
    /// Wraps a directory handle that a filesystem held in memory issued for one of its own
    /// directories, as the root of a tree under <paramref name="policy"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No <see cref="AmbientAuthority"/> token is asked for, which is what separates this from
    /// <see cref="OpenRootCore"/>. The token marks the point where authority over the host
    /// enters the capability graph, and a tree held in memory is not the host's: whoever holds
    /// it already has all of it, and a handle on part of it grants nothing over anything else.
    /// Asking for a token here would put a test's scaffolding in the ambient authority log
    /// beside the places a program really reaches outside itself.
    /// </para>
    /// <para>
    /// Refuses a handle from a backend that issues kernel handles, so that this cannot become
    /// a way round the token for the host's filesystem.
    /// </para>
    /// </remarks>
    internal static Dir FromInMemoryRoot(SafeDirHandle handle, SymlinkPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.Backend.IssuesKernelHandles)
        {
            throw new ArgumentException(
                "Only a handle on a filesystem held in memory can become a root without an ambient-authority token.",
                nameof(handle));
        }

        return new Dir(handle, Demand(policy));
    }

    /// <summary>The policy this handle resolves under, for a handle derived from it to copy.</summary>
    internal ConfinedResolveOptions Options => _options;

    /// <summary>
    /// Whether <paramref name="other"/> is on the same filesystem as this handle, so that the
    /// two can take part in one operation and their identities can be compared.
    /// </summary>
    internal bool SharesBackendWith(Dir other) => _handle.SharesBackendWith(other._handle);

    /// <summary>
    /// Opens a directory beneath this one without following a symbolic link anywhere on the
    /// way, the last component included, and hands it back under this handle's own policy.
    /// </summary>
    /// <param name="path">A relative path. See <see cref="OpenDir"/>.</param>
    /// <param name="dir">The handle, when this returns true.</param>
    /// <returns>True when a directory was opened; false for every refusal and failure.</returns>
    /// <remarks>
    /// <para>
    /// For a tree walk that has promised not to pass through links. Deciding that from the
    /// kind a directory read reported is not enough: the kind is a snapshot taken before the
    /// open, some filesystems do not report one at all, and a directory can be swapped for a
    /// link between the two. Refusing links in the open itself is the only answer that holds
    /// whatever happened in between.
    /// </para>
    /// <para>
    /// The resolution is stricter than this handle's policy, but the handle produced is not.
    /// That is not a widening: the new handle names a directory reached without a link, and
    /// carries exactly the policy it would have carried had <see cref="OpenDir"/> opened it.
    /// Returning it restricted would silently change what the walk's caller can do through
    /// the handles it is given, depending only on how deep they are.
    /// </para>
    /// </remarks>
    internal bool TryOpenDirRefusingLinks(string path, [NotNullWhen(true)] out Dir? dir) =>
        OpenDirCore(path, out dir, out CapError error, ConfinedResolveOptions.RefuseSymlinks) == CapPathError.None &&
        error.IsSuccess;

    /// <summary>
    /// Removes a name beneath this handle, reporting the platform's own answer.
    /// </summary>
    /// <param name="name">A single component.</param>
    /// <remarks>
    /// For the callers that have to tell one failure from another and must not pay for an
    /// exception to do it. The public members answer with an exception or with a bare
    /// <see langword="bool"/>; a walk that removes a tree needs the middle ground, because
    /// "that was the other kind of object" is something it acts on rather than reports.
    /// </remarks>
    internal CapError DeleteFileCore(string name)
    {
        CapPathError pathError = DeleteCore(name, directory: false, out CapError error, out _);
        return pathError == CapPathError.None ? error : CapError.FromCategory(CapErrorCategory.InvalidArgument);
    }

    /// <summary>
    /// Removes an empty directory beneath this handle, reporting the platform's own answer.
    /// </summary>
    /// <param name="name">A single component.</param>
    internal CapError DeleteDirCore(string name)
    {
        CapPathError pathError = DeleteCore(name, directory: true, out CapError error, out _);
        return pathError == CapPathError.None ? error : CapError.FromCategory(CapErrorCategory.InvalidArgument);
    }

    /// <summary>
    /// Clears whatever the platform records on an object itself to stop it being removed.
    /// </summary>
    /// <param name="name">A single component.</param>
    /// <remarks>
    /// Windows keeps a read-only flag on the object which refuses a deletion that the
    /// account is otherwise entitled to make. Clearing it is a separate step because it is
    /// worth taking only on a removal that has already failed, and it reports
    /// <see cref="CapErrorCategory.NotSupported"/> where there is no such flag to clear.
    /// </remarks>
    internal CapError ClearRemovalBlock(string name)
    {
        CapPathError pathError = Locate(name, out NameLookup lookup, out CapError error);
        using (lookup)
        {
            if (pathError != CapPathError.None)
            {
                return CapError.FromCategory(CapErrorCategory.InvalidArgument);
            }

            return error.IsFailure
                ? error
                : Ops.ClearChildRemovalBlock(lookup.Directory, lookup.Name);
        }
    }

    /// <summary>
    /// Creates a directory beneath this one that no other account can look into, and opens
    /// it.
    /// </summary>
    /// <param name="name">A single component, which must not already be taken.</param>
    /// <param name="dir">A handle on the new directory, on success.</param>
    /// <returns>
    /// The platform's answer, so that a caller can tell the name being taken from every
    /// other reason the creation did not happen.
    /// </returns>
    /// <remarks>
    /// Internal, and narrower than the public creation on purpose. Asking for permissions
    /// other than the system's own is right only where this library rather than the caller
    /// chose the location: a scratch directory, and the per-application directories placed
    /// by the platform's conventions.
    /// </remarks>
    internal CapError CreateOwnedDir(string name, out Dir? dir)
    {
        CapPathError pathError = CreateDirCore(
            name,
            exclusive: true,
            CreationVisibility.OwnerOnly,
            out dir,
            out CapError error,
            out _);

        return pathError == CapPathError.None ? error : CapError.FromCategory(CapErrorCategory.InvalidArgument);
    }

    /// <summary>
    /// Opens a directory beneath this one, creating it so that no other account can look
    /// into it if it is not there.
    /// </summary>
    /// <param name="name">A single component.</param>
    /// <returns>A handle on the directory, carrying this handle's policy.</returns>
    /// <remarks>
    /// <see cref="OpenOrCreateDir"/> with the owner-only mode of <see cref="CreateOwnedDir"/>,
    /// and the same restriction on who may ask for it. A directory already there is opened
    /// as it is: its permissions were somebody's decision, and this does not revisit it.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a usable name.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the operation.</exception>
    /// <exception cref="CapIOException">The name is held by something that is not a directory.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    internal Dir OpenOrCreateOwnedDir(string name) =>
        Produce(
            CreateDirCore(
                name,
                exclusive: false,
                CreationVisibility.OwnerOnly,
                out Dir? dir,
                out CapError error,
                out ExpectedTarget expected),
            name,
            dir,
            error,
            expected);

    /// <summary>
    /// Resolves everything ahead of a path's last component, and hands back that component
    /// with a handle of its own on the directory that holds it.
    /// </summary>
    /// <param name="path">A path beneath this handle, of one or more components.</param>
    /// <param name="holder">
    /// On success, a handle on the directory the last component belongs to. The caller owns
    /// it and must close it; it carries no more authority than this handle does.
    /// </param>
    /// <param name="name">On success, the last component, which has never been looked up.</param>
    /// <param name="error">
    /// What the resolution ran into, once the path itself was accepted. Reported separately
    /// from the return value because the two are different kinds of answer: one is about the
    /// string, the other about the filesystem, and they are turned into exceptions by
    /// different overloads.
    /// </param>
    /// <returns>What the parser made of the path, which is <see cref="CapPathError.None"/>
    /// whenever the string named something expressible beneath a handle — including when the
    /// resolution then failed.</returns>
    /// <remarks>
    /// <para>
    /// For the operations that cannot be expressed as an open at all. The rest of this type
    /// finishes a resolution by making one call against the confined directory itself, and
    /// so never needs to say where that directory is; an operation that has to hand the pair
    /// to something outside this assembly needs the directory to keep, because the borrowed
    /// one lives only as long as the resolution that produced it.
    /// </para>
    /// <para>
    /// This is the end of what confinement can promise and the start of what the caller must
    /// keep. The component comes back unresolved on purpose: the guarantee is that it names
    /// something directly inside the returned directory or nothing at all, and whatever
    /// consumes it has to preserve that by looking it up exactly once, in that directory,
    /// without following a link.
    /// </para>
    /// <para>
    /// A path spelled so that its target has to be a directory is refused, because nothing
    /// that needs this pair acts on a directory.
    /// </para>
    /// </remarks>
    internal CapPathError OpenNameHolder(
        string path,
        out SafeDirHandle? holder,
        out string? name,
        out CapError error)
    {
        holder = null;
        name = null;

        CapPathError pathError = Locate(path, out NameLookup lookup, out error);
        using (lookup)
        {
            if (pathError != CapPathError.None || error.IsFailure)
            {
                return pathError;
            }

            if (lookup.RequiresDirectory)
            {
                error = CapError.FromCategory(CapErrorCategory.IsADirectory);
                return CapPathError.None;
            }

            CapResult<SafeDirHandle> copy = Ops.DuplicateDirectory(lookup.Directory);
            if (!copy.IsSuccess)
            {
                error = copy.Error;
                return CapPathError.None;
            }

            holder = copy.Value;
            name = lookup.Name.ToString();
            return CapPathError.None;
        }
    }

    /// <summary>
    /// A directory a name is about to be used against, and that name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every operation that acts on a name rather than on an object works from one of these:
    /// resolution has confined the directory, and exactly one further call is made against it
    /// with one component that has been validated and never looked up.
    /// </para>
    /// <para>
    /// The directory is sometimes owned and sometimes borrowed, which is why this exists at
    /// all rather than the operations passing a pair around. A path of a single component is
    /// looked up in the handle the caller already holds, so there is nothing to resolve and
    /// nothing to close; anything longer resolves to a directory of its own that has to be.
    /// Disposing this is correct in both cases and closes only what was opened for it.
    /// </para>
    /// </remarks>
    private ref struct NameLookup
    {
        private ResolvedParent? _owned;
        private SafeDirHandle? _ownedDirectory;

        public NameLookup(
            ResolvedParent? owned,
            SafeDirHandle directory,
            ReadOnlySpan<char> name,
            bool requiresDirectory)
        {
            _owned = owned;
            _ownedDirectory = null;
            Directory = directory;
            Name = name;
            RequiresDirectory = requiresDirectory;
            NamesDirectoryItself = false;
        }

        private NameLookup(SafeDirHandle directory)
        {
            _owned = null;
            _ownedDirectory = directory;
            Directory = directory;
            Name = default;
            RequiresDirectory = true;
            NamesDirectoryItself = true;
        }

        /// <summary>
        /// A path that ended in <c>..</c>, resolved to the directory it climbed back to. There
        /// is no name in it to act on; <see cref="Directory"/> is the thing the path named.
        /// </summary>
        public static NameLookup ForDirectoryItself(SafeDirHandle directory) => new(directory);

        /// <summary>
        /// Whether the path ended in <c>..</c>, so that <see cref="Directory"/> is what it named
        /// and <see cref="Name"/> is empty.
        /// </summary>
        public bool NamesDirectoryItself { get; }

        /// <summary>The directory the name is used against.</summary>
        public SafeDirHandle Directory { get; }

        /// <summary>The single component the operation acts on.</summary>
        public ReadOnlySpan<char> Name { get; }

        /// <summary>
        /// Whether the path was spelled so that whatever holds the name has to be a
        /// directory: it ended in a separator.
        /// </summary>
        /// <remarks>
        /// Carried here because splitting a path into components is exactly what loses it,
        /// and every operation that does not act on a directory has to refuse such a path.
        /// </remarks>
        public bool RequiresDirectory { get; }

        /// <summary>Closes the directory, if this one owns it.</summary>
        public void Dispose()
        {
            _owned?.Dispose();
            _owned = null;
            _ownedDirectory?.Dispose();
            _ownedDirectory = null;
        }
    }

    /// <summary>
    /// Resolves everything ahead of a path's last component and hands back that component
    /// with the directory it belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one place every name-acting member starts, so that they cannot drift apart about
    /// what a path means. What they do with the pair differs; how they get to it does not.
    /// </para>
    /// <para>
    /// A single-component path takes a shortcut and is handed this handle itself. That is not
    /// only cheaper — it avoids duplicating a descriptor and, on the kernel-atomic backend,
    /// avoids a syscall — it is also what lets the whole common case run without allocating,
    /// which is what the failure-reporting overloads are for.
    /// </para>
    /// </remarks>
    private CapPathError Locate(
        string path,
        out NameLookup lookup,
        out CapError error,
        bool describing = false,
        bool followLastLink = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        lookup = default;
        error = CapError.Success;

        if (!TryParseCallerPath(path, out CapPath parsed, out CapPathError pathError))
        {
            return pathError;
        }

        if (!parsed.TrySplitLastComponent(out _, out _))
        {
            return CapPathError.Empty;
        }

        error = LocateParsed(in parsed, out lookup, describing);
        if (followLastLink && error.IsSuccess)
        {
            error = FollowLastLink(parsed, ref lookup);
        }

        return CapPathError.None;
    }

    /// <summary>
    /// The resolution half of <see cref="Locate"/>, for a path that has already been parsed
    /// and has at least one component.
    /// </summary>
    private CapError LocateParsed(scoped in CapPath parsed, out NameLookup lookup, bool describing)
    {
        lookup = default;

        if (!parsed.TrySplitLastComponent(out ReadOnlySpan<char> prefix, out ReadOnlySpan<char> name))
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        // A path ending in `..` names a directory by where it sits rather than by a name in
        // its parent, so there is no name for a create, a removal or a rename to act on. It is
        // still resolved in full first, so that one climbing above the handle is reported as
        // the escape it is and one through something missing as missing. Only describing the
        // directory, and asking whether it is there, can go on from it.
        if (name.SequenceEqual(".."))
        {
            CapResult<SafeDirHandle> reached = Resolver.OpenDirectory(_handle, in parsed, CapAccess.None, _options);
            if (!reached.IsSuccess)
            {
                return reached.Error;
            }

            if (!describing)
            {
                reached.Value.Dispose();
                return CapError.FromCategory(CapErrorCategory.InvalidArgument);
            }

            lookup = NameLookup.ForDirectoryItself(reached.Value);
            return CapError.Success;
        }

        if (prefix.IsEmpty)
        {
            lookup = new NameLookup(null, _handle, name, parsed.RequiresDirectory);
            return CapError.Success;
        }

        CapResult<ResolvedParent> parent = Resolver.ResolveParent(_handle, in parsed, _options);
        if (!parent.IsSuccess)
        {
            return parent.Error;
        }

        lookup = new NameLookup(
            parent.Value, parent.Value.Directory, parent.Value.Name, parsed.RequiresDirectory);

        return CapError.Success;
    }

    /// <summary>
    /// Turns a lookup whose name holds a symbolic link into one for whatever the link leads
    /// to, for a member asked to act on a final link's target rather than on the link.
    /// </summary>
    /// <param name="path">The path <paramref name="lookup"/> was resolved from.</param>
    /// <param name="lookup">
    /// The lookup to replace. On success it names something that is not a link — or nothing,
    /// when the chain ends at a name that is free — or is the directory a chain ended on.
    /// </param>
    /// <remarks>
    /// <para>
    /// The operations that act on a name — describing it, setting its times, giving it a
    /// second name — do so with one call that never follows a link, and that call is where
    /// they get their safety from. So a final link is not handed to the platform to follow.
    /// It is read here, its target is put in place of the last component, and the result is
    /// resolved from this handle again, by the same confined resolution as the caller's own
    /// path: a link's target cannot reach anywhere a written-out path could not, and a rooted
    /// target is refused as the escape it is. The chain is followed until a name holds
    /// something other than a link, under the same bound on the number of links the walk
    /// applies, and every link on it is subject to this handle's policy, so under
    /// <see cref="Cap.Primitives.SymlinkPolicy.Deny"/> a final link is refused rather than
    /// followed.
    /// </para>
    /// <para>
    /// Re-resolving from this handle rather than from the directory holding the link is what
    /// lets a target climb with <c>..</c> as far as this handle and no further, which is the
    /// same bound it would have had inside a longer path. It also means the steps are not one
    /// instant: something that can write in the tree can replace a name between the look at
    /// it and the operation on it. What the operation then meets is a name in a directory
    /// resolution confined, acted on without following, so the replacement can change which
    /// entry is acted on and never lead it out of the subtree.
    /// </para>
    /// </remarks>
    private CapError FollowLastLink(CapPath path, ref NameLookup lookup)
    {
        IPlatformOps ops = Ops;
        int budget = PortableResolver.MaxSymbolicLinks;

        while (!lookup.NamesDirectoryItself)
        {
            // A name that is free, or that cannot be looked at, is left for the operation to
            // meet: it reports what it finds, in the terms it would use without following.
            if (ops.StatChild(lookup.Directory, lookup.Name, out CapNodeInfo info).IsFailure)
            {
                return CapError.Success;
            }

            // A reparse point whose tag is not a filesystem link has nothing to follow that
            // means a place, and resolution refuses one it meets on the way; following one on
            // request is refused the same way rather than quietly acting on it as a name.
            if (info.Type == CapNodeType.UnknownReparsePoint)
            {
                return CapError.FromCategory(CapErrorCategory.Reparse);
            }

            if (info.Type != CapNodeType.SymbolicLink)
            {
                return CapError.Success;
            }

            if ((_options & ConfinedResolveOptions.RefuseSymlinks) != 0 || --budget < 0)
            {
                return CapError.FromCategory(CapErrorCategory.SymbolicLinkLoop);
            }

            CapResult<string> target = ops.ReadChildLink(lookup.Directory, lookup.Name);
            if (!target.IsSuccess)
            {
                // No longer a link by the time it was read: replaced in between, so the name
                // is looked at again. The budget bounds how often that can happen.
                if (target.Error.Category == CapErrorCategory.NotALink)
                {
                    continue;
                }

                return target.Error;
            }

            CapError joined = JoinLinkTarget(path, target.Value, out CapPath next);
            if (joined.IsFailure)
            {
                return joined;
            }

            lookup.Dispose();
            lookup = default;

            if (next.ComponentCount == 0)
            {
                // The target led back to this handle's own directory, which has no name
                // beneath it to act on.
                CapResult<SafeDirHandle> self = ops.DuplicateDirectory(_handle);
                if (!self.IsSuccess)
                {
                    return self.Error;
                }

                lookup = NameLookup.ForDirectoryItself(self.Value);
                return CapError.Success;
            }

            CapError located = LocateParsed(in next, out lookup, describing: true);
            if (located.IsFailure)
            {
                return located;
            }

            path = next;
        }

        return CapError.Success;
    }

    /// <summary>
    /// Puts a link's stored target in place of the last component of the path that reached
    /// the link.
    /// </summary>
    /// <remarks>
    /// The target is data written by whoever could write in the directory holding the link,
    /// so it is parsed by the same rules as a caller's path, and refused on the same grounds a
    /// link met by the walk is refused. A target of <c>.</c> names the directory holding the
    /// link, which is what the path is left naming once its last component is dropped.
    /// </remarks>
    private static CapError JoinLinkTarget(scoped in CapPath path, string target, out CapPath joined)
    {
        joined = default;
        path.TrySplitLastComponent(out ReadOnlySpan<char> prefix, out _);

        if (!CapPath.TryParse(target, path.Syntax, ParentLinkPolicy.Preserve, out _, out CapPathError error))
        {
            if (error != CapPathError.Empty || target.Length == 0)
            {
                return PortableResolver.TranslateLinkTarget(error);
            }

            target = string.Empty;
        }

        string combined = string.Concat(prefix, target);
        if (combined.Length == 0)
        {
            return CapError.Success;
        }

        if (!CapPath.TryParse(combined, path.Syntax, ParentLinkPolicy.Preserve, out joined, out error))
        {
            // Everything in the combination was accepted on its own, so what is left to refuse
            // is its length, or a prefix of nothing but `.` components.
            if (error == CapPathError.Empty)
            {
                joined = default;
                return CapError.Success;
            }

            return PortableResolver.TranslateLinkTarget(error);
        }

        return CapError.Success;
    }

    /// <summary>
    /// Parses a path arriving from a caller, carrying each <c>..</c> through to resolution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>..</c> is walked for real, beneath this handle: the resolvers step back through a
    /// directory handle they already hold, or let the kernel do so under a flag that confines
    /// it, and refuse a step that would climb above the handle as an escape. So
    /// <c>dir/nested/../file</c> names <c>dir/file</c>, and <c>dir/../../file</c> is refused.
    /// Nothing is collapsed as text: <c>link/../file</c> climbs from wherever the link led,
    /// which only the walk can know.
    /// </para>
    /// <para>
    /// Refusing <c>..</c> outright would buy nothing a caller could rely on. A symbolic link
    /// inside the subtree may already hold <c>..</c> in its target, and following one is the
    /// same walk under the same root test, so the confinement a written-out <c>..</c> is held
    /// to is the one every link is already held to.
    /// </para>
    /// <para>
    /// Read under the rules of the filesystem this handle is on, which for every handle on
    /// the host's filesystem are the running platform's. A filesystem held in memory can be
    /// told to read paths as another platform does, and the path has to be split where that
    /// filesystem splits it, or a name it would refuse could reach it.
    /// </para>
    /// </remarks>
    private bool TryParseCallerPath(string path, out CapPath parsed, out CapPathError error) =>
        CapPath.TryParse(path, PathSyntax, ParentLinkPolicy.Preserve, out parsed, out error);

    /// <summary>
    /// The rules a path handed to this handle is read under: the running platform's, unless
    /// the handle is on a filesystem that chose others.
    /// </summary>
    internal CapPathSyntax PathSyntax => Ops.Capabilities.PathSyntax;

    /// <summary>Turns a core's outcome into a handle or the exception explaining its absence.</summary>
    private static Dir Produce(CapPathError pathError, string path, Dir? dir, CapError error, ExpectedTarget expected)
    {
        if (pathError != CapPathError.None)
        {
            throw FailureTranslation.ToException(pathError, path, "path");
        }

        return error.IsSuccess ? dir! : throw FailureTranslation.ToException(error, path, expected);
    }

    /// <summary>Turns a core's outcome into nothing, or the exception explaining the failure.</summary>
    /// <remarks>
    /// Takes the parameter's name rather than deriving one, so that a refusal names the
    /// argument the caller actually wrote rather than whatever this helper calls it.
    /// </remarks>
    private static void Complete(
        CapPathError pathError,
        string path,
        string parameterName,
        CapError error,
        ExpectedTarget expected)
    {
        if (pathError != CapPathError.None)
        {
            throw FailureTranslation.ToException(pathError, path, parameterName);
        }

        if (error.IsFailure)
        {
            throw FailureTranslation.ToException(error, path, expected);
        }
    }

    /// <summary>
    /// Turns a link creation's outcome into nothing, or the exception explaining the failure.
    /// </summary>
    /// <remarks>
    /// A refused target is the one failure that is about the text stored rather than about
    /// the name the link was to be made at, so it is reported against the target.
    /// </remarks>
    private static void CompleteSymlink(
        CapPathError pathError,
        string linkPath,
        string target,
        CapError error,
        ExpectedTarget expected,
        bool rootedTarget)
    {
        if (rootedTarget)
        {
            throw new SandboxEscapeException(
                $"'{target}' was not stored as the target of '{linkPath}': it is rooted, so it " +
                $"names somewhere outside the directory the handle grants authority over from " +
                $"wherever the link sits. ({error})");
        }

        Complete(pathError, linkPath, nameof(linkPath), error, expected);
    }

    /// <summary>Whether a core's outcome was a success, for the reporting overloads.</summary>
    private static bool Succeeded(CapPathError pathError, CapError error) =>
        pathError == CapPathError.None && error.IsSuccess;

    /// <summary>
    /// Reports whichever of a two-ended operation's paths the parser refused, naming the
    /// parameter it arrived through.
    /// </summary>
    private static void ThrowForPaths(
        CapPathError fromError,
        string from,
        string fromParameter,
        CapPathError toError,
        string to,
        string toParameter)
    {
        if (fromError != CapPathError.None)
        {
            throw FailureTranslation.ToException(fromError, from, fromParameter);
        }

        if (toError != CapPathError.None)
        {
            throw FailureTranslation.ToException(toError, to, toParameter);
        }
    }

    /// <summary>
    /// Creates a directory and opens it, either refusing a name already taken or accepting it.
    /// </summary>
    /// <remarks>
    /// Written once for both members because they differ in exactly one place: whether the
    /// name already being there is a failure or the ordinary case. Everything else — where
    /// the name is resolved, how the new directory is opened, what the resulting handle
    /// carries — is identical, and two copies of it would eventually not be.
    /// </remarks>
    private CapPathError CreateDirCore(
        string path,
        bool exclusive,
        CreationVisibility visibility,
        out Dir? dir,
        out CapError error,
        out ExpectedTarget expected)
    {
        dir = null;
        expected = ExpectedTarget.Name;

        CapPathError pathError = Locate(path, out NameLookup lookup, out error);
        using (lookup)
        {
            if (pathError != CapPathError.None || error.IsFailure)
            {
                expected = ExpectedTarget.Directory;
                return pathError;
            }

            error = Ops.CreateChildDirectory(
                lookup.Directory, lookup.Name, visibility);
            if (error.IsFailure && (exclusive || error.Category != CapErrorCategory.AlreadyExists))
            {
                return CapPathError.None;
            }

            // Opened by name against the directory the walk confined, and the open refuses to
            // follow a link. So a name swapped for a link between the two steps is refused
            // rather than followed, and the worst a swap can achieve is a handle on some
            // other directory inside the same subtree.
            CapResult<SafeDirHandle> opened =
                Ops.OpenChildDirectory(lookup.Directory, lookup.Name, CapAccess.Read);

            if (!opened.IsSuccess)
            {
                error = opened.Error;
                return CapPathError.None;
            }

            error = CapError.Success;
            dir = new Dir(opened.Value, _options);
            return CapPathError.None;
        }
    }

    /// <summary>Removes a name, as an entry of any kind or as an empty directory.</summary>
    private CapPathError DeleteCore(
        string path,
        bool directory,
        out CapError error,
        out ExpectedTarget expected)
    {
        expected = ExpectedTarget.Name;

        CapPathError pathError = Locate(path, out NameLookup lookup, out error);
        using (lookup)
        {
            if (pathError != CapPathError.None || error.IsFailure)
            {
                expected = ExpectedTarget.Directory;
                return pathError;
            }

            // A trailing separator is a request that the target be a directory, which a
            // removal of anything else cannot satisfy. The distinction is carried by the
            // parser rather than rediscovered from the string, and it is dropped by the split
            // into components, so it has to be applied here or not at all. Nothing is
            // removed either way; the name is only looked at to say which refusal it is: a
            // directory where a file removal was asked for, something that is not the
            // directory the spelling promised, or nothing at all.
            if (!directory && lookup.RequiresDirectory)
            {
                error = RefuseAsDirectory(lookup.Directory, lookup.Name, existing: CapErrorCategory.IsADirectory);
                return CapPathError.None;
            }

            error = directory
                ? Ops.RemoveChildDirectory(lookup.Directory, lookup.Name)
                : Ops.RemoveChildFile(lookup.Directory, lookup.Name);

            // Asking the call that removes a name to remove a directory is refused everywhere,
            // but not in the same words: one platform answers that the name is a directory and
            // another that permission was denied. The second would send a caller looking for a
            // permissions problem that is not there, over a directory it can read perfectly
            // well, and it would stop a removal of a whole tree from noticing that its guess
            // about the kind was wrong and trying the other call. So the refusal is
            // reclassified afterwards by what the name holds, rather than anticipated by a
            // look before the removal, which would cost every successful removal a second call
            // and still leave the window between the two.
            //
            // The name is examined without following it, so a link to a directory is not
            // mistaken for one: removing a link is removing a name, which is what this call
            // does, and it would have succeeded.
            if (!directory &&
                error.Category == CapErrorCategory.PermissionDenied &&
                Ops.StatChild(lookup.Directory, lookup.Name, out CapNodeInfo refused).IsSuccess &&
                refused.Type == CapNodeType.Directory)
            {
                error = CapError.FromCategory(CapErrorCategory.IsADirectory);
            }

            return CapPathError.None;
        }
    }

    /// <summary>Creates a symbolic link of either kind.</summary>
    private CapPathError SymlinkCore(
        string linkPath,
        string target,
        bool targetIsDirectory,
        out CapError error,
        out ExpectedTarget expected,
        out bool rootedTarget)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        rootedTarget = false;

        // Refused here, as it is in a path, rather than left to the platform: the kernel reads
        // a target up to its first NUL, so the link would store less than it was given, and
        // whether the platform layer notices first is not something a caller should depend on.
        if (target.Contains((char)0))
        {
            throw new ArgumentException(
                "A symbolic link's target cannot contain a NUL character: it would be stored " +
                "cut short at that point, as a different target from the one given.",
                nameof(target));
        }

        expected = ExpectedTarget.Name;

        CapPathError pathError = Locate(linkPath, out NameLookup lookup, out error);
        using (lookup)
        {
            if (pathError != CapPathError.None || error.IsFailure)
            {
                expected = ExpectedTarget.Directory;
                return pathError;
            }

            // A link is not a directory, so a name spelled as one cannot be where it is made.
            // The link is never created under such a name; the name is only looked at to
            // report what POSIX reports: that it is missing, that a directory already holds
            // it, or that something other than a directory does.
            if (!targetIsDirectory && lookup.RequiresDirectory)
            {
                error = RefuseAsDirectory(lookup.Directory, lookup.Name, existing: CapErrorCategory.AlreadyExists);
                return CapPathError.None;
            }

            // A rooted target names somewhere from a root this handle confers no authority
            // over, wherever the link sits, so it is refused from its text alone, by the same
            // reading of "rooted" that resolution applies when it meets one. Resolution beneath
            // a handle would refuse to follow it anyway; the link is refused here because
            // other programs reading the same tree would not.
            if (CapPath.IsRooted(target, PathSyntax))
            {
                rootedTarget = true;
                error = CapError.FromCategory(CapErrorCategory.Escaped);
                return CapPathError.None;
            }

            error = Ops.CreateChildSymbolicLink(
                lookup.Directory, lookup.Name, target, targetIsDirectory);

            return CapPathError.None;
        }
    }

    /// <summary>
    /// The refusal for a name spelled with a trailing separator that an operation cannot act
    /// on as a directory, according to what the name holds.
    /// </summary>
    /// <param name="parent">The directory the name is beneath.</param>
    /// <param name="name">The name, without its separator.</param>
    /// <param name="existing">What to report when the name holds a directory.</param>
    /// <remarks>
    /// Only ever a refusal: the name is described without following it, and nothing is done
    /// to it, so what it holds changing in the meantime can alter which failure is reported
    /// but never lets the operation go ahead. A name holding anything other than a directory,
    /// a link included, is not the directory the spelling asked for.
    /// </remarks>
    private static CapError RefuseAsDirectory(SafeDirHandle parent, ReadOnlySpan<char> name, CapErrorCategory existing)
    {
        CapError described = parent.Backend.StatChild(parent, name, out CapNodeInfo info);
        if (described.IsFailure)
        {
            return described;
        }

        return CapError.FromCategory(
            info.Type == CapNodeType.Directory ? existing : CapErrorCategory.NotADirectory);
    }

    /// <summary>
    /// Joins a name beneath this handle to a name beneath another: a move, or a second name.
    /// </summary>
    /// <remarks>
    /// The two share everything but the final call. Each end is resolved against its own
    /// handle and under that handle's own policy, which is what makes the operation exactly
    /// as confined as the weaker of the two capabilities rather than as confined as whichever
    /// one the caller happened to start from.
    /// </remarks>
    private CapError LinkCore(
        string from,
        IDir toDir,
        string to,
        bool rename,
        bool replaceExisting,
        bool followLink,
        out CapPathError fromError,
        out CapPathError toError,
        out ExpectedTarget expected)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(toDir);
        ArgumentNullException.ThrowIfNull(to);

        fromError = CapPathError.None;
        toError = CapPathError.None;
        expected = ExpectedTarget.Name;

        // Two handles from different filesystems, such as a simulated tree and the disk, can
        // no more share an entry than two mounted volumes can, and the kernel refuses a rename
        // between those as a move across devices. This is refused the same way, before either
        // backend sees anything: a handle is meaningful only to the backend that issued it,
        // and handing one backend the other's handle as a destination would have it act on
        // whatever its own table holds under that number.
        //
        // A destination that is not a Dir at all, such as a test's stub of the interface, is
        // refused the same way and for a stronger reason: it holds no handle, so there is
        // nothing a backend could be given as the other end of the call.
        if (toDir is not Dir destinationDir || !_handle.SharesBackendWith(destinationDir._handle))
        {
            return CapError.FromCategory(CapErrorCategory.CrossDevice);
        }

        fromError = Locate(from, out NameLookup source, out CapError error, followLastLink: followLink);
        using (source)
        {
            if (fromError != CapPathError.None || error.IsFailure)
            {
                expected = ExpectedTarget.Directory;
                return error;
            }

            // A followed link can end at a directory reached by `..` or `.`, which is not a
            // name but a directory, and no filesystem this runs on gives a directory a second
            // name.
            if (source.NamesDirectoryItself)
            {
                return CapError.FromCategory(CapErrorCategory.IsADirectory);
            }

            toError = destinationDir.Locate(to, out NameLookup destination, out error);
            using (destination)
            {
                if (toError != CapPathError.None || error.IsFailure)
                {
                    expected = ExpectedTarget.Directory;
                    return error;
                }

                // A trailing separator on either end asks for the entry to be a directory, and
                // the platform call never sees it: the split into components drops it, so it is
                // applied here or not at all. The entry is looked at before it is moved, which
                // leaves a window in which a directory can be swapped for something else under
                // the same name. What slips through that window is a move of an entry beneath
                // the same handle the caller named, which is the move a caller without the
                // separator would have got, so nothing is reached that was not already in reach.
                if (source.RequiresDirectory || destination.RequiresDirectory)
                {
                    CapError described = Ops.StatChild(source.Directory, source.Name, out CapNodeInfo info);
                    if (described.IsFailure)
                    {
                        return described;
                    }

                    // A second name for a file, spelled as a directory, is refused by what that
                    // name holds, as it is when a link of any kind is made there: missing, a
                    // directory already, or something else that is not the directory the
                    // spelling promised.
                    if (!rename && !source.RequiresDirectory && info.Type != CapNodeType.Directory)
                    {
                        return RefuseAsDirectory(
                            destination.Directory, destination.Name, existing: CapErrorCategory.AlreadyExists);
                    }

                    // A directory cannot be given a second name on any filesystem this runs on,
                    // so for a link the only question left is which refusal to report.
                    if (info.Type != CapNodeType.Directory || !rename)
                    {
                        return CapError.FromCategory(
                            info.Type == CapNodeType.Directory ? CapErrorCategory.IsADirectory : CapErrorCategory.NotADirectory);
                    }
                }

                if (rename)
                {
                    return Ops.RenameChild(
                        source.Directory, source.Name, destination.Directory, destination.Name, replaceExisting);
                }

                CapError linked = Ops.CreateChildHardLink(
                    source.Directory, source.Name, destination.Directory, destination.Name);

                // Linux and macOS refuse a second name for a directory as a permission failure,
                // which would reach the caller as the filesystem refusing access to something it
                // can read perfectly well. The refusal is reclassified after the fact rather than
                // anticipated, so that the link itself is still one call. A volume with no hard
                // links refuses a directory the same way, and a directory is the more precise
                // answer there too, since no volume would have linked one.
                if (linked.Category is CapErrorCategory.PermissionDenied or CapErrorCategory.NotSupported &&
                    Ops.StatChild(source.Directory, source.Name, out CapNodeInfo refused).IsSuccess &&
                    refused.Type == CapNodeType.Directory)
                {
                    return CapError.FromCategory(CapErrorCategory.IsADirectory);
                }

                return linked;
            }
        }
    }

    /// <summary>Reads a link's stored target.</summary>
    private CapPathError ReadLinkCore(
        string path,
        out string? target,
        out CapError error,
        out ExpectedTarget expected)
    {
        target = null;
        expected = ExpectedTarget.Name;

        CapPathError pathError = Locate(path, out NameLookup lookup, out error);
        using (lookup)
        {
            if (pathError != CapPathError.None || error.IsFailure)
            {
                expected = ExpectedTarget.Directory;
                return pathError;
            }

            CapResult<string> read = Ops.ReadChildLink(lookup.Directory, lookup.Name);
            if (!read.IsSuccess)
            {
                error = read.Error;
                return CapPathError.None;
            }

            target = read.Value;
            return CapPathError.None;
        }
    }


    private CapPathError OpenDirCore(
        string path,
        out Dir? dir,
        out CapError error,
        ConfinedResolveOptions stricter = ConfinedResolveOptions.None,
        bool followFinalLink = true)
    {
        ArgumentNullException.ThrowIfNull(path);
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        dir = null;
        error = CapError.Success;

        if (!TryParseCallerPath(path, out CapPath parsed, out CapPathError pathError))
        {
            return pathError;
        }

        CapResult<SafeDirHandle> opened = Resolver.OpenDirectory(
            _handle,
            in parsed,
            CapAccess.Read,
            _options | stricter,
            followFinalLink);
        if (!opened.IsSuccess)
        {
            error = opened.Error;
            return CapPathError.None;
        }

        dir = new Dir(opened.Value, _options);
        return CapPathError.None;
    }

    /// <summary>
    /// Checks a caller-supplied policy and converts it to the flags resolution takes.
    /// </summary>
    /// <remarks>
    /// An undefined value is refused rather than mapped. The mapping reads anything it does
    /// not recognise as the default, and the default is the loosest policy, so a value that
    /// arrived by a bad cast or a stale constant would otherwise select the weakest
    /// behaviour without anybody being told.
    /// </remarks>
    private static ConfinedResolveOptions Demand(SymlinkPolicy policy)
    {
        if (!policy.IsDefinedValue())
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy,
                "The symbolic-link policy is not one of the defined values. It is refused " +
                "rather than treated as the default, because the default is the least " +
                "restrictive of them.");
        }

        return policy.ToResolveOptions();
    }

    private CapError RestrictCore(SymlinkPolicy policy, out Dir? restricted)
    {
        ConfinedResolveOptions requested = Demand(policy);
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        restricted = null;

        if (policy < SymlinkPolicy)
        {
            throw new ArgumentException(
                $"A handle resolving under {SymlinkPolicy} cannot produce one resolving " +
                $"under {policy}, which is less restrictive. Authority is only ever narrowed " +
                "by derivation; a caller that needs the looser policy has to have been given " +
                "a handle that already carries it.",
                nameof(policy));
        }

        CapResult<SafeDirHandle> copy = Ops.DuplicateDirectory(_handle);
        if (!copy.IsSuccess)
        {
            return copy.Error;
        }

        restricted = new Dir(copy.Value, _options | requested);
        return CapError.Success;
    }

    private CapError CloneCore(out Dir? clone)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        clone = null;

        CapResult<SafeDirHandle> copy = Ops.DuplicateDirectory(_handle);
        if (!copy.IsSuccess)
        {
            return copy.Error;
        }

        clone = new Dir(copy.Value, _options);
        return CapError.Success;
    }
}
