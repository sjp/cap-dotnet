using System.Text;
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
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name for a file.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="CapIOException">The write or the move failed.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static void WriteAllBytesAtomic(
        this Dir dir,
        string path,
        ReadOnlySpan<byte> bytes,
        Durability durability = Durability.FileAndDirectory)
    {
        using ParentLocation location = ParentLocation.Resolve(dir, path, nameof(path), mayNameDirectory: false);

        string? scratch = Claim(location.Directory, asynchronous: false, out CapFile file);
        try
        {
            using (file)
            {
                file.Write(bytes, 0);
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
    /// Writes a text file beneath this handle so that no reader ever sees it half-written.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the file to publish.</param>
    /// <param name="contents">The text to store.</param>
    /// <param name="durability">How far the write is pushed before it is treated as done.</param>
    /// <remarks>
    /// Encoded as UTF-8 with no byte-order mark, which is what the framework's own text write
    /// produces and what the matching read here assumes when a file begins with no mark. A
    /// caller who needs some other encoding encodes it themselves and publishes the bytes.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name for a file.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="CapIOException">The write or the move failed.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static void WriteAllTextAtomic(
        this Dir dir,
        string path,
        string contents,
        Durability durability = Durability.FileAndDirectory)
    {
        ArgumentNullException.ThrowIfNull(contents);

        WriteAllBytesAtomic(dir, path, Encoding.UTF8.GetBytes(contents), durability);
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
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name for a file.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="CapIOException">The write or the move failed.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static async Task WriteAllBytesAtomicAsync(
        this Dir dir,
        string path,
        ReadOnlyMemory<byte> bytes,
        Durability durability = Durability.FileAndDirectory,
        CancellationToken cancellationToken = default)
    {
        using ParentLocation location = ParentLocation.Resolve(dir, path, nameof(path), mayNameDirectory: false);

        string? scratch = Claim(location.Directory, asynchronous: true, out CapFile file);
        try
        {
            using (file)
            {
                await file.WriteAsync(bytes, 0, cancellationToken).ConfigureAwait(false);
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
    /// Publishes a text file beneath this handle without holding the calling thread.
    /// </summary>
    /// <param name="dir">The handle the path is relative to.</param>
    /// <param name="path">A relative path to the file to publish.</param>
    /// <param name="contents">The text to store.</param>
    /// <param name="durability">How far the write is pushed before it is treated as done.</param>
    /// <param name="cancellationToken">Asks for the write to be abandoned.</param>
    /// <remarks>Encoded as <see cref="WriteAllTextAtomic"/> describes.</remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a usable name for a file.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> named something outside this handle's authority.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">A directory above the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="CapIOException">The write or the move failed.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public static Task WriteAllTextAtomicAsync(
        this Dir dir,
        string path,
        string contents,
        Durability durability = Durability.FileAndDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);

        return WriteAllBytesAtomicAsync(dir, path, Encoding.UTF8.GetBytes(contents), durability, cancellationToken);
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
    /// </remarks>
    private static string Claim(Dir directory, bool asynchronous, out CapFile file)
    {
        FileOptions options = asynchronous ? FileOptions.Asynchronous : FileOptions.None;

        for (int attempt = 1; attempt < TemporaryNames.Attempts; attempt++)
        {
            string candidate = TemporaryNames.Next();
            if (directory.TryOpenFile(
                    candidate, FileMode.CreateNew, FileAccess.Write, FileShare.Read, options, 0,
                    out CapFile? created))
            {
                file = created;
                return candidate;
            }
        }

        string last = TemporaryNames.Next();
        file = directory.OpenFile(last, FileMode.CreateNew, FileAccess.Write, FileShare.Read, options);
        return last;
    }

    /// <summary>Commits the contents, if the caller asked for the contents to be committed.</summary>
    private static void Commit(CapFile file, Durability durability)
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
    /// </remarks>
    private static void Publish(Dir directory, string scratch, string name, Durability durability)
    {
        directory.Rename(scratch, directory, name, replaceExisting: true);

        if (durability != Durability.FileAndDirectory)
        {
            return;
        }

        CapError synced = directory.SyncContents();
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
    private static void Abandon(Dir directory, string? scratch)
    {
        if (scratch is not null)
        {
            _ = directory.TryDeleteFile(scratch);
        }
    }
}
