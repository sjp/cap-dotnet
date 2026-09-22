using System.Net;
using System.Net.Sockets;

namespace Cap.Net;

/// <summary>
/// Reads back the endpoints a socket ended up with.
/// </summary>
/// <remarks>
/// <para>
/// The framework answers these questions with a base type that covers every address family,
/// and every check in this library is written about addresses and ports. One place does the
/// narrowing, so that a socket which somehow answers with something else is a loud failure
/// here rather than a silently skipped check at each of the call sites.
/// </para>
/// <para>
/// This is where "the endpoint actually reached" comes from, which is the value the grant is
/// tested against after an operation rather than before it. Asking the socket is the only way
/// to learn the port the system assigned to a request that did not name one.
/// </para>
/// </remarks>
internal static class SocketEndpoints
{
    /// <summary>The endpoint at this end of <paramref name="socket"/>.</summary>
    public static IPEndPoint Local(Socket socket) => Require(socket.LocalEndPoint, "local");

    /// <summary>The endpoint at the other end of <paramref name="socket"/>.</summary>
    public static IPEndPoint Remote(Socket socket) => Require(socket.RemoteEndPoint, "remote");

    private static IPEndPoint Require(EndPoint? endpoint, string which) =>
        endpoint as IPEndPoint ??
        throw new InvalidOperationException(
            $"The socket reported a {which} endpoint that is not an address and a port, so " +
            "there is nothing for the pool to be checked against. This should be " +
            "unreachable; a grant is not assumed in its absence.");
}
