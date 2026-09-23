using System.Net.Sockets;

namespace Cap.Net;

/// <summary>
/// A byte stream over a connected socket that a capability granted.
/// </summary>
/// <remarks>
/// <para>
/// Everything about reading and writing is here; what differs between a connection in one
/// domain and a connection in another is which capability was consulted to make it, and that
/// lives in the derived types. A caller that only wants to move bytes can hold this and not
/// care which.
/// </para>
/// <para>
/// A <see cref="Stream"/> rather than a wrapper with its own vocabulary, because everything
/// that already reads and writes streams — a text reader, a protocol implementation, a copy
/// to a file — then works over one of these without translation.
/// </para>
/// <para>
/// <strong>The connection carries no authority of its own.</strong> Disposing a stream closes
/// that connection and nothing else; it takes nothing away from the pool or the directory
/// handle it came from, and it cannot be used to make a second connection.
/// </para>
/// <para>
/// <strong>Thread safety.</strong> One reader and one writer may use an instance at the same
/// time, which is what a request-and-response protocol needs. Two concurrent readers, or two
/// concurrent writers, will interleave.
/// </para>
/// </remarks>
public abstract class CapSocketStream : Stream
{
    private readonly Socket _socket;

    private protected CapSocketStream(Socket socket) => _socket = socket;

    /// <summary>Always true: a connected socket can be read until the peer stops writing.</summary>
    /// <remarks>Safe to read from any thread.</remarks>
    public override bool CanRead => true;

    /// <summary>Always true.</summary>
    /// <remarks>Safe to read from any thread.</remarks>
    public override bool CanWrite => true;

    /// <summary>Always false: a connection has no position to move about in.</summary>
    /// <remarks>Safe to read from any thread.</remarks>
    public override bool CanSeek => false;

    /// <summary>Not meaningful for a connection.</summary>
    /// <remarks>Throws from any thread, the same way every time.</remarks>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override long Length => throw new NotSupportedException(
        "A connection has no length. The bytes that have arrived are the bytes that have arrived.");

    /// <summary>Not meaningful for a connection.</summary>
    /// <remarks>Throws from any thread, the same way every time.</remarks>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override long Position
    {
        get => throw new NotSupportedException("A connection has no position.");
        set => throw new NotSupportedException("A connection has no position.");
    }

    /// <summary>The socket underneath, for the derived types to operate.</summary>
    private protected Socket Socket => _socket;

    /// <summary>Does nothing: nothing is buffered on this side of the socket.</summary>
    /// <remarks>Safe to call from any thread.</remarks>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    /// <remarks>Completes at once, since nothing is buffered. Safe to call from any thread.</remarks>
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled(cancellationToken)
            : Task.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>
    /// May run at the same time as one write on another thread. Two reads at once are not
    /// coordinated: each takes whatever bytes arrive first, so one message can end up split
    /// between them.
    /// </remarks>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// May run at the same time as one write on another thread. Two reads at once are not
    /// coordinated: each takes whatever bytes arrive first, so one message can end up split
    /// between them.
    /// </remarks>
    public override int Read(Span<byte> buffer) => _socket.Receive(buffer, SocketFlags.None);

    /// <inheritdoc/>
    /// <remarks>
    /// May run at the same time as one write on another thread. Two reads at once are not
    /// coordinated: each takes whatever bytes arrive first, so one message can end up split
    /// between them.
    /// </remarks>
    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// May run at the same time as one write on another thread. Two reads at once are not
    /// coordinated: each takes whatever bytes arrive first, so one message can end up split
    /// between them.
    /// </remarks>
    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// May run at the same time as one read on another thread. Two writes at once are not
    /// coordinated: a write that takes more than one send can have the other's bytes land
    /// between its pieces.
    /// </remarks>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Loops until everything is gone, because a send is entitled to take less than it was
    /// offered when the outgoing buffer is full, and a stream write that returned early
    /// having sent part of what it was given would drop the rest silently.
    /// </para>
    /// <para>
    /// May run at the same time as one read on another thread. Two writes at once are not
    /// coordinated: because of the loop, the other's bytes can land between this one's
    /// pieces.
    /// </para>
    /// </remarks>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            int sent = _socket.Send(buffer, SocketFlags.None);
            buffer = buffer[sent..];
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// May run at the same time as one read on another thread. Two writes at once are not
    /// coordinated: a write that takes more than one send can have the other's bytes land
    /// between its pieces.
    /// </remarks>
    public override Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Loops for the reason the synchronous form does, and so shares its thread safety: one
    /// read may run alongside it, but a second write may interleave with it.
    /// </remarks>
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!buffer.IsEmpty)
        {
            int sent = await _socket.SendAsync(buffer, SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
            buffer = buffer[sent..];
        }
    }

    /// <summary>Not meaningful for a connection.</summary>
    /// <remarks>Throws from any thread, the same way every time.</remarks>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("A connection cannot be seeked.");

    /// <summary>Not meaningful for a connection.</summary>
    /// <remarks>Throws from any thread, the same way every time.</remarks>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override void SetLength(long value) =>
        throw new NotSupportedException("A connection has no length to set.");

    /// <summary>
    /// Stops this side of the connection sending, reading or both, while leaving the socket
    /// open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// How a protocol says "I have finished speaking" without closing: the peer sees the end
    /// of the stream and can still reply. Closing instead would discard whatever it was in
    /// the middle of sending.
    /// </para>
    /// <para>
    /// Safe to call from any thread, including while another thread is reading or writing.
    /// </para>
    /// </remarks>
    public void Shutdown(SocketShutdown how) => _socket.Shutdown(how);

    /// <inheritdoc/>
    /// <remarks>
    /// Closes the connection. Safe to call from any thread; a read or write in progress on
    /// another thread is abandoned and fails rather than completing.
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _socket.Dispose();
        }

        base.Dispose(disposing);
    }
}
