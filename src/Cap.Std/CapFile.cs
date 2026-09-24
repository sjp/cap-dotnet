using Cap.Primitives.Interop;
using Microsoft.Win32.SafeHandles;

namespace Cap.Std;

/// <summary>
/// An open file, reached through a capability and read or written by position.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="Dir"/> for the thing at the end of a path. It exists
/// because the authority to read or write a file has to be something a component can be
/// handed and can hold, rather than something it re-derives from a string every time it acts
/// — and because once a handle exists, the name it was opened by has stopped mattering
/// entirely. Nothing here takes a path.
/// </para>
/// <para>
/// <strong>It is a handle and not a stream.</strong> Every read and write says where in the
/// file it applies, and no position is carried between calls. That is what makes the same
/// handle usable from several threads at once without any of them agreeing about whose turn
/// it is, and it is why nothing here buffers: a buffer only pays for itself where reads
/// follow one another, which is a stream's assumption rather than a file's. A caller who
/// wants the stream's assumptions asks for <see cref="AsStream"/> and gets the framework's
/// own implementation of them.
/// </para>
/// <para>
/// <strong>Disposing this closes the file.</strong> A stream taken from it is given a
/// separate handle to the same open file, so the two are disposed independently and in
/// either order; the exception is a stream asked for with ownership, which takes this one's
/// handle and leaves it spent.
/// </para>
/// <para>
/// <strong>Symbolic links.</strong> A handle never refers to a link. Any link on the path it
/// was opened by, the last component included, was followed or refused under the opening
/// directory's policy at the moment of the open; what is held afterwards is the object that
/// resolution arrived at, and no member here consults a name again.
/// </para>
/// </remarks>
public sealed class CapFile : IDisposable
{
    /// <summary>
    /// The buffer a stream taken from this handle is given, when the caller names no size.
    /// </summary>
    /// <remarks>
    /// The framework's own default for the same thing, so that a caller who moves a stream
    /// from a path-based API onto this one gets the performance they had rather than a
    /// number chosen here.
    /// </remarks>
    private const int DefaultStreamBufferSize = 4096;

    private readonly SafeFileHandle _handle;
    private readonly FileAccess _access;
    private readonly bool _isAsync;
    private bool _given;

    internal CapFile(SafeFileHandle handle, FileAccess access, bool isAsync)
    {
        _handle = handle;
        _access = access;
        _isAsync = isAsync;
    }

    /// <summary>What this handle may do with the file's contents.</summary>
    /// <remarks>
    /// <para>
    /// Fixed when the file was opened. There is no way to widen it, for the same reason a
    /// directory handle cannot be widened: a component given a handle to read a file has been
    /// given exactly that, and a method that turned it into a writable one would make the
    /// grant meaningless.
    /// </para>
    /// <para>Safe to read from any thread.</para>
    /// </remarks>
    public FileAccess Access => _access;

    /// <summary>
    /// Whether the operating system itself completes this handle's reads and writes, rather
    /// than a thread waiting on them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Decided when the file was opened and not afterwards, because that is the only time
    /// the question can be answered: a handle is made capable of it or it is not, and
    /// nothing can change which. Asking for the wrong one is not an error anybody notices —
    /// the reads and writes still work, and each of them occupies a thread that was supposed
    /// to be doing something else.
    /// </para>
    /// <para>
    /// <strong>False on every system but Windows, whatever was asked for.</strong> There is
    /// nothing there for it to be true of: a read of a file on those systems is a call that
    /// returns when the data is there, and what the asynchronous methods offer is to make
    /// some other thread do that waiting. They are still worth calling — they keep the
    /// caller's thread free — but the waiting has not gone anywhere, and a property that
    /// claimed otherwise would be describing a capability the platform does not have.
    /// </para>
    /// <para>Safe to read from any thread.</para>
    /// </remarks>
    public bool IsAsync => _isAsync;

    /// <summary>The file's current length in bytes.</summary>
    /// <remarks>
    /// <para>
    /// Safe to read from any thread, including while other threads read, write or resize the
    /// file through this handle; the answer is whatever length the file had when the
    /// operating system was asked, which a concurrent write may already have changed.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> The length of the object this handle refers to, never
    /// of a link: any link was resolved when the file was opened.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">This handle has been closed or given away.</exception>
    public long Length
    {
        get
        {
            Demand();
            return RandomAccess.GetLength(_handle);
        }
    }

    /// <summary>
    /// Describes the file this handle refers to.
    /// </summary>
    /// <returns>A snapshot of the file, taken at the moment of the call.</returns>
    /// <remarks>
    /// <para>
    /// Asked of the object rather than of a name, so there is no path to resolve and nothing
    /// for a rename to interfere with. A file whose last name has been removed while this
    /// handle was held still answers, and the length it reports is the length of the object
    /// this handle writes to, not of whatever now holds the name it was opened by.
    /// </para>
    /// <para>
    /// The richer form of <see cref="Length"/>, and one call rather than several: the length,
    /// the times and the permissions all come from the same query, so they describe one
    /// instant. Where only the length is wanted, <see cref="Length"/> is the cheaper
    /// question.
    /// </para>
    /// <para>
    /// Safe to call from any thread, concurrently with any other member. A disposal racing
    /// the call on another thread ends as <see cref="ObjectDisposedException"/>, never as a
    /// description of whatever else has since been given the handle's number.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> The type reported is never
    /// <see cref="Cap.Primitives.CapFileType.Symlink"/>: the handle refers to the object a
    /// link led to, because the link was followed, or refused, when the file was opened. To
    /// describe a link itself, ask the directory that holds it about the name.
    /// </para>
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the question.</exception>
    /// <exception cref="CapIOException">The question could not be answered.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been closed or given away.</exception>
    public CapMetadata GetMetadata()
    {
        Demand();

        CapError error = PlatformOps.Current.DescribeHandle(_handle, out CapNodeStat stat);
        return error.IsSuccess ? new CapMetadata(stat) : throw FailureTranslation.ToHandleException(error);
    }

    /// <summary>
    /// Sets the file's length, truncating it or extending it with zeroes.
    /// </summary>
    /// <param name="length">The length in bytes.</param>
    /// <remarks>
    /// <para>
    /// Safe to call from any thread. It is not ordered against writes made concurrently on
    /// other threads: a write that lands beyond the new length after the resize extends the
    /// file again, and which of the two the operating system applies first is its choice.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> Acts on the object this handle refers to; no name is
    /// consulted, so there is no link to follow.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    /// <exception cref="UnauthorizedAccessException">This handle cannot write.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been closed or given away.</exception>
    public void SetLength(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Demand();
        RandomAccess.SetLength(_handle, length);
    }

    /// <summary>
    /// Waits for what has been written to reach the storage device.
    /// </summary>
    /// <param name="toDisk">
    /// Whether to wait. False does nothing at all, and says so: nothing here holds written
    /// data back, so there is no buffer of this library's own for a flush to empty. The
    /// parameter exists because a caller arriving from a stream expects to be able to ask
    /// the weaker question, and answering it with silence is more honest than answering it
    /// with a disk flush they did not ask for.
    /// </param>
    /// <remarks>
    /// <para>
    /// A write that has returned is a write the operating system has accepted, which is not
    /// the same as one the hardware has stored. The difference is invisible until the power
    /// fails, so the choice belongs to the caller: durability costs a great deal on some
    /// devices and nothing on others, and there is no default that is right for both.
    /// </para>
    /// <para>
    /// Safe to call from any thread. What it waits for is the writes that had returned before
    /// it was called; a write still in progress on another thread may or may not be included.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">This handle has been closed or given away.</exception>
    public void Flush(bool toDisk)
    {
        Demand();

        if (toDisk)
        {
            RandomAccess.FlushToDisk(_handle);
        }
    }

    /// <summary>
    /// Writes permissions onto the file this handle refers to.
    /// </summary>
    /// <param name="permissions">A value read from some other object's snapshot.</param>
    /// <remarks>
    /// Applied through the handle, so the object whose permissions change is the one that was
    /// opened and not whatever its name has come to mean since. Internal for the reason the
    /// directory's counterpart is: it exists to reproduce an object, not to let a caller
    /// revise one.
    /// </remarks>
    internal CapError SetPermissions(in CapPermissions permissions)
    {
        Demand();

        return PlatformOps.Current.SetHandlePermissions(
            _handle, permissions.UnixMode, permissions.WindowsAttributes);
    }

    /// <summary>
    /// Reads from a given position in the file.
    /// </summary>
    /// <param name="buffer">Where the bytes go.</param>
    /// <param name="fileOffset">Where in the file to read from.</param>
    /// <returns>
    /// How many bytes were read, which may be fewer than the buffer holds and is zero at the
    /// end of the file. A short read is not an error and not a promise that the rest is
    /// absent; a caller that needs the whole buffer filled asks again from further on.
    /// </returns>
    /// <remarks>
    /// Safe to call from any thread, concurrently with other reads and writes on the same
    /// handle: each call carries its own offset and moves no shared position. A read that
    /// overlaps a concurrent write to the same range may see some, all or none of it — the
    /// operating system does not promise that a write is observed whole.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fileOffset"/> is negative.</exception>
    /// <exception cref="UnauthorizedAccessException">This handle cannot read.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been closed or given away.</exception>
    public int Read(Span<byte> buffer, long fileOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileOffset);
        Demand();
        return RandomAccess.Read(_handle, buffer, fileOffset);
    }

    /// <summary>
    /// Writes at a given position in the file.
    /// </summary>
    /// <param name="buffer">The bytes to write.</param>
    /// <param name="fileOffset">Where in the file to write them.</param>
    /// <remarks>
    /// <para>
    /// Writes all of the buffer, repeating the call underneath if the system accepts only
    /// part of it. A file opened to append is the exception, and the exception is the
    /// platform's rather than this library's: such a handle puts every write at the end of
    /// the file whatever offset it is given, because appending is a property of how the file
    /// was opened and is applied by the operating system.
    /// </para>
    /// <para>
    /// Safe to call from any thread, concurrently with other reads and writes on the same
    /// handle. Concurrent writes to overlapping ranges are not ordered against each other, and
    /// the file may end up holding bytes from either.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fileOffset"/> is negative.</exception>
    /// <exception cref="UnauthorizedAccessException">This handle cannot write.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been closed or given away.</exception>
    public void Write(ReadOnlySpan<byte> buffer, long fileOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileOffset);
        Demand();
        RandomAccess.Write(_handle, buffer, fileOffset);
    }

    /// <summary>
    /// Reads from a given position in the file without holding the calling thread.
    /// </summary>
    /// <param name="buffer">Where the bytes go.</param>
    /// <param name="fileOffset">Where in the file to read from.</param>
    /// <param name="cancellationToken">Asks for the read to be abandoned.</param>
    /// <returns>How many bytes were read. See <see cref="Read"/> for what a short count means.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Cancellation is best-effort and is not a guarantee about the file.</strong>
    /// A read already in the hands of the operating system is usually not recallable, so what
    /// cancelling reliably does is release the caller — the read may still complete, and a
    /// write may still reach the file. It is honoured before the operation starts on every
    /// platform, and during it only where the platform provides a way to say so.
    /// </para>
    /// <para>
    /// Safe to call from any thread, with as many reads and writes outstanding on the same
    /// handle as the caller likes, for the same reason <see cref="Read"/> is: no position is
    /// shared between them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fileOffset"/> is negative.</exception>
    /// <exception cref="UnauthorizedAccessException">This handle cannot read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been closed or given away.</exception>
    public ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        long fileOffset,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileOffset);
        Demand();
        return RandomAccess.ReadAsync(_handle, buffer, fileOffset, cancellationToken);
    }

    /// <summary>
    /// Writes at a given position in the file without holding the calling thread.
    /// </summary>
    /// <param name="buffer">The bytes to write.</param>
    /// <param name="fileOffset">Where in the file to write them.</param>
    /// <param name="cancellationToken">Asks for the write to be abandoned.</param>
    /// <remarks>
    /// <para>
    /// Cancellation carries the same caveat as it does for a read, and carries it more
    /// sharply: a write that was abandoned may still have reached the file, in whole or in
    /// part. Cancelling releases the caller and says nothing about what is now on disk.
    /// </para>
    /// <para>
    /// Safe to call from any thread, with other reads and writes outstanding on the same
    /// handle; as with <see cref="Write"/>, writes to overlapping ranges are not ordered
    /// against each other.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fileOffset"/> is negative.</exception>
    /// <exception cref="UnauthorizedAccessException">This handle cannot write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
    /// <exception cref="ObjectDisposedException">This handle has been closed or given away.</exception>
    public ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        long fileOffset,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileOffset);
        Demand();
        return RandomAccess.WriteAsync(_handle, buffer, fileOffset, cancellationToken);
    }

    /// <summary>
    /// Presents the file as a <see cref="FileStream"/>, for code that wants a position, a
    /// buffer and the rest of the stream vocabulary.
    /// </summary>
    /// <param name="leaveOpen">
    /// Whether this handle stays usable. True, the default, gives the stream a separate
    /// handle to the same open file, so the two are closed independently and in either
    /// order. False hands over this one's handle: the stream closes the file, and this
    /// object is spent — every member on it will report that it has been disposed.
    /// </param>
    /// <param name="bufferSize">
    /// How much the stream buffers. Zero or one turns buffering off, which is what a caller
    /// doing large sequential transfers wants, since a buffer only pays for itself when reads
    /// are smaller than it is.
    /// </param>
    /// <returns>A stream over the file.</returns>
    /// <remarks>
    /// <para>
    /// The stream can read or write exactly what this handle can, which is not a check it
    /// performs but a fact about the handle underneath it: the operating system refused the
    /// wider access when the file was opened, and nothing since has asked for more.
    /// </para>
    /// <para>
    /// A borrowed stream is given a copy of the handle rather than the handle itself,
    /// because a stream constructed over a handle closes that handle when it is disposed and
    /// there is no way to ask it not to. The copy refers to the same open file, with the
    /// same access and the same appending behaviour, so it is a second way to reach one
    /// file rather than a second opinion about what the name meant. That matters: re-opening
    /// by name is exactly what a capability exists to avoid, and a name can hold something
    /// else by the time it is asked again.
    /// </para>
    /// <para>
    /// What the copy does not share is a position. A <see cref="FileStream"/> keeps its own
    /// and reads and writes at it by offset, so each stream taken from this handle moves
    /// independently of every other, and this handle, whose reads and writes all name their
    /// offset, has no position to share.
    /// </para>
    /// <para>
    /// <strong>Thread safety.</strong> The borrowing form is safe to call from any thread.
    /// The stream it returns is not: it is an ordinary <see cref="FileStream"/>, with a
    /// position and a buffer that belong to one caller at a time, so a thread that wants one
    /// of its own should take one of its own. Handing the handle over with
    /// <paramref name="leaveOpen"/> false must not race any other call on this object, since
    /// those calls can no longer tell that the handle has changed owner.
    /// </para>
    /// <para>
    /// <strong>Symbolic links.</strong> No name is consulted, so no link is followed: the
    /// stream reaches the same object this handle does.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">This handle has been closed or given away.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferSize"/> is negative.</exception>
    /// <exception cref="CapIOException">The handle could not be copied.</exception>
    public FileStream AsStream(bool leaveOpen = true, int bufferSize = DefaultStreamBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bufferSize);
        Demand();

        if (!leaveOpen)
        {
            // Marked spent only once the stream exists. A constructor that refuses the
            // handle has taken nothing, and recording the transfer before knowing it
            // happened would leave the file open with nothing left able to close it.
            FileStream owned = new(_handle, _access, bufferSize, _isAsync);
            _given = true;
            return owned;
        }

        CapResult<SafeFileHandle> copy = PlatformOps.Current.DuplicateFile(_handle);
        if (!copy.IsSuccess)
        {
            FailureTranslation.ThrowIfClosed(copy.Error);
            throw new CapIOException(
                $"The open file could not be copied, so no stream could be given one of its " +
                $"own. ({copy.Error})");
        }

        try
        {
            return new FileStream(copy.Value, _access, bufferSize, _isAsync);
        }
        catch
        {
            copy.Value.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Hands out the underlying handle, for interoperation with APIs that take one.
    /// </summary>
    /// <returns>The handle, still owned by this object.</returns>
    /// <remarks>
    /// <para>
    /// Named as it is because it is a hole in the guarantee and should look like one. What
    /// comes back is the authority itself with nothing around it: the caller can pass it to
    /// anything, including something that will keep it, and nothing here can tell that it
    /// happened. A search for this name finds every place authority leaves the library, which
    /// is the whole reason for the name.
    /// </para>
    /// <para>
    /// It is not a transfer. This object still closes the handle when it is disposed, so a
    /// caller that keeps it past that point holds a closed handle — and a descriptor number
    /// that something else may by then have been given.
    /// </para>
    /// <para>
    /// Safe to call from any thread. What the caller does with the handle afterwards is
    /// outside anything this object can keep safe: a disposal on another thread closes it
    /// under them.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">This handle has been closed or given away.</exception>
    public SafeFileHandle UnsafeGetHandle()
    {
        Demand();
        return _handle;
    }

    /// <summary>
    /// Closes the file, unless a stream was given ownership of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Safe to call more than once, and safe to call after ownership was handed to a stream —
    /// in which case it does nothing, because closing a handle somebody else now owns would
    /// close a file they are still using.
    /// </para>
    /// <para>
    /// Safe to call from any thread, including while other threads are using this handle: an
    /// operation already under way keeps the file open until it returns, and one that starts
    /// afterwards throws <see cref="ObjectDisposedException"/>. A stream taken with
    /// <see cref="AsStream"/> in its borrowing form holds a handle of its own and is not
    /// closed by this.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (!_given)
        {
            _handle.Dispose();
        }
    }

    /// <summary>Refuses to act on a handle that is closed or has been given away.</summary>
    private void Demand()
    {
        ObjectDisposedException.ThrowIf(_given || _handle.IsClosed, this);
    }
}
