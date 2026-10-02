using Cap.Std;

namespace Cap.IO.Abstractions;

/// <summary>
/// The stream beneath one opened with <see cref="FileMode.Append"/>, which writes through the
/// file's own members so that every write lands at the end of the file.
/// </summary>
/// <remarks>
/// <para>
/// Exists because no stream <see cref="CapFile.AsStream"/> gives appends on every platform.
/// On macOS a stream writes at its own position whether or not the file appends, and on
/// Windows it is given a handle that can only append, which the system does not let change
/// the length. <see cref="CapFile.Write"/> puts each write at the end everywhere, and
/// <see cref="CapFile.SetLength"/> works on any file open for writing.
/// </para>
/// <para>
/// It keeps a position of its own, moved past each write as a <see cref="FileStream"/>'s is,
/// and does not buffer. The file is borrowed: whoever made this stream disposes it.
/// </para>
/// </remarks>
internal sealed class AppendingStream(CapFile file) : Stream
{
    private long _position;
    private bool _disposed;

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanWrite => !_disposed;

    /// <inheritdoc/>
    public override bool CanSeek => !_disposed;

    /// <inheritdoc/>
    public override long Length
    {
        get
        {
            Demand();
            return file.Length;
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
            SeekOrigin.End => file.Length + offset,
            _ => throw new ArgumentException("Invalid seek origin.", nameof(origin)),
        };

        if (target < 0)
        {
            throw new IOException("An attempt was made to move the position before the beginning of the stream.");
        }

        _position = target;
        return _position;
    }

    /// <inheritdoc/>
    /// <remarks>A position past the new end moves back to it, as a <see cref="FileStream"/>'s does.</remarks>
    public override void SetLength(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Demand();
        file.SetLength(value);
        _position = Math.Min(_position, value);
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Stream does not support reading.");

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Demand();
        file.Write(buffer, _position);
        _position += buffer.Length;
    }

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Demand();
        await file.WriteAsync(buffer, _position, cancellationToken).ConfigureAwait(false);
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
        _disposed = true;
        base.Dispose(disposing);
    }

    private void Demand() => ObjectDisposedException.ThrowIf(_disposed, this);
}
