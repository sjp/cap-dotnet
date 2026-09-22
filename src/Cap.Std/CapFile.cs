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
    /// Fixed when the file was opened. There is no way to widen it, for the same reason a
    /// directory handle cannot be widened: a component given a handle to read a file has been
    /// given exactly that, and a method that turned it into a writable one would make the
    /// grant meaningless.
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
    /// </remarks>
    public bool IsAsync => _isAsync;

    /// <summary>The file's current length in bytes.</summary>
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
    /// A write that has returned is a write the operating system has accepted, which is not
    /// the same as one the hardware has stored. The difference is invisible until the power
    /// fails, so the choice belongs to the caller: durability costs a great deal on some
    /// devices and nothing on others, and there is no default that is right for both.
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
    /// Reads from a given position in the file.
    /// </summary>
    /// <param name="buffer">Where the bytes go.</param>
    /// <param name="fileOffset">Where in the file to read from.</param>
    /// <returns>
    /// How many bytes were read, which may be fewer than the buffer holds and is zero at the
    /// end of the file. A short read is not an error and not a promise that the rest is
    /// absent; a caller that needs the whole buffer filled asks again from further on.
    /// </returns>
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
    /// Writes all of the buffer, repeating the call underneath if the system accepts only
    /// part of it. A file opened to append is the exception, and the exception is the
    /// platform's rather than this library's: such a handle puts every write at the end of
    /// the file whatever offset it is given, because appending is a property of how the file
    /// was opened and is applied by the operating system.
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
    /// <strong>Cancellation is best-effort and is not a guarantee about the file.</strong>
    /// A read already in the hands of the operating system is usually not recallable, so what
    /// cancelling reliably does is release the caller — the read may still complete, and a
    /// write may still reach the file. It is honoured before the operation starts on every
    /// platform, and during it only where the platform provides a way to say so.
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
    /// Cancellation carries the same caveat as it does for a read, and carries it more
    /// sharply: a write that was abandoned may still have reached the file, in whole or in
    /// part. Cancelling releases the caller and says nothing about what is now on disk.
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
    /// there is no way to ask it not to. The copy refers to the same open file — the same
    /// position, the same access, the same appending behaviour — so it is a second way to
    /// reach one file rather than a second opinion about what the name meant. That matters:
    /// re-opening by name is exactly what a capability exists to avoid, and a name can hold
    /// something else by the time it is asked again.
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
    /// Safe to call more than once, and safe to call after ownership was handed to a stream —
    /// in which case it does nothing, because closing a handle somebody else now owns would
    /// close a file they are still using.
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
