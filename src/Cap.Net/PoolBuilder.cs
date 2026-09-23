using System.Net;
using Cap.Primitives;

namespace Cap.Net;

/// <summary>
/// Collects the grants a <see cref="Pool"/> will hold, and fixes them.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the pool itself so that the two halves of the arrangement cannot be
/// confused. Deciding what a component may reach happens once, in code that has the standing
/// to decide it; using that decision happens everywhere else. If one type did both, a
/// component handed a pool could add to it — and an authority its holder can widen is not an
/// authority, it is a suggestion. Worse, the widening would be visible to everybody else
/// holding the same pool, so one component could quietly extend another's reach.
/// </para>
/// <para>
/// Every grant demands an <see cref="AmbientAuthority"/> token, because every grant is a
/// decision made from outside the capability graph rather than derived from something the
/// caller was already given. The token puts each of those decisions in the list a search of
/// the source produces, alongside the places the process opens its first directory.
/// </para>
/// <para>
/// <strong>What a grant over a range does not reach.</strong> The addresses an interface
/// configures for itself — <c>169.254.0.0/16</c> and <c>fe80::/10</c> — are never covered by
/// <see cref="InsertIpNet"/>, however wide the range. On a hosted machine one of them answers
/// configuration questions about the instance, credentials included, and it is reachable from
/// every process on the machine without any routing. A range written to describe a network
/// would otherwise hand that over as a side effect of describing something else, and nothing
/// in the way such a grant is written would show it. Naming such an endpoint outright with
/// <see cref="InsertSocketAddress"/> still works, and now says plainly that it was meant.
/// </para>
/// <para>
/// <strong>Thread safety.</strong> Instances are not safe for concurrent use. A builder
/// belongs to the code that is deciding, which is one piece of start-up code; what gets
/// shared is the pool it produces, and that is immutable.
/// </para>
/// </remarks>
public sealed class PoolBuilder
{
    private readonly List<IPEndPoint> _endpoints = [];
    private readonly List<NetworkGrant> _networks = [];
    private bool _everyEndpoint;

    /// <summary>Grants one endpoint.</summary>
    /// <param name="endpoint">The address and port that may be reached.</param>
    /// <param name="authority">
    /// Proof that deciding what may be reached is intended here. Must come from
    /// <see cref="AmbientAuthority.Acquire"/>; a default value is refused.
    /// </param>
    /// <returns>This builder, so that grants can be written one after another.</returns>
    /// <remarks>
    /// <para>
    /// The only grant that reaches an address an interface configures for itself, which is
    /// the point: reaching one is a thing somebody has to write down rather than a thing that
    /// falls out of a range.
    /// </para>
    /// <para>
    /// Not safe to call at the same time as any other member of the same builder.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="endpoint"/> names port zero, which is a request for a port rather than
    /// a port, or <paramref name="authority"/> was never acquired.
    /// </exception>
    public PoolBuilder InsertSocketAddress(IPEndPoint endpoint, AmbientAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        authority.Demand(nameof(authority));

        if (endpoint.Port == 0)
        {
            throw new ArgumentException(
                "Port zero asks the system to choose a port rather than naming one, so it " +
                "cannot be granted. A socket that asks for it is checked against the port it " +
                "was actually given.",
                nameof(endpoint));
        }

        _endpoints.Add(new IPEndPoint(
            EndpointNormalization.Normalize(endpoint.Address), endpoint.Port));

        return this;
    }

    /// <summary>Grants a range of ports across a range of addresses.</summary>
    /// <param name="network">The addresses that may be reached.</param>
    /// <param name="ports">The ports that may be reached at each of them.</param>
    /// <param name="authority">
    /// Proof that deciding what may be reached is intended here. Must come from
    /// <see cref="AmbientAuthority.Acquire"/>; a default value is refused.
    /// </param>
    /// <returns>This builder, so that grants can be written one after another.</returns>
    /// <remarks>
    /// <para>
    /// A range of addresses given to the newer family that lies wholly inside the prefix
    /// which embeds the older one is read as a grant over the older family, so that it covers
    /// those addresses however they are spelled.
    /// </para>
    /// <para>
    /// Not safe to call at the same time as any other member of the same builder.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="ports"/> is empty, <paramref name="network"/> lies wholly inside the
    /// addresses a range grant does not reach, or <paramref name="authority"/> was never
    /// acquired.
    /// </exception>
    public PoolBuilder InsertIpNet(IPNetwork network, PortRange ports, AmbientAuthority authority)
    {
        authority.Demand(nameof(authority));

        if (ports.IsEmpty)
        {
            throw new ArgumentException(
                "A grant with no ports in it reaches nothing, so it is more likely a mistake " +
                "than an intention. Name the ports the service is reached at.",
                nameof(ports));
        }

        IPNetwork normalized = EndpointNormalization.Normalize(network);

        // Refused rather than accepted and then never matched. A grant that silently covers
        // nothing reads, to whoever writes it, exactly like a grant that works.
        if (EndpointNormalization.IsWhollyLinkLocal(normalized))
        {
            throw new ArgumentException(
                "A range of addresses an interface configures for itself is never reached by " +
                "a grant over a range, so this one would cover nothing. Name the endpoint " +
                $"outright with {nameof(InsertSocketAddress)} if reaching it is intended.",
                nameof(network));
        }

        _networks.Add(new NetworkGrant(normalized, ports));
        return this;
    }

    /// <summary>Grants every endpoint there is.</summary>
    /// <param name="authority">
    /// Proof that deciding what may be reached is intended here. Must come from
    /// <see cref="AmbientAuthority.Acquire"/>; a default value is refused.
    /// </param>
    /// <returns>This builder, so that grants can be written one after another.</returns>
    /// <remarks>
    /// <para>
    /// Named so that it cannot be arrived at by accident, and so that it is findable. There
    /// is no address or port that produces this from any other member — a range covering
    /// everything is still a range, and still does not reach the addresses an interface
    /// configures for itself. This does, and it is the only grant that does so without
    /// naming one.
    /// </para>
    /// <para>
    /// A pool built with it states that the component holding it has no stated network reach.
    /// That is sometimes the honest answer, for a process whose whole job is to talk to
    /// whatever it is told to; it is worth writing down as a decision rather than reaching by
    /// widening a range until everything fits.
    /// </para>
    /// <para>
    /// Not safe to call at the same time as any other member of the same builder.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="authority"/> was never acquired.</exception>
    public PoolBuilder InsertEveryEndpoint(AmbientAuthority authority)
    {
        authority.Demand(nameof(authority));

        _everyEndpoint = true;
        return this;
    }

    /// <summary>Fixes the grants collected so far as a pool.</summary>
    /// <remarks>
    /// <para>
    /// Takes a copy, so the builder can go on being used and nothing that happens to it
    /// afterwards reaches a pool already handed out.
    /// </para>
    /// <para>
    /// Not safe to call while another thread is adding a grant to the same builder. The pool
    /// it returns is immutable and may be shared between threads freely.
    /// </para>
    /// </remarks>
    public Pool Build() => new([.. _endpoints], [.. _networks], _everyEndpoint);
}
