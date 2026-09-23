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
public sealed partial class Dir : IDisposable
{
    private readonly SafeDirHandle _handle;
    private readonly ConfinedResolveOptions _options;

    private Dir(SafeDirHandle handle, ConfinedResolveOptions options)
    {
        _handle = handle;
        _options = options;
    }

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
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="policy"/> is not a value the enumeration defines.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">There is no such directory.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the open.</exception>
    /// <exception cref="CapIOException">The open failed for another reason.</exception>
    public static Dir Open(
        string path,
        AmbientAuthority authority,
        SymlinkPolicy policy = SymlinkPolicy.FollowWithinSandbox)
    {
        CapError error = OpenRootCore(path, authority, Demand(policy), out Dir? dir);
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
    /// A missing directory is an expected answer rather than an exceptional one, and building
    /// an exception to say so costs more than the open. Arguments that are wrong rather than
    /// unlucky — a null path, a token that was never acquired, a policy that is not one of
    /// the defined values — still throw, because no retry or fallback can be the right
    /// response to any of them.
    /// </remarks>
    public static bool TryOpen(
        string path,
        AmbientAuthority authority,
        [NotNullWhen(true)] out Dir? dir,
        SymlinkPolicy policy = SymlinkPolicy.FollowWithinSandbox) =>
        OpenRootCore(path, authority, Demand(policy), out dir).IsSuccess;

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
    /// False covers every reason it was not created, a name already taken included. A caller
    /// that needs to tell those apart wants <see cref="CreateDir"/>; this form deliberately
    /// reports no reason, so that the failure path builds no message and no exception.
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
    /// False when there was nothing there as well as when the removal was refused, so this is
    /// not quite "make sure it is gone": a caller that wants that treats false as success
    /// once it has satisfied itself the name is absent. The two are kept apart because some
    /// callers audit deletions and need to know which ones did something.
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
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryDeleteDir(string path) =>
        Succeeded(DeleteCore(path, directory: true, out CapError error, out _), error);

    /// <summary>
    /// Moves an entry to a name beneath another handle, or beneath this one.
    /// </summary>
    /// <param name="from">A relative path, beneath this handle, to the entry to move.</param>
    /// <param name="toDir">The handle the destination name is beneath. May be this one.</param>
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
    /// different filesystems, or the move failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Either handle has been disposed.</exception>
    public void Rename(string from, Dir toDir, string to, bool replaceExisting = false)
    {
        CapError error = LinkCore(
            from, toDir, to, rename: true, replaceExisting,
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
    /// <exception cref="ArgumentNullException">A path, or <paramref name="toDir"/>, is null.</exception>
    /// <exception cref="ObjectDisposedException">Either handle has been disposed.</exception>
    public bool TryRename(string from, Dir toDir, string to, bool replaceExisting = false)
    {
        CapError error = LinkCore(
            from, toDir, to, rename: true, replaceExisting,
            out CapPathError fromError, out CapPathError toError, out _);

        return fromError == CapPathError.None && toError == CapPathError.None && error.IsSuccess;
    }

    /// <summary>
    /// Creates a symbolic link to a file beneath this handle.
    /// </summary>
    /// <param name="linkPath">A relative path naming the link to create.</param>
    /// <param name="target">The text the link stores, kept exactly as given.</param>
    /// <remarks>
    /// <para>
    /// <strong>The target is data, not a path this call resolves.</strong> It is stored as
    /// written: not checked against the subtree, not required to exist, not rewritten.
    /// Containment is enforced where a link is followed rather than where it is made — a
    /// stored target that leaves the subtree is refused by resolution under every policy, and
    /// refused from the text before anything is looked up, so nothing is gained by refusing
    /// it here. Refusing here would also refuse links that are perfectly good: whether a
    /// relative target escapes depends on where the link ends up, which is not knowable when
    /// it is created.
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
    /// </remarks>
    /// <exception cref="ArgumentNullException">A path is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="linkPath"/> is not a usable name, or <paramref name="target"/> is empty or
    /// contains a NUL character.
    /// </exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="linkPath"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the link is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the creation.</exception>
    /// <exception cref="CapIOException">
    /// The name is taken, the filesystem has no symbolic links, or creation failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public void CreateSymlink(string linkPath, string target) =>
        Complete(
            SymlinkCore(linkPath, target, targetIsDirectory: false, out CapError error, out ExpectedTarget expected),
            linkPath,
            nameof(linkPath),
            error,
            expected);

    /// <summary>
    /// Creates a symbolic link to a file, reporting failure rather than throwing.
    /// </summary>
    /// <param name="linkPath">A relative path naming the link. See <see cref="CreateSymlink"/>.</param>
    /// <param name="target">The text the link stores.</param>
    /// <returns>True when the link was created.</returns>
    /// <exception cref="ArgumentNullException">A path is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="target"/> is empty or contains a NUL character.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryCreateSymlink(string linkPath, string target) =>
        Succeeded(SymlinkCore(linkPath, target, targetIsDirectory: false, out CapError error, out _), error);

    /// <summary>
    /// Creates a symbolic link to a directory beneath this handle.
    /// </summary>
    /// <param name="linkPath">A relative path naming the link to create.</param>
    /// <param name="target">The text the link stores, kept exactly as given.</param>
    /// <remarks>
    /// The directory-kind counterpart of <see cref="CreateSymlink"/>, and everything said
    /// there about the stored target applies unchanged. The two are separate members because
    /// Windows records the kind in the link and will not traverse one made as the wrong kind;
    /// on every other platform a link has no kind and these do the same thing. Choosing
    /// between them in portable code is therefore not pedantry — it is the only way the
    /// choice gets made before the platform that cares is reached.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A path is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="linkPath"/> is not a usable name, or <paramref name="target"/> is empty or
    /// contains a NUL character.
    /// </exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="linkPath"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the link is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the creation.</exception>
    /// <exception cref="CapIOException">
    /// The name is taken, the filesystem has no symbolic links, or creation failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public void CreateDirSymlink(string linkPath, string target) =>
        Complete(
            SymlinkCore(linkPath, target, targetIsDirectory: true, out CapError error, out ExpectedTarget expected),
            linkPath,
            nameof(linkPath),
            error,
            expected);

    /// <summary>
    /// Creates a symbolic link to a directory, reporting failure rather than throwing.
    /// </summary>
    /// <param name="linkPath">A relative path naming the link. See <see cref="CreateDirSymlink"/>.</param>
    /// <param name="target">The text the link stores.</param>
    /// <returns>True when the link was created.</returns>
    /// <exception cref="ArgumentNullException">A path is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="target"/> is empty or contains a NUL character.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool TryCreateDirSymlink(string linkPath, string target) =>
        Succeeded(SymlinkCore(linkPath, target, targetIsDirectory: true, out CapError error, out _), error);

    /// <summary>
    /// Gives an existing entry a second name, beneath another handle or beneath this one.
    /// </summary>
    /// <param name="path">A relative path, beneath this handle, to the entry to name again.</param>
    /// <param name="toDir">The handle the new name is beneath. May be this one.</param>
    /// <param name="to">A relative path, beneath <paramref name="toDir"/>, for the new name.</param>
    /// <remarks>
    /// <para>
    /// Both ends are capabilities, for the reason a move's are: one object reachable by two
    /// names is a join between two places, and making one should require authority over
    /// both. A hard link created into a handle held by somebody else makes the object
    /// reachable through their subtree for as long as the name survives, which is a transfer
    /// of reach and not merely a convenience.
    /// </para>
    /// <para>
    /// The name is taken as written. A hard link to a symbolic link is a second name for the
    /// link, not for whatever it points at.
    /// </para>
    /// <para>
    /// This never replaces anything: a second name for an object is always a new name, so
    /// there is no option to overwrite and a destination already in use is a failure.
    /// Directories cannot be linked on any filesystem this runs on, and filesystems that do
    /// not support hard links at all report so.
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
    /// The new name is taken, the two names are on different filesystems, the entry is a
    /// directory, or the operation failed otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Either handle has been disposed.</exception>
    public void CreateHardLink(string path, Dir toDir, string to)
    {
        CapError error = LinkCore(
            path, toDir, to, rename: false, replaceExisting: false,
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
    /// <returns>True when the second name was created.</returns>
    /// <exception cref="ArgumentNullException">A path, or <paramref name="toDir"/>, is null.</exception>
    /// <exception cref="ObjectDisposedException">Either handle has been disposed.</exception>
    public bool TryCreateHardLink(string path, Dir toDir, string to)
    {
        CapError error = LinkCore(
            path, toDir, to, rename: false, replaceExisting: false,
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
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public bool Exists(string path)
    {
        CapPathError pathError = Locate(path, out NameLookup lookup, out CapError error);
        using (lookup)
        {
            if (pathError != CapPathError.None || error.IsFailure)
            {
                return false;
            }

            CapError stat = PlatformOps.Current.StatChild(lookup.Directory, lookup.Name, out CapNodeInfo info);
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
    /// False covers a name that is not a link at all, which is an ordinary answer to "is this
    /// a link, and what does it say?" and not worth an exception on a path that asks it of
    /// every entry in a directory.
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
        return error.IsSuccess
            ? restricted!
            : throw new CapIOException(
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
    /// False means only that the process or the system is out of handles, which is a
    /// condition a server may prefer to shed load for rather than unwind a stack over. A
    /// policy looser than this handle's still throws: that is a mistake in the calling code,
    /// and a caller that treated it as a transient failure and retried would loop.
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
                : PlatformOps.Current.ClearChildRemovalBlock(lookup.Directory, lookup.Name);
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

            CapResult<SafeDirHandle> copy = PlatformOps.Current.DuplicateDirectory(lookup.Directory);
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

        public NameLookup(
            ResolvedParent? owned,
            SafeDirHandle directory,
            ReadOnlySpan<char> name,
            bool requiresDirectory)
        {
            _owned = owned;
            Directory = directory;
            Name = name;
            RequiresDirectory = requiresDirectory;
        }

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
    private CapPathError Locate(string path, out NameLookup lookup, out CapError error)
    {
        ArgumentNullException.ThrowIfNull(path);
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        lookup = default;
        error = CapError.Success;

        // Refusing `..` here rather than walking it is the rule for every path arriving from
        // a caller, and it is the same rule an open is held to. A resolver beneath can take
        // an upward step safely by moving back through a handle it already holds; a path that
        // asks to climb above the root is one this handle has no answer for.
        if (!CapPath.TryParse(path, out CapPath parsed, out CapPathError pathError))
        {
            return pathError;
        }

        if (!parsed.TrySplitLastComponent(out ReadOnlySpan<char> prefix, out ReadOnlySpan<char> name))
        {
            return CapPathError.Empty;
        }

        if (prefix.IsEmpty)
        {
            lookup = new NameLookup(null, _handle, name, parsed.RequiresDirectory);
            return CapPathError.None;
        }

        CapResult<ResolvedParent> parent = Resolver.ResolveParent(_handle, in parsed, _options);
        if (!parent.IsSuccess)
        {
            error = parent.Error;
            return CapPathError.None;
        }

        lookup = new NameLookup(
            parent.Value, parent.Value.Directory, parent.Value.Name, parsed.RequiresDirectory);

        return CapPathError.None;
    }

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

            error = PlatformOps.Current.CreateChildDirectory(
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
                PlatformOps.Current.OpenChildDirectory(lookup.Directory, lookup.Name, CapAccess.Read);

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
            // into components, so it has to be applied here or not at all.
            if (!directory && lookup.RequiresDirectory)
            {
                error = CapError.FromCategory(CapErrorCategory.IsADirectory);
                return CapPathError.None;
            }

            error = directory
                ? PlatformOps.Current.RemoveChildDirectory(lookup.Directory, lookup.Name)
                : PlatformOps.Current.RemoveChildFile(lookup.Directory, lookup.Name);

            return CapPathError.None;
        }
    }

    /// <summary>Creates a symbolic link of either kind.</summary>
    private CapPathError SymlinkCore(
        string linkPath,
        string target,
        bool targetIsDirectory,
        out CapError error,
        out ExpectedTarget expected)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);

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

            if (!targetIsDirectory && lookup.RequiresDirectory)
            {
                error = CapError.FromCategory(CapErrorCategory.IsADirectory);
                return CapPathError.None;
            }

            error = PlatformOps.Current.CreateChildSymbolicLink(
                lookup.Directory, lookup.Name, target, targetIsDirectory);

            return CapPathError.None;
        }
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
        Dir toDir,
        string to,
        bool rename,
        bool replaceExisting,
        out CapPathError fromError,
        out CapPathError toError,
        out ExpectedTarget expected)
    {
        ArgumentNullException.ThrowIfNull(toDir);

        toError = CapPathError.None;
        expected = ExpectedTarget.Name;

        fromError = Locate(from, out NameLookup source, out CapError error);
        using (source)
        {
            if (fromError != CapPathError.None || error.IsFailure)
            {
                expected = ExpectedTarget.Directory;
                return error;
            }

            toError = toDir.Locate(to, out NameLookup destination, out error);
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
                    CapError described = PlatformOps.Current.StatChild(source.Directory, source.Name, out CapNodeInfo info);
                    if (described.IsFailure)
                    {
                        return described;
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
                    return PlatformOps.Current.RenameChild(
                        source.Directory, source.Name, destination.Directory, destination.Name, replaceExisting);
                }

                CapError linked = PlatformOps.Current.CreateChildHardLink(
                    source.Directory, source.Name, destination.Directory, destination.Name);

                // Linux and macOS refuse a second name for a directory as a permission failure,
                // which would reach the caller as the filesystem refusing access to something it
                // can read perfectly well. The refusal is reclassified after the fact rather than
                // anticipated, so that the link itself is still one call.
                if (linked.Category == CapErrorCategory.PermissionDenied &&
                    PlatformOps.Current.StatChild(source.Directory, source.Name, out CapNodeInfo refused).IsSuccess &&
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

            CapResult<string> read = PlatformOps.Current.ReadChildLink(lookup.Directory, lookup.Name);
            if (!read.IsSuccess)
            {
                error = read.Error;
                return CapPathError.None;
            }

            target = read.Value;
            return CapPathError.None;
        }
    }


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

        CapResult<SafeDirHandle> copy = PlatformOps.Current.DuplicateDirectory(_handle);
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

        CapResult<SafeDirHandle> copy = PlatformOps.Current.DuplicateDirectory(_handle);
        if (!copy.IsSuccess)
        {
            return copy.Error;
        }

        clone = new Dir(copy.Value, _options);
        return CapError.Success;
    }
}
