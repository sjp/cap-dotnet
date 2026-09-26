namespace Cap.Std;

/// <summary>
/// A stream over an open <see cref="CapFile"/> whose handle the operating system completes
/// work on by itself.
/// </summary>
/// <remarks>
/// <para>
/// Exists because such a handle can be given to a <see cref="FileStream"/> only once. On
/// Windows the file behind it is attached to the thread pool the first time it is used for an
/// asynchronous read or write, and the attachment belongs to the open file rather than to the
/// handle, so a copy of the handle cannot be attached a second time. A stream built on a copy
/// therefore fails as soon as anything else has used the file that way, and the next stream
/// after it always does. This stream reads and writes through the file's own members instead,
/// so every stream taken from one file shares the one attachment and any number can exist.
/// </para>
/// <para>
/// It has the shape a <see cref="FileStream"/> has underneath: a position of its own, and
/// reads and writes that name their offset. It does not buffer. Writes go through the file's
/// members, so a file that appends has every write from the stream put at the end, as its own
/// writes are.
/// </para>
/// <para>
/// A stream that owns its file disposes it when disposed; one that borrows it leaves it open,
/// and fails as the file does once the file is disposed. Not safe for use from more than one
/// thread at once, as no stream with a position is.
/// </para>
/// </remarks>
internal sealed class CapFileStream : Stream
{
    private readonly CapFile _file;
    private readonly bool _ownsFile;
    private long _position;
    private bool _disposed;

    public CapFileStream(CapFile file, bool ownsFile)
    {
        _file = file;
        _ownsFile = ownsFile;
    }

    /// <inheritdoc/>
    public override bool CanRead => !_disposed && (_file.Access & FileAccess.Read) != 0;

    /// <inheritdoc/>
    public override bool CanWrite => !_disposed && (_file.Access & FileAccess.Write) != 0;

    /// <inheritdoc/>
    public override bool CanSeek => !_disposed;

    /// <inheritdoc/>
    public override long Length
    {
        get
        {
            Demand();
            return _file.Length;
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
            SeekOrigin.End => _file.Length + offset,
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
        DemandWrite();
        _file.SetLength(value);
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
        int read = _file.Read(buffer, _position);
        _position += read;
        return read;
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        DemandRead();
        int read = await _file.ReadAsync(buffer, _position, cancellationToken).ConfigureAwait(false);
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
        _file.Write(buffer, _position);
        _position += buffer.Length;
    }

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        DemandWrite();
        await _file.WriteAsync(buffer, _position, cancellationToken).ConfigureAwait(false);
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
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (_ownsFile)
            {
                _file.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private void Demand() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void DemandRead()
    {
        Demand();
        if ((_file.Access & FileAccess.Read) == 0)
        {
            throw new NotSupportedException("The stream does not support reading.");
        }
    }

    private void DemandWrite()
    {
        Demand();
        if ((_file.Access & FileAccess.Write) == 0)
        {
            throw new NotSupportedException("The stream does not support writing.");
        }
    }
}
