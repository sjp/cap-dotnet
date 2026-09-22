using System.Net.Sockets;
using Cap.Primitives.Interop.Unix;
using Cap.Std;

namespace Cap.Net;

/// <summary>
/// A listening socket in the Unix domain, created through a directory handle.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="CapUnixStream"/>, and the same arrangement: the authority to
/// create a socket at a path is the authority over that path, so it is a <see cref="Dir"/>
/// rather than a <see cref="Pool"/>. Code holding a handle on a directory can publish a
/// socket inside it and nowhere else.
/// </para>
/// <para>
/// A name already taken fails the bind — by anything at all, a symbolic link included, which
/// is what stops a link planted in the directory from redirecting the creation somewhere the
/// handle does not cover. Nothing is replaced: clearing a name left behind by a previous run
/// is a deletion, and a deletion is something the caller asks for through the directory
/// handle rather than something a bind does on its behalf.
/// </para>
/// <para>
/// <strong>Disposal does not remove the name.</strong> Closing the socket leaves the entry in
/// the directory, as it does for every other program that binds one. Removing it is
/// <see cref="Dir.DeleteFile"/>, and it is left to the caller because a removal acts on the
/// name rather than on the object: by the time a process has finished, the name may hold
/// something somebody else put there, and unlinking it as a courtesy would delete that.
/// </para>
/// <para>
/// <strong>Available only where the platform can express it.</strong> See
/// <see cref="IsSupported"/>.
/// </para>
/// </remarks>
public sealed class CapUnixListener : IDisposable
{
    /// <summary>How many pending connections are held when the caller does not say.</summary>
    private const int DefaultBacklog = 128;

    private readonly Socket _socket;

    private CapUnixListener(Socket socket) => _socket = socket;

    /// <inheritdoc cref="CapUnixStream.IsSupported"/>
    public static bool IsSupported => UnixSocketReach.IsSupported;

    /// <summary>Creates a socket at <paramref name="path"/> beneath <paramref name="dir"/> and listens on it.</summary>
    /// <param name="dir">The authority over where the socket is created.</param>
    /// <param name="path">A path beneath it, of one or more components, that nothing holds yet.</param>
    /// <param name="backlog">How many pending connections the system holds before refusing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="PlatformNotSupportedException"><see cref="IsSupported"/> is false.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> names something outside what <paramref name="dir"/> covers.
    /// </exception>
    /// <exception cref="SocketException">The name is taken, or the socket could not be created.</exception>
    public static CapUnixListener Bind(Dir dir, string path, int backlog = DefaultBacklog)
    {
        using UnixSocketName named = UnixSocketReach.ToBind(dir, path);

        Socket socket = CapUnixStream.Create();
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(named.Address));
            socket.Listen(backlog);

            return new CapUnixListener(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Waits for a connection and takes it.</summary>
    /// <exception cref="SocketException">The accept failed.</exception>
    public CapUnixStream Accept() => Adopt(_socket.Accept());

    /// <inheritdoc cref="Accept"/>
    /// <param name="cancellationToken">Abandons the wait.</param>
    public async ValueTask<CapUnixStream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        Socket accepted = await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
        return Adopt(accepted);
    }

    /// <summary>Stops listening, leaving the name in the directory.</summary>
    public void Dispose() => _socket.Dispose();

    private static CapUnixStream Adopt(Socket accepted)
    {
        try
        {
            return new CapUnixStream(accepted);
        }
        catch
        {
            accepted.Dispose();
            throw;
        }
    }
}
