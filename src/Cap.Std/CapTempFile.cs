using Cap.Primitives.Interop;
using Microsoft.Win32.SafeHandles;

namespace Cap.Std;

/// <summary>
/// A file made to be thrown away, created so that nobody else can get at it first.
/// </summary>
/// <remarks>
/// <para>
/// The attack this exists to remove is the same one that makes scratch directories
/// dangerous: a name that can be predicted can be claimed by somebody else before the
/// program that intends to use it gets there, and what the program then opens is a symbolic
/// link pointing at a file the attacker wants written. So the name carries a hundred and
/// twenty bits from the system's cryptographic generator, and it is claimed by an exclusive
/// creation — one operation that either makes the file or reports the name as taken, never a
/// check followed by an open.
/// </para>
/// <para>
/// <strong>A file with no name at all is better still, and is used where the system offers
/// one.</strong> Such a file has no entry in any directory, so there is nothing to predict,
/// nothing to claim and nowhere to plant a link: the handle is the only reference to it and
/// the storage goes back when the last one closes. <see cref="NewAnonymous"/> asks for that
/// and says, through <see cref="HasName"/>, whether it got it — because where the system has
/// no such facility the fallback is an ordinary named file, and a caller who reasoned about
/// the nameless one should be able to find out that they did not get it.
/// </para>
/// <para>
/// <strong>Disposal is the only cleanup, and a process that is killed leaves the file
/// behind.</strong> A file with no name is the exception: nothing has to run for it to go,
/// because there is no name to clean up and the system reclaims the storage on its own.
/// </para>
/// </remarks>
public sealed class CapTempFile : IDisposable
{
    private readonly Dir? _parent;
    private readonly CapFile _file;
    private readonly string? _name;
    private bool _keep;
    private bool _disposed;

    private CapTempFile(Dir? parent, CapFile file, string? name)
    {
        _parent = parent;
        _file = file;
        _name = name;
    }

    /// <summary>
    /// Creates a scratch file with an unguessable name in a directory the caller holds.
    /// </summary>
    /// <param name="parent">The directory to create it in.</param>
    /// <returns>The scratch file, open for reading and writing.</returns>
    /// <remarks>
    /// <para>
    /// No ambient authority is needed and none is taken: the place comes from a handle the
    /// caller was given. The file is created with whatever permissions the system gives a
    /// file created in that directory, because the caller chose the directory — a scratch
    /// file in a directory somebody asked for should be no more and no less readable than
    /// anything else they put there.
    /// </para>
    /// <para>
    /// The parent handle is duplicated rather than borrowed, so disposing it does not stop
    /// this from removing its file, and disposing this leaves the caller's handle open.
    /// </para>
    /// <para>
    /// Safe to call from any thread, including several at once against the same
    /// <paramref name="parent"/>: each call works through its own copy of the handle and
    /// draws its own name.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> The creation is exclusive, and an exclusive creation
    /// never follows a link: a link already sitting at the drawn name, wherever it points,
    /// counts as the name being taken, and another name is drawn. That is what stops a
    /// planted link from redirecting the file somewhere the attacker chose.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> is null.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the creation.</exception>
    /// <exception cref="CapIOException">The file could not be created.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="parent"/> has been disposed.</exception>
    public static CapTempFile New(Dir parent)
    {
        ArgumentNullException.ThrowIfNull(parent);

        Dir owned = parent.Clone();
        try
        {
            return CreateNamed(owned);
        }
        catch
        {
            owned.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a scratch file with no name where the system can, and a named one where it
    /// cannot.
    /// </summary>
    /// <param name="parent">The directory the file's storage comes from.</param>
    /// <returns>
    /// The scratch file, open for reading and writing. <see cref="HasName"/> says which kind
    /// it turned out to be.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A nameless file cannot be reached by anything but the handle returned here, whatever
    /// the permissions on the directory it came from, so it is the right choice for scratch
    /// data that is never going to be opened by name — which is most scratch data. It is
    /// also the only kind that cannot be left behind by a process that dies.
    /// </para>
    /// <para>
    /// Where the system or the filesystem has no such facility, this creates an ordinary
    /// exclusively-named file instead. That is a weaker thing and it is reported rather than
    /// glossed over: the file has a name, something with access to the directory can open it,
    /// and it needs disposal to go away.
    /// </para>
    /// <para>
    /// Safe to call from any thread, including several at once against the same
    /// <paramref name="parent"/>.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> A nameless file is created against the directory
    /// handle itself, with no name to look up, so there is no link that could be met. The
    /// named fallback behaves as <see cref="New"/> does.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="parent"/> is null.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the creation.</exception>
    /// <exception cref="CapIOException">The file could not be created.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="parent"/> has been disposed.</exception>
    public static CapTempFile NewAnonymous(Dir parent)
    {
        ArgumentNullException.ThrowIfNull(parent);

        CapResult<CapFile> anonymous = parent.OpenAnonymousFile();
        if (anonymous.IsSuccess)
        {
            // Nothing is kept beyond the handle. There is no name to remove later and no
            // directory to remove it from, so there is nothing for this object to hold.
            return new CapTempFile(parent: null, anonymous.Value, name: null);
        }

        if (anonymous.Error.Category != CapErrorCategory.NotSupported)
        {
            throw FailureTranslation.ToException(anonymous.Error, AnonymousDescription, ExpectedTarget.Name);
        }

        return New(parent);
    }

    /// <summary>The open file, for reading and writing.</summary>
    /// <remarks>
    /// <para>
    /// This is the capability. Disposing this object closes it, so a handle passed to
    /// something that outlives this object is a handle on a file that is about to go.
    /// </para>
    /// <para>
    /// Safe to read from any thread; what the returned file guarantees to concurrent callers
    /// is described on <see cref="CapFile"/>.
    /// </para>
    /// </remarks>
    public CapFile File => _file;

    /// <summary>
    /// The name the file was given in the directory holding it, or <see langword="null"/>
    /// when it has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A single component and never a path. Null is not a missing answer: it means the file
    /// genuinely has no entry in any directory, which is a stronger position than any name
    /// could give it.
    /// </para>
    /// <para>Safe to read from any thread.</para>
    /// </remarks>
    public string? Name => _name;

    /// <summary>
    /// Whether the file has a name that something else could open it by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// False is the better answer. A caller who asked for a nameless file and got one can
    /// rely on nothing else in the system being able to reach it; a caller who asked and got
    /// a named one has an ordinary file, protected by the unguessability of its name and by
    /// the permissions on the directory it is in.
    /// </para>
    /// <para>Safe to read from any thread.</para>
    /// </remarks>
    public bool HasName => _name is not null;

    /// <summary>
    /// Leaves the file on the disk when this is disposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the case where what was written turned out to be worth looking at. It cannot be
    /// undone, and it does nothing at all for a file with no name: there is no name for such
    /// a file to be kept under, and the storage goes back when the handle closes whatever
    /// anybody asks for.
    /// </para>
    /// <para>
    /// The handle is still closed on disposal. What is kept is the file, not the access to
    /// it.
    /// </para>
    /// <para>
    /// Not synchronised with <see cref="Dispose"/>. Called from another thread while disposal
    /// is under way, it may or may not take effect; call it before disposal begins, from the
    /// thread that will dispose.
    /// </para>
    /// </remarks>
    public void Keep() => _keep = true;

    /// <summary>
    /// Removes the file and closes the handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name is removed against the handle on the directory it was created in, so the
    /// removal lands in the same directory the creation did whatever has happened to the
    /// names around it. It removes the name and not what the name refers to, so a name
    /// something has replaced with a symbolic link is unlinked as that link.
    /// </para>
    /// <para>
    /// Nothing is reported. Disposal runs where throwing would replace an exception already
    /// in flight with one about tidying up, so a file that will not go is left behind
    /// silently.
    /// </para>
    /// <para>
    /// Disposing twice does nothing the second time.
    /// </para>
    /// <para>
    /// <strong>Not thread-safe.</strong> Two disposals racing each other are not guarded
    /// against. Other threads still using <see cref="File"/> are not corrupted by it — an
    /// operation under way finishes, and one that starts afterwards throws
    /// <see cref="ObjectDisposedException"/> — but they lose the file under them, so dispose
    /// once, after that work has finished.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Closed first: Windows will not remove a file that is still open unless it was
        // opened with that in mind, and this one was not.
        _file.Dispose();

        if (_parent is null)
        {
            return;
        }

        if (!_keep && _name is not null)
        {
            _ = _parent.DeleteFileCore(_name);
        }

        _parent.Dispose();
    }

    /// <summary>Claims an unused name beneath a directory and creates the file.</summary>
    /// <remarks>
    /// The budget is for the same reason the scratch directory has one: a name of this width
    /// does not collide by chance, so a name reported as taken means something is guessing,
    /// and the answer to that is to draw again rather than to give up or to loop for ever.
    /// </remarks>
    private static CapTempFile CreateNamed(Dir parent)
    {
        for (int attempt = 0; attempt < TemporaryNames.Attempts; attempt++)
        {
            string name = TemporaryNames.Next();

            // Exclusive creation, and the only mode that is: the name is either claimed by
            // this call or it was somebody else's already. Read as well as write, because a
            // scratch file is written and then read back, and reopening it by name to read it
            // would put a lookup where the handle already is.
            bool created = parent.TryOpenFile(
                name,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                FileOptions.None,
                preallocationSize: 0,
                append: false,
                noFollow: false,
                out CapFile? file);

            if (created)
            {
                return new CapTempFile(parent, file!, name);
            }
        }

        throw FailureTranslation.ToException(
            CapError.FromCategory(CapErrorCategory.AlreadyExists), NamedDescription, ExpectedTarget.Name);
    }

    /// <summary>How a nameless file is described in a failure message.</summary>
    private const string AnonymousDescription = "a scratch file with no name";

    /// <summary>How a named scratch file is described in a failure message.</summary>
    private const string NamedDescription = "a scratch file";
}
