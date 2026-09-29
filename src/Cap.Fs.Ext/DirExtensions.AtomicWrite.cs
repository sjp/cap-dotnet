using System.Text;
using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Std;

namespace Cap.Fs.Ext;

/// <summary>
/// Publishing a file by writing it somewhere else first.
/// </summary>
/// <remarks>
/// <para>
/// The most-asked-for helper in any filesystem library and the one most often written
/// wrongly. The shape is always the same — write the contents under a name nobody is
/// watching, then move that name onto the real one — and what goes wrong is always one of
/// three things: the move is made across directories, so it is not a move at all but a copy
/// and a delete; the contents are never committed, so a crash leaves a name that resolves to
/// nothing; or the directory is never committed, so the move itself is the part that is lost.
/// </para>
/// <para>
/// The first of those cannot happen here, because a directory handle is where the work
/// happens and both names are single components used against it. The other two are the
/// caller's decision, expressed as <see cref="Durability"/>.
/// </para>
/// </remarks>
public static partial class DirExtensions
{
    /// <summary>
    /// Writes a file beneath this handle so that no reader ever sees it half-written.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the file to publish.</param>
    /// <param name="bytes">The contents to store.</param>
    /// <param name="durability">How far the write is pushed before it is treated as done.</param>
    /// <remarks>
    /// <para>
    /// The contents go to a scratch name in the same directory, drawn so that nobody can
    /// predict it, and are then moved onto <paramref name="path"/> in one operation. A reader
    /// opening the name at any moment gets either the whole of what was there before or the
    /// whole of what is there now — never a prefix of the new contents, and never a moment in
    /// which the name resolves to nothing.
    /// </para>
    /// <para>
    /// <strong>Only the last component is published atomically.</strong> Everything ahead of
    /// it is resolved first, once, and both names are used against the directory that
    /// resolution reached — which is what makes the move a move rather than a copy between
    /// filesystems, and what keeps it inside the subtree the handle grants.
    /// </para>
    /// <para>
    /// A failure leaves the scratch name removed and whatever was already at
    /// <paramref name="path"/> untouched. A crash may leave the scratch name behind; it is
    /// recognisable, it is inside the same directory, and it is never mistaken for the file.
    /// </para>
    /// <para>
    /// Safe to call from any thread, and concurrently with other writers of the same name:
    /// each call claims its own scratch name, so two publishing at once cannot write into
    /// each other's file, the later move wins, and a reader sees one whole version or the
    /// other.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> Components ahead of the last are resolved under this
    /// handle's own policy, so a link among them is followed while it stays inside the subtree
    /// or refused as for any other operation. The last component is never followed: the move
    /// replaces the name, so a link already holding it is replaced by the new file and what it
    /// pointed at is neither written nor removed, wherever it points and whether or not its
    /// target exists. That includes a link to a directory on Windows — a directory symbolic
    /// link or a junction — which is replaced as the name it is. Only on a Windows version
    /// whose rename lacks the form that replaces such a link is the publish refused instead,
    /// with a <see cref="CapIOException"/> whose kind is
    /// <see cref="CapErrorKind.SymbolicLink"/>, leaving the link and what it points at as they
    /// were. The scratch
    /// file is created exclusively, so a link planted at its name makes the creation fail
    /// rather than redirecting it.
    /// </para>
    /// <para>
    /// <strong>On a handle that is not a <see cref="Dir"/>.</strong> The same steps are taken
    /// through the interface's members, and the directory is committed with
    /// <see cref="IDir.Flush"/>, whose answer that it cannot commit one is accepted as it is
    /// on Windows.
    /// </para>
    /// <para>
    /// <strong>Permissions, ownership and hard links.</strong> The published file is a new
    /// object, not the old one rewritten. When a file already holds the name it is given that
    /// file's permissions — the mode on Unix, the attribute flags on Windows — as
    /// <see cref="AtomicWriteOptions.PreservePermissions"/> describes; ownership is not
    /// carried, and any other hard link to the old file keeps the old contents. On Windows a
    /// read-only file holding the name refuses the publish.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name for a file.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="CapIOException">
    /// The write or the move failed, or on Windows a link to a directory holds the name.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static void WriteAllBytesAtomic(
        this IDir dir,
        string path,
        ReadOnlySpan<byte> bytes,
        Durability durability = Durability.FileAndDirectory) =>
        WriteAllBytesAtomicCore(dir, path, bytes, durability, preservePermissions: true);

    /// <summary>
    /// Writes a file beneath this handle so that no reader ever sees it half-written, with
    /// the settings given.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the file to publish.</param>
    /// <param name="bytes">The contents to store.</param>
    /// <param name="options">How far the write is pushed, and what it carries from a file it replaces.</param>
    /// <remarks>
    /// The same operation as
    /// <see cref="WriteAllBytesAtomic(IDir, string, ReadOnlySpan{byte}, Durability)"/>, which
    /// is this with <see cref="AtomicWriteOptions.Default"/> and the durability it was given.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name for a file.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="CapIOException">
    /// The write, the permissions or the move failed, or on Windows a link to a directory
    /// holds the name.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static void WriteAllBytesAtomic(
        this IDir dir,
        string path,
        ReadOnlySpan<byte> bytes,
        AtomicWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        WriteAllBytesAtomicCore(dir, path, bytes, options.Durability, options.PreservePermissions);
    }

    /// <summary>
    /// Writes a text file beneath this handle so that no reader ever sees it half-written.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the file to publish.</param>
    /// <param name="contents">The text to store.</param>
    /// <param name="durability">How far the write is pushed before it is treated as done.</param>
    /// <remarks>
    /// <para>
    /// Encoded as UTF-8 with no byte-order mark, which is what the framework's own text write
    /// produces and what the matching read here assumes when a file begins with no mark. A
    /// caller who needs some other encoding encodes it themselves and publishes the bytes.
    /// </para>
    /// <para>
    /// Safe to call from any thread, on the terms <see cref="WriteAllBytesAtomic(IDir, string, ReadOnlySpan{byte}, Durability)"/> gives.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> Treated exactly as <see cref="WriteAllBytesAtomic(IDir, string, ReadOnlySpan{byte}, Durability)"/>
    /// treats them: followed ahead of the last component as the policy allows, and replaced,
    /// never followed, as the last — except that on Windows a link to a directory there makes
    /// the publish fail instead.
    /// </para>
    /// <para>
    /// <strong>Permissions, ownership and hard links.</strong> As
    /// <see cref="WriteAllBytesAtomic(IDir, string, ReadOnlySpan{byte}, Durability)"/> treats them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name for a file.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="CapIOException">
    /// The write or the move failed, or on Windows a link to a directory holds the name.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static void WriteAllTextAtomic(
        this IDir dir,
        string path,
        string contents,
        Durability durability = Durability.FileAndDirectory)
    {
        ArgumentNullException.ThrowIfNull(contents);

        WriteAllBytesAtomicCore(dir, path, Encoding.UTF8.GetBytes(contents), durability, preservePermissions: true);
    }

    /// <summary>
    /// Writes a text file beneath this handle so that no reader ever sees it half-written,
    /// with the settings given.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the file to publish.</param>
    /// <param name="contents">The text to store.</param>
    /// <param name="options">How far the write is pushed, and what it carries from a file it replaces.</param>
    /// <remarks>
    /// The same operation as <see cref="WriteAllTextAtomic(IDir, string, string, Durability)"/>,
    /// which is this with <see cref="AtomicWriteOptions.Default"/> and the durability it was
    /// given.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name for a file.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="CapIOException">
    /// The write, the permissions or the move failed, or on Windows a link to a directory
    /// holds the name.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static void WriteAllTextAtomic(
        this IDir dir,
        string path,
        string contents,
        AtomicWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(options);

        WriteAllBytesAtomicCore(
            dir, path, Encoding.UTF8.GetBytes(contents), options.Durability, options.PreservePermissions);
    }

    /// <summary>
    /// Publishes a file beneath this handle without holding the calling thread.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the file to publish.</param>
    /// <param name="bytes">The contents to store.</param>
    /// <param name="durability">How far the write is pushed before it is treated as done.</param>
    /// <param name="cancellationToken">Asks for the write to be abandoned.</param>
    /// <remarks>
    /// <para>
    /// The same operation as the synchronous form, with the contents written through a handle
    /// the operating system can complete by itself where it offers that. Resolving the path,
    /// claiming the scratch name, moving it into place and committing the directory all still
    /// happen on the calling thread: they are short sequences of calls that no platform here
    /// performs asynchronously, and a version that promised otherwise would only be waiting on
    /// a pool thread instead.
    /// </para>
    /// <para>
    /// Abandoning it leaves nothing behind and leaves <paramref name="path"/> as it was. That
    /// is the difference from cancelling an ordinary write, which has already emptied the file
    /// it was writing to by the time it notices.
    /// </para>
    /// <para>
    /// Safe to call from any thread, and concurrently with other writers of the same name:
    /// each call claims its own scratch name, so two publishing at once cannot write into
    /// each other's file, the later move wins, and a reader sees one whole version or the
    /// other.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> Components ahead of the last are resolved under this
    /// handle's own policy, so a link among them is followed while it stays inside the subtree
    /// or refused as for any other operation. The last component is never followed: the move
    /// replaces the name, so a link already holding it is replaced by the new file and what it
    /// pointed at is neither written nor removed, wherever it points and whether or not its
    /// target exists. That includes a link to a directory on Windows — a directory symbolic
    /// link or a junction — which is replaced as the name it is. Only on a Windows version
    /// whose rename lacks the form that replaces such a link is the publish refused instead,
    /// with a <see cref="CapIOException"/> whose kind is
    /// <see cref="CapErrorKind.SymbolicLink"/>, leaving the link and what it points at as they
    /// were. The scratch
    /// file is created exclusively, so a link planted at its name makes the creation fail
    /// rather than redirecting it.
    /// </para>
    /// <para>
    /// <strong>On a handle that is not a <see cref="Dir"/>.</strong> The same steps are taken
    /// through the interface's members, and the directory is committed with
    /// <see cref="IDir.Flush"/>, whose answer that it cannot commit one is accepted as it is
    /// on Windows.
    /// </para>
    /// <para>
    /// <strong>Permissions, ownership and hard links.</strong> The published file is a new
    /// object, not the old one rewritten. When a file already holds the name it is given that
    /// file's permissions — the mode on Unix, the attribute flags on Windows — as
    /// <see cref="AtomicWriteOptions.PreservePermissions"/> describes; ownership is not
    /// carried, and any other hard link to the old file keeps the old contents. On Windows a
    /// read-only file holding the name refuses the publish.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name for a file.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="CapIOException">
    /// The write or the move failed, or on Windows a link to a directory holds the name.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static Task WriteAllBytesAtomicAsync(
        this IDir dir,
        string path,
        ReadOnlyMemory<byte> bytes,
        Durability durability = Durability.FileAndDirectory,
        CancellationToken cancellationToken = default) =>
        WriteAllBytesAtomicCoreAsync(dir, path, bytes, durability, preservePermissions: true, cancellationToken);

    /// <summary>
    /// Publishes a file beneath this handle without holding the calling thread, with the
    /// settings given.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the file to publish.</param>
    /// <param name="bytes">The contents to store.</param>
    /// <param name="options">How far the write is pushed, and what it carries from a file it replaces.</param>
    /// <param name="cancellationToken">Asks for the write to be abandoned.</param>
    /// <remarks>
    /// The same operation as
    /// <see cref="WriteAllBytesAtomicAsync(IDir, string, ReadOnlyMemory{byte}, Durability, CancellationToken)"/>,
    /// which is this with <see cref="AtomicWriteOptions.Default"/> and the durability it was
    /// given.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name for a file.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="CapIOException">
    /// The write, the permissions or the move failed, or on Windows a link to a directory
    /// holds the name.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static Task WriteAllBytesAtomicAsync(
        this IDir dir,
        string path,
        ReadOnlyMemory<byte> bytes,
        AtomicWriteOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        return WriteAllBytesAtomicCoreAsync(
            dir, path, bytes, options.Durability, options.PreservePermissions, cancellationToken);
    }

    /// <summary>
    /// Publishes a text file beneath this handle without holding the calling thread.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the file to publish.</param>
    /// <param name="contents">The text to store.</param>
    /// <param name="durability">How far the write is pushed before it is treated as done.</param>
    /// <param name="cancellationToken">Asks for the write to be abandoned.</param>
    /// <remarks>
    /// <para>Encoded as <see cref="WriteAllTextAtomic(IDir, string, string, Durability)"/> describes.</para>
    /// <para>
    /// Safe to call from any thread, on the terms <see cref="WriteAllBytesAtomicAsync(IDir, string, ReadOnlyMemory{byte}, Durability, CancellationToken)"/>
    /// gives.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> Treated exactly as <see cref="WriteAllBytesAtomic(IDir, string, ReadOnlySpan{byte}, Durability)"/>
    /// treats them: followed ahead of the last component as the policy allows, and replaced,
    /// never followed, as the last — except that on Windows a link to a directory there makes
    /// the publish fail instead.
    /// </para>
    /// <para>
    /// <strong>Permissions, ownership and hard links.</strong> As
    /// <see cref="WriteAllBytesAtomic(IDir, string, ReadOnlySpan{byte}, Durability)"/> treats them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name for a file.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="CapIOException">
    /// The write or the move failed, or on Windows a link to a directory holds the name.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static Task WriteAllTextAtomicAsync(
        this IDir dir,
        string path,
        string contents,
        Durability durability = Durability.FileAndDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);

        return WriteAllBytesAtomicCoreAsync(
            dir, path, Encoding.UTF8.GetBytes(contents), durability, preservePermissions: true, cancellationToken);
    }

    /// <summary>
    /// Publishes a text file beneath this handle without holding the calling thread, with the
    /// settings given.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the file to publish.</param>
    /// <param name="contents">The text to store.</param>
    /// <param name="options">How far the write is pushed, and what it carries from a file it replaces.</param>
    /// <param name="cancellationToken">Asks for the write to be abandoned.</param>
    /// <remarks>
    /// The same operation as
    /// <see cref="WriteAllTextAtomicAsync(IDir, string, string, Durability, CancellationToken)"/>,
    /// which is this with <see cref="AtomicWriteOptions.Default"/> and the durability it was
    /// given.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name for a file.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="CapIOException">
    /// The write, the permissions or the move failed, or on Windows a link to a directory
    /// holds the name.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static Task WriteAllTextAtomicAsync(
        this IDir dir,
        string path,
        string contents,
        AtomicWriteOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(options);

        return WriteAllBytesAtomicCoreAsync(
            dir, path, Encoding.UTF8.GetBytes(contents), options.Durability, options.PreservePermissions,
            cancellationToken);
    }

    /// <summary>The synchronous publish, whichever form it was asked for through.</summary>
    private static void WriteAllBytesAtomicCore(
        IDir dir, string path, ReadOnlySpan<byte> bytes, Durability durability, bool preservePermissions)
    {
        using ParentLocation location = ParentLocation.Resolve(dir, path, nameof(path), mayNameDirectory: false);

        CapPermissions? carried = preservePermissions ? Replaced(location.Directory, location.Name) : null;
        string? scratch = Claim(location.Directory, asynchronous: false, OwnerOnlyFor(carried), out ICapFile file);
        try
        {
            using (file)
            {
                file.Write(bytes, 0);
                Carry(file, carried, location.Name);
                Commit(file, durability);
            }

            Publish(location.Directory, scratch, location.Name, durability);
            scratch = null;
        }
        finally
        {
            Abandon(location.Directory, scratch);
        }
    }

    /// <summary>The asynchronous publish, whichever form it was asked for through.</summary>
    private static async Task WriteAllBytesAtomicCoreAsync(
        IDir dir,
        string path,
        ReadOnlyMemory<byte> bytes,
        Durability durability,
        bool preservePermissions,
        CancellationToken cancellationToken)
    {
        using ParentLocation location = ParentLocation.Resolve(dir, path, nameof(path), mayNameDirectory: false);

        CapPermissions? carried = preservePermissions ? Replaced(location.Directory, location.Name) : null;
        string? scratch = Claim(location.Directory, asynchronous: true, OwnerOnlyFor(carried), out ICapFile file);
        try
        {
            using (file)
            {
                await file.WriteAsync(bytes, 0, cancellationToken).ConfigureAwait(false);
                Carry(file, carried, location.Name);
                Commit(file, durability);
            }

            Publish(location.Directory, scratch, location.Name, durability);
            scratch = null;
        }
        finally
        {
            Abandon(location.Directory, scratch);
        }
    }

    /// <summary>
    /// The permissions of the file a publish is about to replace, if a file holds the name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name is described without following it, so a symbolic link holding it answers as
    /// the link, which is not a file: the link is what the move replaces, and the mode of
    /// whatever it points at is none of this write's business.
    /// </para>
    /// <para>
    /// The answer can be out of date by the time the move is made. That is harmless: the
    /// worst it can do is give the new file the mode of whatever held the name for a moment,
    /// and that is a mode this process could have set on its own file anyway. Nothing holding
    /// the name, or the name being impossible to describe, means nothing to carry.
    /// </para>
    /// <para>
    /// <strong>The Windows read-only flag is left behind.</strong> A read-only file on Windows
    /// refuses to be replaced by a rename, so a publish over one fails whatever the scratch
    /// file carries — and a scratch file that carried the flag would then refuse to be
    /// removed as well, leaving it behind as litter. The flag can only ever reach a published
    /// file by losing that race, so it is not carried.
    /// </para>
    /// </remarks>
    private static CapPermissions? Replaced(IDir directory, string name)
    {
        if (!directory.TryGetMetadata(name, out CapMetadata existing) || existing.Type != CapFileType.File)
        {
            return null;
        }

        CapPermissions permissions = existing.Permissions;
        if (permissions.TryGetWindowsAttributes(out FileAttributes attributes))
        {
            return CapPermissions.FromWindowsAttributes(attributes & ~FileAttributes.ReadOnly);
        }

        return permissions.TryGetUnixMode(out _) ? permissions : null;
    }

    /// <summary>
    /// Whether the scratch file is created so that only its owner can read it: when the file it
    /// replaces carries a Unix mode, which is given to it afterwards.
    /// </summary>
    /// <remarks>
    /// Without a mode to give it afterwards the scratch file must be created as any new file
    /// is, because what a new file gets is what the published one is meant to have.
    /// </remarks>
    private static bool OwnerOnlyFor(CapPermissions? carried) =>
        carried is { } permissions && permissions.TryGetUnixMode(out _);

    /// <summary>
    /// Gives the scratch file the permissions of the file it will replace, once its contents
    /// are written.
    /// </summary>
    /// <remarks>
    /// After the contents, so that a mode forbidding writes cannot get in their way, and
    /// before the commit, so that the mode is committed with them.
    /// </remarks>
    private static void Carry(ICapFile file, CapPermissions? carried, string name)
    {
        if (carried is not { } permissions)
        {
            return;
        }

        if (file is not CapFile concrete)
        {
            file.SetPermissions(permissions);
            return;
        }

        CapError error = concrete.SetPermissionsCore(permissions);
        if (error.IsFailure)
        {
            throw FailureTranslation.ToException(error, name, ExpectedTarget.Name);
        }
    }

    /// <summary>
    /// Creates a file under a name nobody can predict, in the directory the published file
    /// will end up in.
    /// </summary>
    /// <returns>The name that was claimed.</returns>
    /// <remarks>
    /// <para>
    /// <strong>The same directory, always.</strong> A scratch file written somewhere else —
    /// the system's temporary location being the usual choice — cannot be moved onto the
    /// target when the two turn out to be on different filesystems, and the fallback every
    /// library reaches for at that point is a copy, which is exactly the thing this operation
    /// exists not to do.
    /// </para>
    /// <para>
    /// The name is drawn from the library's unguessable generator and claimed exclusively, so
    /// the file is this caller's or the creation fails; nothing is opened that somebody else
    /// prepared. A name already taken is drawn again, because a collision at that width is
    /// not chance but somebody guessing, and the answer to guessing is another draw.
    /// </para>
    /// <para>
    /// The last attempt is made in the form that explains itself, so that a directory
    /// refusing every creation for some other reason — no permission, no space, a read-only
    /// mount — is reported as that reason rather than as an improbable run of collisions.
    /// </para>
    /// <para>
    /// Asked for <paramref name="ownerOnly"/>, a <see cref="Dir"/> creates the file so that no
    /// other account can read it, in the creating open itself, as a scratch file of the
    /// library's own is made. Any other handle has no way to ask for that and creates it as it
    /// creates any file.
    /// </para>
    /// </remarks>
    private static string Claim(IDir directory, bool asynchronous, bool ownerOnly, out ICapFile file)
    {
        FileOptions options = asynchronous ? FileOptions.Asynchronous : FileOptions.None;

        if (ownerOnly && directory is Dir concrete)
        {
            return ClaimOwned(concrete, options, out file);
        }

        for (int attempt = 1; attempt < TemporaryNames.Attempts; attempt++)
        {
            string candidate = TemporaryNames.Next();
            if (directory.TryOpenFile(
                    candidate, FileMode.CreateNew, FileAccess.Write, FileShare.Read, options, 0,
                    append: false, noFollow: false, out ICapFile? created))
            {
                file = created;
                return candidate;
            }
        }

        string last = TemporaryNames.Next();
        file = directory.OpenFile(last, FileMode.CreateNew, FileAccess.Write, FileShare.Read, options);
        return last;
    }

    /// <summary>
    /// Claims a scratch name as <see cref="Claim"/> does, creating the file so that only its
    /// owner can read it.
    /// </summary>
    private static string ClaimOwned(Dir directory, FileOptions options, out ICapFile file)
    {
        CapError error = CapError.FromCategory(CapErrorCategory.AlreadyExists);

        for (int attempt = 0; attempt < TemporaryNames.Attempts; attempt++)
        {
            string candidate = TemporaryNames.Next();
            error = directory.CreateOwnedFile(candidate, options, out CapFile? created);
            if (error.IsSuccess)
            {
                file = created!;
                return candidate;
            }

            // Only a name already taken is worth another draw; any other refusal would be
            // repeated under every name.
            if (error.Category != CapErrorCategory.AlreadyExists)
            {
                break;
            }
        }

        throw FailureTranslation.ToException(error, ScratchDescription, ExpectedTarget.Parent);
    }

    /// <summary>How a scratch file is described in a failure message.</summary>
    private const string ScratchDescription = "a scratch file";

    /// <summary>Commits the contents, if the caller asked for the contents to be committed.</summary>
    private static void Commit(ICapFile file, Durability durability)
    {
        if (durability != Durability.None)
        {
            file.Flush(toDisk: true);
        }
    }

    /// <summary>
    /// Moves the scratch name onto the real one and commits the directory, if asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replacement is asked for, so the name is published whether or not something already
    /// holds it, and the two states — old contents, new contents — are the only ones any
    /// reader can observe.
    /// </para>
    /// <para>
    /// A platform with no way to commit a directory says so, and that answer is accepted
    /// rather than turned into a failure: the caller has been told, in the documentation of
    /// the setting they chose, that this step does not happen there. Every other failure is
    /// reported, because it means the directory could have been committed and was not.
    /// </para>
    /// <para>
    /// A <see cref="Dir"/> is committed directly, so a failure names the file being published.
    /// Any other handle is asked through <see cref="IDir.Flush"/>, whose false answer is the
    /// same "no way to commit a directory here" and is accepted in the same way; a failure is
    /// whatever that implementation throws.
    /// </para>
    /// </remarks>
    private static void Publish(IDir directory, string scratch, string name, Durability durability)
    {
        directory.Rename(scratch, directory, name, replaceExisting: true);

        if (durability != Durability.FileAndDirectory)
        {
            return;
        }

        if (directory is not Dir concrete)
        {
            _ = directory.Flush(toDisk: true);
            return;
        }

        CapError synced = concrete.SyncContents();
        if (synced.IsFailure && synced.Category != CapErrorCategory.NotSupported)
        {
            throw FailureTranslation.ToException(synced, name, ExpectedTarget.Name);
        }
    }

    /// <summary>Removes a scratch name that never became a file.</summary>
    /// <remarks>
    /// Reported failures are dropped. This runs from a <c>finally</c>, where the interesting
    /// exception is the one already on its way out, and a scratch name that cannot be removed
    /// is a stray file rather than a reason to replace that exception with one about tidying
    /// up.
    /// </remarks>
    private static void Abandon(IDir directory, string? scratch)
    {
        if (scratch is not null)
        {
            _ = directory.TryDeleteFile(scratch);
        }
    }
}
