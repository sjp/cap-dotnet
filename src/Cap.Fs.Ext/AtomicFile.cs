using Cap.Std;

namespace Cap.Fs.Ext;

/// <summary>
/// A file being written under a scratch name, to be published onto its real name in one
/// move once it is complete.
/// </summary>
/// <remarks>
/// <para>
/// The streaming form of <see cref="DirExtensions.WriteAllBytesAtomic(IDir, string, ReadOnlySpan{byte}, Durability)"/>,
/// for contents that are produced a piece at a time and should not have to be held in memory
/// first: a serialiser writing to a stream, a body arriving from the network, an archive
/// being assembled. <see cref="DirExtensions.OpenAtomicWrite(IDir, string, Durability, bool)"/>
/// claims the scratch name; the caller writes through <see cref="Stream"/> or
/// <see cref="File"/>; <see cref="Commit"/> moves it onto the name. Disposing it without
/// committing removes the scratch name and leaves the real one as it was.
/// </para>
/// <code>
/// using AtomicFile publish = dir.OpenAtomicWrite("state.json");
/// await JsonSerializer.SerializeAsync(publish.Stream, state, cancellationToken);
/// publish.Commit();
/// </code>
/// <para>
/// Everything <see cref="DirExtensions.WriteAllBytesAtomic(IDir, string, ReadOnlySpan{byte}, Durability)"/>
/// promises holds here, because that operation is this one with the contents written in a
/// single call: the scratch file sits beside the target and is created exclusively under a
/// name nobody can predict, the last component is replaced as a name and never followed, a
/// reader sees the whole old file or the whole new one, and a failure publishes nothing.
/// </para>
/// <para>
/// <strong>Permissions.</strong> Under the default options the permissions of a file already
/// holding the name are read when the scratch file is claimed, not when it is committed, since
/// they decide how the scratch file is created. A change made to them while the contents are
/// being written is not carried.
/// </para>
/// <para>
/// <strong>Not safe to use from more than one thread at once.</strong> The stream has a
/// position and usually a buffer, and committing hands the file over; one caller at a time
/// owns this object, as with any stream.
/// </para>
/// </remarks>
public sealed class AtomicFile : IDisposable, IAsyncDisposable
{
    private readonly ParentLocation _location;
    private readonly ICapFile _file;
    private readonly Durability _durability;
    private readonly CapPermissions? _carried;
    private string? _scratch;
    private Stream? _stream;
    private bool _spent;
    private bool _disposed;

    private AtomicFile(ParentLocation location, ICapFile file, string scratch, Durability durability, CapPermissions? carried)
    {
        _location = location;
        _file = file;
        _scratch = scratch;
        _durability = durability;
        _carried = carried;
    }

    /// <summary>
    /// The scratch file itself, open for writing, for writes that name their own offset.
    /// </summary>
    /// <remarks>
    /// The same file <see cref="Stream"/> writes to. The two can be mixed: the stream keeps a
    /// position of its own, and whatever it has buffered is written out when this is committed.
    /// The file belongs to this object, which closes it on commit and on disposal; do not
    /// dispose it.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">This has been committed, has failed to commit, or has been disposed.</exception>
    public ICapFile File
    {
        get
        {
            ThrowIfSpent();
            return _file;
        }
    }

    /// <summary>
    /// A stream over the scratch file, starting at its beginning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Made the first time it is asked for, and the same stream every time after. It belongs
    /// to this object: <see cref="Commit"/> writes out whatever it holds and closes it before
    /// the move, which is what a platform that will not rename an open file needs. A caller
    /// may still dispose it — a writer that closes the stream it was given is the usual way
    /// — and committing afterwards publishes what it had written.
    /// </para>
    /// <para>
    /// Opened with <c>asynchronous</c> set, the file's stream reads and writes through the
    /// file's own members without a buffer, as <see cref="ICapFile.AsStream"/> describes.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">This has been committed, has failed to commit, or has been disposed.</exception>
    /// <exception cref="CapIOException">The file could not be given a stream.</exception>
    public Stream Stream
    {
        get
        {
            ThrowIfSpent();
            return _stream ??= _file.AsStream(leaveOpen: true);
        }
    }

    /// <summary>Whether the contents have been published onto the name.</summary>
    public bool IsCommitted { get; private set; }

    /// <summary>
    /// Publishes what has been written onto the name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Writes out and closes <see cref="Stream"/>, gives the file the permissions it carries,
    /// commits the contents and closes the file, moves the scratch name onto the real one and
    /// commits the directory — each commit only as far as the <see cref="Durability"/> this
    /// was opened with asks.
    /// </para>
    /// <para>
    /// One attempt only. Whether it succeeds or fails, this object is spent afterwards: a
    /// second call throws, and so do <see cref="File"/> and <see cref="Stream"/>. After a
    /// failure the name is as it was, and disposing this removes the scratch name.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This has already been committed, or a commit has already failed.</exception>
    /// <exception cref="ObjectDisposedException">This has been disposed.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="CapIOException">
    /// The write, the permissions or the move failed, or on Windows a link to a directory
    /// holds the name.
    /// </exception>
    public void Commit()
    {
        BeginCommit();
        _stream?.Dispose();
        Finish();
    }

    /// <summary>
    /// Publishes what has been written onto the name, writing out the stream without holding
    /// the calling thread.
    /// </summary>
    /// <param name="cancellationToken">Asks for the publish to be abandoned before the move.</param>
    /// <remarks>
    /// <para>
    /// The same operation as <see cref="Commit"/>. Only writing out <see cref="Stream"/> is
    /// asynchronous; committing the contents, moving the name and committing the directory
    /// happen on the calling thread, as they do for
    /// <see cref="DirExtensions.WriteAllBytesAtomicAsync(IDir, string, ReadOnlyMemory{byte}, Durability, CancellationToken)"/>
    /// and for the same reason.
    /// </para>
    /// <para>
    /// Cancellation is noticed up to the move and not after it. A cancelled commit is a
    /// failed one: the name is as it was, and disposing this removes the scratch name.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This has already been committed, or a commit has already failed.</exception>
    /// <exception cref="ObjectDisposedException">This has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the write.</exception>
    /// <exception cref="CapIOException">
    /// The write, the permissions or the move failed, or on Windows a link to a directory
    /// holds the name.
    /// </exception>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        BeginCommit();

        if (_stream is not null)
        {
            // A stream the caller has already closed has nothing left to write, and asking it
            // to flush would only report that it is closed.
            if (_stream.CanWrite)
            {
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Finish();
    }

    /// <summary>
    /// Closes the file and, unless it was committed, removes the scratch name and leaves the
    /// real one as it was.
    /// </summary>
    /// <remarks>
    /// A failure to remove the scratch name is not reported: disposal is where an exception
    /// already on its way out is usually passing through, and a stray scratch file is not a
    /// reason to replace it. The name is recognisable and sits beside the target, as after a
    /// crash.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _spent = true;

        try
        {
            if (_stream is not null)
            {
                DiscardStream(_stream);
            }

            _file.Dispose();
            DirExtensions.Abandon(_location.Directory, _scratch);
        }
        finally
        {
            _location.Dispose();
        }
    }

    /// <summary>
    /// Closes the file and, unless it was committed, removes the scratch name, without
    /// holding the calling thread while the stream is closed.
    /// </summary>
    /// <remarks>As <see cref="Dispose"/> describes.</remarks>
    /// <returns>A task that completes once everything is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_stream is not null && !_spent)
        {
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The contents are being thrown away, so a failure to write them out is not news.
            }

            _stream = null;
        }

        Dispose();
    }

    /// <summary>
    /// Resolves the parent, claims a scratch name beside the target and hands back the open
    /// file.
    /// </summary>
    internal static AtomicFile Open(IDir dir, string path, Durability durability, bool preservePermissions, bool asynchronous)
    {
        ParentLocation location = ParentLocation.Resolve(dir, path, nameof(path), mayNameDirectory: false);
        try
        {
            CapPermissions? carried = preservePermissions ? DirExtensions.Replaced(location.Directory, location.Name) : null;
            string scratch = DirExtensions.Claim(
                location.Directory, asynchronous, DirExtensions.OwnerOnlyFor(carried), out ICapFile file);

            return new AtomicFile(location, file, scratch, durability, carried);
        }
        catch
        {
            location.Dispose();
            throw;
        }
    }

    /// <summary>Checks this can still be committed, and marks it as no longer so.</summary>
    private void BeginCommit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_spent)
        {
            throw new InvalidOperationException(
                IsCommitted
                    ? "This file has already been published."
                    : "An earlier attempt to publish this file failed; open a new one to try again.");
        }

        _spent = true;
    }

    /// <summary>The steps of a commit after the stream is written out.</summary>
    private void Finish()
    {
        using (_file)
        {
            DirExtensions.Carry(_file, _carried, _location.Name);
            DirExtensions.Commit(_file, _durability);
        }

        DirExtensions.Publish(_location.Directory, _scratch!, _location.Name, _durability);
        _scratch = null;
        IsCommitted = true;
    }

    /// <summary>
    /// Closes a stream whose contents are being thrown away, ignoring a failure to write out
    /// what it held.
    /// </summary>
    private static void DiscardStream(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // As in DisposeAsync.
        }
    }

    private void ThrowIfSpent() => ObjectDisposedException.ThrowIf(_spent, this);
}
