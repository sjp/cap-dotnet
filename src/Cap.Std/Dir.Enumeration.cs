using System.Runtime.CompilerServices;
using Cap.Primitives.Interop;

namespace Cap.Std;

/// <summary>
/// Reading what a directory holds.
/// </summary>
/// <remarks>
/// <para>
/// The part of the surface with the most room to go quietly wrong, because the obvious
/// design for it hands back paths. A path is a string, and a string is the form in which
/// authority escapes: the caller joins it to something, or passes it to a framework call
/// that resolves it with the process's own privileges, and the subtree the handle was
/// supposed to bound stops bounding anything. So what comes back is an entry carrying a
/// single name and the handle it was read through, and the only way to act on it is through
/// that handle.
/// </para>
/// <para>
/// <strong>Recursion is not here.</strong> Walking a tree is a sequence of enumerations, one
/// per directory, each through a handle opened from the one above it — a caller writing that
/// gets containment for free, and a convenience that returned a flattened list of paths
/// would be back to handing out strings. The convenience layer offers the walk, expressed
/// over handles.
/// </para>
/// </remarks>
public sealed partial class Dir
{
    /// <summary>
    /// How many entries the asynchronous form gathers per trip to the thread pool.
    /// </summary>
    /// <remarks>
    /// The cost being amortised is the scheduling, not the reading: the read itself already
    /// fills a large buffer per system call, and handing each entry back through its own
    /// scheduled work item would cost more than reading it did. Large enough that the
    /// scheduling disappears into the IO, small enough that a caller who stops early has not
    /// paid for much it never saw, and that a cancellation is noticed promptly.
    /// </remarks>
    private const int AsyncBatchSize = 64;

    /// <summary>
    /// Reads the entries of this directory.
    /// </summary>
    /// <returns>
    /// The entries, in whatever order the filesystem holds them, excluding the two names
    /// every directory has for itself and for the one above it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Lazy: the directory is opened for reading when enumeration begins and closed when it
    /// ends, so the result can be enumerated more than once and each pass reads afresh. A
    /// caller that stops early stops the reading, which for a large directory is most of the
    /// work.
    /// </para>
    /// <para>
    /// <strong>This is not a snapshot,</strong> on any platform, and the guarantee is
    /// deliberately weaker than it looks. An entry that is present for the whole enumeration
    /// is returned; an entry created or removed while the enumeration runs may or may not
    /// appear, and no ordering between the two is promised. Nothing here can do better — the
    /// filesystems do not offer it — and code that needs a consistent view of a directory
    /// has to get it some other way than by reading the directory.
    /// </para>
    /// <para>
    /// A failure part of the way through is thrown from the enumeration rather than
    /// swallowed, so a caller that has already been handed some entries learns that there
    /// were more it did not see.
    /// </para>
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">
    /// The filesystem refused to let the directory be read, or this handle was opened only
    /// to resolve names beneath it and not to list them.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">The directory has since been removed.</exception>
    /// <exception cref="CapIOException">The directory could not be read.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public IEnumerable<DirEntry> EnumerateEntries()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        return Enumerate();
    }

    /// <summary>
    /// Reads the entries of this directory without occupying the calling thread.
    /// </summary>
    /// <param name="cancellationToken">Stops the enumeration between batches of entries.</param>
    /// <returns>The entries, as <see cref="EnumerateEntries"/> returns them.</returns>
    /// <remarks>
    /// <para>
    /// <strong>The reading is not asynchronous, and nothing here claims it is.</strong> No
    /// operating system this runs on offers a directory read that completes by itself: the
    /// calls are the blocking kind everywhere, so what this does is have a thread-pool thread
    /// do the waiting a batch at a time. That is worth having — the caller's thread is free,
    /// and a long enumeration becomes cancellable — and it is not the same thing as work the
    /// system performs while nobody waits.
    /// </para>
    /// <para>
    /// The token is observed between batches rather than during one. A read already in the
    /// hands of the filesystem runs to completion, so cancelling stops the enumeration
    /// promptly rather than instantly.
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The filesystem refused to let the directory be read, or this handle was opened only
    /// to resolve names beneath it and not to list them.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">The directory has since been removed.</exception>
    /// <exception cref="CapIOException">The directory could not be read.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been disposed.</exception>
    public IAsyncEnumerable<DirEntry> EnumerateEntriesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        return EnumerateAsync(cancellationToken);
    }

    private IEnumerable<DirEntry> Enumerate()
    {
        using DirectoryReader reader = BeginRead();

        while (true)
        {
            CapError error = reader.Read(out bool advanced);
            if (error.IsFailure)
            {
                throw FailureTranslation.ToEnumerationException(error);
            }

            if (!advanced)
            {
                yield break;
            }

            // The name is copied out here and nowhere else. The reader hands back a view of
            // storage it reuses, so this is the one allocation an entry costs, and an
            // enumeration that is scanning for something in particular pays it for every
            // entry either way -- there is no shape of this API that hands back a borrowed
            // name and still lets a caller keep one.
            yield return new DirEntry(this, reader.CurrentName.ToString(), reader.CurrentType);
        }
    }

    private async IAsyncEnumerable<DirEntry> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        DirectoryReader? reader = null;
        List<DirEntry> batch = new(AsyncBatchSize);

        try
        {
            bool more;
            do
            {
                // Opening the directory for reading is a blocking call too, so it happens on
                // the pool with the first batch rather than on the caller's thread before
                // anything has been awaited.
                more = await Task.Run(
                    () =>
                    {
                        reader ??= BeginRead();
                        return FillBatch(reader, batch);
                    },
                    cancellationToken).ConfigureAwait(false);

                foreach (DirEntry entry in batch)
                {
                    yield return entry;
                }
            }
            while (more);
        }
        finally
        {
            reader?.Dispose();
        }
    }

    /// <summary>
    /// Gathers up to a batch of entries.
    /// </summary>
    /// <returns>False when the directory has been read to its end.</returns>
    private bool FillBatch(DirectoryReader reader, List<DirEntry> batch)
    {
        batch.Clear();

        while (batch.Count < AsyncBatchSize)
        {
            CapError error = reader.Read(out bool advanced);
            if (error.IsFailure)
            {
                throw FailureTranslation.ToEnumerationException(error);
            }

            if (!advanced)
            {
                return false;
            }

            batch.Add(new DirEntry(this, reader.CurrentName.ToString(), reader.CurrentType));
        }

        return true;
    }

    /// <summary>
    /// Opens this directory for reading, with a position of its own.
    /// </summary>
    /// <remarks>
    /// Every enumeration gets its own, because the position a directory read advances belongs
    /// to the open object rather than to the handle. Two enumerations of one handle sharing
    /// a position would each see about half the directory and neither would be told.
    /// </remarks>
    private DirectoryReader BeginRead()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

        CapResult<DirectoryReader> opened = PlatformOps.Current.OpenDirectoryReader(_handle);
        return opened.IsSuccess
            ? opened.Value
            : throw FailureTranslation.ToEnumerationException(opened.Error);
    }
}
