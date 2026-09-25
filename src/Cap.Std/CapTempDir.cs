using Cap.Primitives;
using Cap.Primitives.Interop;

namespace Cap.Std;

/// <summary>
/// A directory made to be thrown away, and the authority to work inside it.
/// </summary>
/// <remarks>
/// <para>
/// Scratch directories are a classic source of local privilege escalation, and almost always
/// for one of two reasons. Either the name was predictable, so somebody else created it
/// first — as a symbolic link to a file they wanted overwritten — or the creator checked
/// whether the name was free and then created it, leaving an instant in which somebody else
/// could take it. Neither is possible here: the name carries a hundred and twenty bits from
/// the system's cryptographic generator, and it is claimed by a single operation that fails
/// if the name is taken rather than by a check followed by a creation.
/// </para>
/// <para>
/// <strong>On Unix the directory usually lands somewhere every account on the machine can
/// write to,</strong> and that is safe here for a reason worth stating rather than assuming.
/// Being able to create names next to ours buys nothing: the name we use is one nobody can
/// predict, the creation that claims it fails outright if it has been taken, and from that
/// point on the directory is reached only through the handle this holds. Renaming the
/// directory, or replacing it with a link, cannot redirect anything — a handle refers to the
/// object, not to the name it had when it was opened. The directory is also created so that
/// only the account that made it may look inside, which is what <c>mkdtemp</c> does and for
/// the same reason: the caller did not choose this location, so the choice of who can read
/// what goes there is not theirs to have made.
/// </para>
/// <para>
/// <strong>Disposal is the only cleanup, and it is not guaranteed to run.</strong> A process
/// killed outright, or ended by an exception nothing catches, leaves the directory behind;
/// nothing here registers a shutdown hook, because a hook that runs during shutdown races
/// every other thread that might still be working inside the directory. What is promised is
/// that disposal removes the tree, and that it does so by descending through handles rather
/// than by rebuilding paths — a cleanup that joins strings is exactly the bug this type is
/// meant to avoid, and it is worse in cleanup than anywhere else because it deletes.
/// </para>
/// </remarks>
public sealed class CapTempDir : IDisposable
{
    /// <summary>
    /// The application context switch that makes every scratch directory in this process
    /// survive its own disposal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For looking at what a failing run left behind, which is the one time the contents are
    /// more valuable than the tidiness. Set it in the application's project file:
    /// </para>
    /// <code>
    /// &lt;RuntimeHostConfigurationOption Include="Cap.Std.PersistTemporaryDirectories" Value="true" /&gt;
    /// </code>
    /// <para>
    /// or from code, before the directories in question are disposed:
    /// <c>AppContext.SetSwitch(CapTempDir.PersistSwitchName, true)</c>. Read afresh at each
    /// disposal, so it can be turned on around one phase of a run.
    /// </para>
    /// <para>
    /// Nothing else changes when it is on. The directories are still created the same way and
    /// the handles are still closed; only the removal is skipped, and what is skipped stays
    /// on the disk until something else removes it.
    /// </para>
    /// <para>
    /// Setting the switch from one thread while another disposes a scratch directory decides
    /// that one disposal either way; every disposal that starts afterwards sees it.
    /// </para>
    /// </remarks>
    public const string PersistSwitchName = "Cap.Std.PersistTemporaryDirectories";

    /// <summary>
    /// The environment variable that does the same, for a failing run that cannot be
    /// rebuilt: <c>CAPDOTNET_PERSIST_TEMPORARY=1</c>.
    /// </summary>
    /// <remarks>
    /// Read once, the first time it matters, so that it cannot change under a running
    /// process and leave half the directories removed for a reason nobody can reconstruct.
    /// </remarks>
    public const string PersistVariableName = "CAPDOTNET_PERSIST_TEMPORARY";

    private static readonly bool PersistRequestedByEnvironment =
        Environment.GetEnvironmentVariable(PersistVariableName) is "1" or "true" or "TRUE";

    private readonly Dir _parent;
    private readonly Dir _directory;
    private readonly string _name;
    private bool _keep;
    private bool _disposed;

    private CapTempDir(Dir parent, Dir directory, string name)
    {
        _parent = parent;
        _directory = directory;
        _name = name;
    }

    /// <summary>
    /// Creates a scratch directory in the system's temporary location.
    /// </summary>
    /// <param name="authority">
    /// Proof that taking authority from outside the capability graph is intended here. Must
    /// come from <see cref="AmbientAuthority.Acquire"/>; a default value is refused.
    /// </param>
    /// <param name="policy">
    /// What resolution beneath the returned handle does with a symbolic link it meets on the
    /// way to the thing a path names. Travels with that handle and with everything derived
    /// from it.
    /// </param>
    /// <returns>The scratch directory.</returns>
    /// <remarks>
    /// <para>
    /// The temporary location is found the way every other program on the system finds it —
    /// the environment variable the platform defines, and the platform's own directory when
    /// it says nothing — and it is resolved exactly once, here, by opening it. That matters
    /// on macOS, where the conventional temporary path leads through a symbolic link:
    /// resolving it repeatedly would mean asking a path question again every time, and the
    /// answer can change between two askings. After this call the location is a handle, and
    /// nothing looks it up again.
    /// </para>
    /// <para>
    /// This is the ambient step, so everything the containment guarantee promises begins
    /// after it. A process whose temporary location has been pointed at somewhere hostile
    /// gets a scratch directory there, and the only defence against that is the same one
    /// every other program on the system has.
    /// </para>
    /// <para>
    /// Safe to call from any thread, as often as wanted: each call opens the location for
    /// itself and draws its own name, and no two calls can be handed the same directory.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> The temporary location is an ordinary path opened
    /// with the process's own authority, so a link anywhere in it is followed wherever it
    /// leads, as it would be for any other program. The scratch directory beneath it is
    /// different: its name is claimed by a creation that never follows a link, so a link
    /// already sitting at that name counts as the name being taken and another is drawn.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="authority"/> was never acquired.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="policy"/> is not a value the enumeration defines.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">
    /// This system has no temporary directory, or the one it names is not there.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the creation.</exception>
    /// <exception cref="CapIOException">The directory could not be created.</exception>
    public static CapTempDir New(
        AmbientAuthority authority,
        SymlinkPolicy policy = SymlinkPolicy.FollowWithinSandbox) =>
        NewThrough(PlatformOps.Host, authority, policy);

    /// <summary>
    /// Creates a scratch directory in the temporary location <paramref name="backend"/>
    /// reports, as <see cref="New(AmbientAuthority, SymlinkPolicy)"/> does on the host.
    /// </summary>
    internal static CapTempDir NewThrough(
        IPlatformOps backend,
        AmbientAuthority authority,
        SymlinkPolicy policy = SymlinkPolicy.FollowWithinSandbox)
    {
        ArgumentNullException.ThrowIfNull(backend);
        authority.Demand(nameof(authority));

        CapResult<string> location = backend.GetSystemTemporaryDirectory();
        if (!location.IsSuccess)
        {
            throw FailureTranslation.ToException(location.Error, SystemLocationDescription);
        }

        Dir parent = Dir.OpenThrough(backend, location.Value, authority, policy);
        try
        {
            return CreateIn(parent, location.Value);
        }
        catch
        {
            parent.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a scratch directory inside a directory the caller already holds.
    /// </summary>
    /// <param name="parent">The directory to create it in.</param>
    /// <returns>
    /// The scratch directory, carrying <paramref name="parent"/>'s symbolic-link policy.
    /// </returns>
    /// <remarks>
    /// <para>
    /// No ambient authority is needed and none is taken: the place comes from a handle the
    /// caller was given, so nothing here reaches outside what they could already reach. That
    /// makes this the form to prefer wherever there is a handle to hand, and the form a test
    /// should use for scratch space inside a tree it is already working in.
    /// </para>
    /// <para>
    /// The parent handle is duplicated rather than borrowed, so disposing it does not stop
    /// this from cleaning up after itself, and disposing this leaves the caller's handle
    /// open.
    /// </para>
    /// <para>
    /// Safe to call from any thread, including several at once against the same
    /// <paramref name="parent"/>: each call works through its own copy of the handle and
    /// draws its own name.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> The name is claimed by a creation that never follows
    /// a link, so a link already sitting at that name counts as the name being taken and
    /// another is drawn. <paramref name="parent"/> is a handle, so no link above it is
    /// consulted either.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> is null.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the creation.</exception>
    /// <exception cref="CapIOException">The directory could not be created.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="parent"/> has been disposed.</exception>
    public static CapTempDir NewIn(Dir parent)
    {
        ArgumentNullException.ThrowIfNull(parent);

        Dir owned = parent.Clone();
        try
        {
            return CreateIn(owned, ParentDescription);
        }
        catch
        {
            owned.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A handle on the scratch directory, carrying authority over it and nothing above it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the capability, and it is what gets passed on to whatever needs to work in
    /// the directory. Passing it hands over the authority without handing over the cleanup:
    /// the holder cannot remove the directory itself, and disposing this removes it whether
    /// or not they are finished — so a handle handed to something outliving this object is a
    /// handle on a directory that is about to disappear.
    /// </para>
    /// <para>
    /// Safe to read from any thread, and the handle it returns is itself safe for concurrent
    /// use; see <see cref="Dir"/>.
    /// </para>
    /// </remarks>
    public Dir Directory => _directory;

    /// <summary>The name the directory was given in the directory holding it.</summary>
    /// <remarks>
    /// <para>
    /// A single component and never a path. It is the one fact about the directory that
    /// cannot be recovered from the handle, and it is here so that a log line, or a test
    /// looking at the tree from outside, can say which directory is being talked about.
    /// </para>
    /// <para>Safe to read from any thread.</para>
    /// </remarks>
    public string Name => _name;

    /// <summary>
    /// Leaves the directory and everything in it on the disk when this is disposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the case where what is in the directory turned out to be the interesting part: a
    /// test that failed, a conversion that produced the wrong output. Calling it does not
    /// change what the directory is or what may be done with it, and it cannot be undone —
    /// a caller that decides to keep something has decided, and reversing that from
    /// somewhere else in the program would be a way for cleanup to reappear where somebody
    /// had reasoned it away.
    /// </para>
    /// <para>
    /// The handles are still closed on disposal. What is kept is the directory, not the
    /// authority over it.
    /// </para>
    /// <para>
    /// Not synchronised with <see cref="Dispose"/>. Called from another thread while disposal
    /// is under way, it may or may not take effect; call it before disposal begins, from the
    /// thread that will dispose.
    /// </para>
    /// </remarks>
    public void Keep() => _keep = true;

    /// <summary>
    /// Removes the directory and everything in it, and closes the handles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The removal descends by handle: each directory is opened through the handle that
    /// listed it, and each name is removed against the directory it was found in. Nothing is
    /// joined into a path, which is what makes this safe to point at a tree somebody else
    /// has been writing to — a name swapped for a symbolic link between being listed and
    /// being removed is unlinked as the link it now is, and what it points at is not reached.
    /// </para>
    /// <para>
    /// <strong>It does not report what it could not remove.</strong> Disposal runs on paths
    /// where throwing would replace the exception that is already in flight with one about
    /// tidying up, so anything that will not go is left behind silently. That is also why the
    /// persistence switch exists: a run whose scratch directories need looking at should be
    /// told to keep them rather than be left to infer what survived.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> A link found in the tree is never descended into: it
    /// is unlinked as a link, and what it points at, file or directory, is not removed
    /// through it. The removal works through a duplicate of the scratch directory's handle
    /// narrowed to <see cref="SymlinkPolicy.Deny"/>, whatever policy the scratch directory
    /// carries, so a directory that something replaces with a link between being listed and
    /// being opened is not entered either — not even when the link leads to another directory
    /// inside the scratch tree — and its name is unlinked as the link.
    /// </para>
    /// <para>
    /// Disposing twice does nothing the second time.
    /// </para>
    /// <para>
    /// <strong>Not thread-safe.</strong> Two disposals racing each other are not guarded
    /// against, and a removal racing work that other threads are still doing inside the
    /// directory — including through <see cref="Directory"/> — leaves behind whatever those
    /// threads create after the removal has passed. Dispose once, after that work has
    /// finished.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        bool remove = !_keep && !PersistRequested;

        // Emptied through the handle this has held since the directory was created, so the
        // work starts from the object itself rather than from a fresh lookup of its name.
        // The handle is closed before the directory is removed because Windows will not
        // remove a directory anything still has open.
        CapError emptied = remove ? TreeRemoval.Empty(_directory) : CapError.Success;
        _directory.Dispose();

        if (remove && emptied.IsSuccess)
        {
            _ = TreeRemoval.RemoveEmpty(_parent, _name);
        }

        _parent.Dispose();
    }

    /// <summary>Whether something has asked for scratch directories to be left behind.</summary>
    private static bool PersistRequested =>
        PersistRequestedByEnvironment || (AppContext.TryGetSwitch(PersistSwitchName, out bool on) && on);

    /// <summary>
    /// Claims an unused name beneath a directory and creates it.
    /// </summary>
    /// <remarks>
    /// The loop is not there for crowding. A name of this width does not collide by chance,
    /// so a name reported as taken means either that something is guessing at names or that
    /// the directory is being filled faster than it can be read — and in both cases the
    /// answer is to draw again from a space nobody is going to exhaust. The budget is what
    /// keeps a directory that reports every name as taken from becoming a call that never
    /// returns.
    /// </remarks>
    private static CapTempDir CreateIn(Dir parent, string description)
    {
        CapError error = CapError.FromCategory(CapErrorCategory.AlreadyExists);

        for (int attempt = 0; attempt < TemporaryNames.Attempts; attempt++)
        {
            string name = TemporaryNames.Next();
            error = parent.CreateOwnedDir(name, out Dir? created);

            if (error.IsSuccess)
            {
                return new CapTempDir(parent, created!, name);
            }

            if (error.Category != CapErrorCategory.AlreadyExists)
            {
                break;
            }
        }

        throw FailureTranslation.ToException(error, description);
    }

    /// <summary>How the system's own temporary location is described in a failure message.</summary>
    /// <remarks>
    /// The location itself is not quoted, because the failure being reported is about the
    /// scratch directory rather than about the path the caller never named.
    /// </remarks>
    private const string SystemLocationDescription = "the system temporary directory";

    /// <summary>How a caller-supplied parent is described in a failure message.</summary>
    /// <remarks>
    /// A handle has no path to quote, which is the point of a handle. Naming it as what it is
    /// says more than an approximation of where it might be.
    /// </remarks>
    private const string ParentDescription = "the directory a scratch directory was asked for in";
}
