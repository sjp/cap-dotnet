using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Interop;

/// <summary>
/// A stream over an open file, for a backend whose handles are not the operating system's.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="FileStream"/> reads and writes its handle through the system, so over a
/// handle from a simulated filesystem it would act on whichever real file has the same
/// number. This stream keeps the position itself and makes every read and write a
/// positioned one through the backend that issued the handle, which is the same shape a
/// <see cref="FileStream"/> has underneath: a position of its own, and reads and writes by
/// offset.
/// </para>
/// <para>
/// It does not buffer. A buffer exists to save system calls, and a backend in memory has
/// none to save, so every read and write reaches the backend as it is made and a flush has
/// nothing to do.
/// </para>
/// <para>
/// Owns the handle, and closes it when disposed. Not safe for use from more than one thread
/// at once, as no stream with a position is.
/// </para>
/// </remarks>
internal sealed class PositionedFileStream : Stream
{
    private readonly IPlatformOps _backend;
    private readonly SafeFileHandle _handle;
    private readonly FileAccess _access;
    private long _position;

    public PositionedFileStream(IPlatformOps backend, SafeFileHandle handle, FileAccess access)
    {
        _backend = backend;
        _handle = handle;
        _access = access;
    }

    /// <inheritdoc/>
    public override bool CanRead => !_handle.IsClosed && (_access & FileAccess.Read) != 0;

    /// <inheritdoc/>
    public override bool CanWrite => !_handle.IsClosed && (_access & FileAccess.Write) != 0;

    /// <inheritdoc/>
    public override bool CanSeek => !_handle.IsClosed;

    /// <inheritdoc/>
    public override long Length
    {
        get
        {
            Demand();
            return _backend.GetFileLength(_handle);
        }
    }

    /// <inheritdoc/>
    public override long Position
    {
        get
        {
            Demand();
            return _position;
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Demand();
            _position = value;
        }
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        Demand();

        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _backend.GetFileLength(_handle) + offset,
            _ => throw new ArgumentException("Not a seek origin.", nameof(origin)),
        };

        if (target < 0)
        {
            throw new IOException("A seek cannot move before the beginning of the file.");
        }

        _position = target;
        return _position;
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        DemandWrite();
        _backend.SetFileLength(_handle, value);
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        DemandRead();
        int read = _backend.ReadFile(_handle, buffer, _position);
        _position += read;
        return read;
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        DemandRead();
        int read = await _backend.ReadFileAsync(_handle, buffer, _position, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        DemandWrite();
        _backend.WriteFile(_handle, buffer, _position);
        _position += buffer.Length;
    }

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        DemandWrite();
        await _backend.WriteFileAsync(_handle, buffer, _position, cancellationToken).ConfigureAwait(false);
        _position += buffer.Length;
    }

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    /// <remarks>Nothing is held back, so there is nothing to flush.</remarks>
    public override void Flush() => Demand();

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        Flush();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _handle.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Demand() => ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

    private void DemandRead()
    {
        Demand();
        if ((_access & FileAccess.Read) == 0)
        {
            throw new NotSupportedException("The stream does not support reading.");
        }
    }

    private void DemandWrite()
    {
        Demand();
        if ((_access & FileAccess.Write) == 0)
        {
            throw new NotSupportedException("The stream does not support writing.");
        }
    }
}
