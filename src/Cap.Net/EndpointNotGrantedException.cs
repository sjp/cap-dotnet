using System.Net;

namespace Cap.Net;

/// <summary>
/// A socket operation named an endpoint the <see cref="Pool"/> it was made against confers
/// no authority over.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from every other failure because it means something different. A refused
/// connection is a fact about the network; this is a request that, had it been carried out,
/// would have reached somewhere nobody granted. Nothing left the process — no packet was
/// sent, no name was published — so there is no operating-system error to report and this is
/// not a <see cref="System.Net.Sockets.SocketException"/>, whose entire content is such a
/// code.
/// </para>
/// <para>
/// It is an event worth recording and worth alerting on, and a separate type is what lets an
/// application do that without pattern-matching on message text.
/// </para>
/// <para>
/// Being thrown is not evidence of an attack. The commonest cause is a grant that was
/// written for one environment and carried into another, and the second commonest is an
/// address a caller believed was covered by a range that does not in fact cover it — the
/// addresses an interface configures for itself, which a grant over a range never reaches.
/// What it does mean is that the operation was refused on authority grounds and not for any
/// other reason, which is the distinction that makes a log of these worth reading.
/// </para>
/// </remarks>
public sealed class EndpointNotGrantedException : IOException
{
    /// <summary>Creates the exception with a default message.</summary>
    public EndpointNotGrantedException()
        : base("The endpoint lies outside what the pool grants authority over.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public EndpointNotGrantedException(string? message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    public EndpointNotGrantedException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Builds the exception for a refusal, naming the endpoint and the operation.</summary>
    internal static EndpointNotGrantedException For(EndPoint endpoint, string operation) =>
        new($"The pool grants no authority to {operation} {endpoint}.");
}
