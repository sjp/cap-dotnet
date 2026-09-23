using System.Net.Sockets;
using Cap.Primitives.Interop.Unix;
using Cap.Std;

namespace Cap.Net;

/// <summary>
/// A connection to a socket in the Unix domain, reached through a directory handle.
/// </summary>
/// <remarks>
/// <para>
/// A socket in this domain is named by a path, so the authority to reach one is the authority
/// over that path: a <see cref="Dir"/>, and not a <see cref="Pool"/>. Code holding a handle on
/// a directory of sockets can connect to the ones inside it and to nothing else, whatever
/// string it is handed — a path that climbs out, a path that was absolute all along, or a path
/// whose middle component turns out to be a link pointing elsewhere are refused here for the
/// same reasons they are refused when a file is opened.
/// </para>
/// <para>
/// The last component is not followed either. A name that holds a symbolic link is refused
/// rather than resolved, because resolving it would hand the target to a socket call, which
/// looks it up with the whole process's authority — so a link planted inside the directory
/// would otherwise reach a socket anywhere on the system.
/// </para>
/// <para>
/// <strong>Available only where the platform can express it.</strong> See
/// <see cref="IsSupported"/>.
/// </para>
/// </remarks>
public sealed class CapUnixStream : CapSocketStream
{
    internal CapUnixStream(Socket socket)
        : base(socket)
    {
    }

    /// <summary>
    /// Whether a socket in the Unix domain can be reached through a directory handle here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// False wherever the platform offers no way to name a socket relative to an open
    /// directory. The calls that use such an address take a path and resolve it themselves,
    /// so without such a way the only implementation available would be to build a path and
    /// hand it over — which is the technique a directory handle exists to replace, and would
    /// make the guarantee this type states untrue rather than merely weaker.
    /// </para>
    /// <para>
    /// Worth asking before offering the feature, rather than discovering it from the
    /// exception: the answer is a property of the host and does not change while a process
    /// runs.
    /// </para>
    /// <para>Safe to read from any thread.</para>
    /// </remarks>
    public static bool IsSupported => UnixSocketReach.IsSupported;

    /// <summary>Connects to the socket <paramref name="path"/> names beneath <paramref name="dir"/>.</summary>
    /// <param name="dir">The authority over where the socket lives.</param>
    /// <param name="path">A path beneath it, of one or more components.</param>
    /// <remarks>
    /// <para>
    /// <strong>Symbolic links.</strong> Every component ahead of the last is resolved beneath
    /// <paramref name="dir"/> under that handle's <see cref="Dir.SymlinkPolicy"/>, exactly as
    /// it would be for an open: a link met on the way is followed only when the policy allows
    /// it and its resolution stays beneath the handle, and refused otherwise. The last
    /// component is never followed, whatever the policy. A name that holds a symbolic link is
    /// refused with <see cref="CapIOException"/>, even when the link points at a socket
    /// beneath the same handle.
    /// </para>
    /// <para>
    /// Safe to call from any thread, and from several at once through the same
    /// <paramref name="dir"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="dir"/> or <paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="dir"/> has been disposed.</exception>
    /// <exception cref="PlatformNotSupportedException"><see cref="IsSupported"/> is false.</exception>
    /// <exception cref="SandboxEscapeException">
    /// <paramref name="path"/> names something outside what <paramref name="dir"/> covers.
    /// </exception>
    /// <exception cref="CapIOException"><paramref name="path"/> does not name a socket.</exception>
    /// <exception cref="SocketException">Nothing was listening, or the connection failed.</exception>
    public static CapUnixStream Connect(Dir dir, string path)
    {
        using UnixSocketName named = UnixSocketReach.ToConnect(dir, path);

        Socket socket = Create();
        try
        {
            // The name was reached through the directory handle, not looked up by path.
#pragma warning disable CAP0002
            socket.Connect(new UnixDomainSocketEndPoint(named.Address));
#pragma warning restore CAP0002
            return new CapUnixStream(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <inheritdoc cref="Connect"/>
    /// <param name="dir">The authority over where the socket lives.</param>
    /// <param name="path">A path beneath it, of one or more components.</param>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    public static async ValueTask<CapUnixStream> ConnectAsync(
        Dir dir, string path, CancellationToken cancellationToken = default)
    {
        using UnixSocketName named = UnixSocketReach.ToConnect(dir, path);

        Socket socket = Create();
        try
        {
            // The name was reached through the directory handle, not looked up by path.
#pragma warning disable CAP0002
            await socket.ConnectAsync(
                    new UnixDomainSocketEndPoint(named.Address), cancellationToken)
                .ConfigureAwait(false);
#pragma warning restore CAP0002

            return new CapUnixStream(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static Socket Create() =>
        new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
}
