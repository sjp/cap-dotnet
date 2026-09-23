using System.Net;

namespace Cap.Net;

/// <summary>
/// A set of endpoints, and the authority to reach them.
/// </summary>
/// <remarks>
/// <para>
/// This is the capability. Code holding one can reach the endpoints it names and no others,
/// whatever address it is handed; code that does not hold one cannot open a socket through
/// this library at all. A pool is built where the process decides what a component may
/// reach, and handed to that component; nothing the component can call widens it, because
/// there is nothing on this type that changes it. Grants are collected by
/// <see cref="PoolBuilder"/> and fixed when it produces a pool, so a pool that has been
/// passed on cannot be altered underneath its holder.
/// </para>
/// <para>
/// <strong>The guarantee here is weaker than the filesystem's, and the difference is not a
/// detail.</strong> A directory handle is authority the operating system itself enforces:
/// code that has not been given one cannot reach what is beneath it however it asks. A pool
/// is not enforced by anything outside this process. Code that can call anything can open a
/// socket of its own, and nothing here stops it. What a pool buys is that the network reach
/// of cooperating code is stated in one place, in a form that a review can read and a host
/// can enforce from outside — which is worth having, and is not the same as containment.
/// </para>
/// <para>
/// <strong>No name is ever resolved.</strong> A pool holds addresses, and every operation
/// takes an address; there is no member anywhere in this library that turns a host name into
/// one. Resolving a name is itself an authority — it tells a server somewhere which name was
/// asked about, and the answer comes back from that server rather than from the caller — and
/// worse, an allowlist checked against a name and then connected by a second resolution can
/// be made to check one address and reach another. A caller who starts from a name resolves
/// it in their own code, where the step is visible, and hands over what came back.
/// </para>
/// <para>
/// <strong>Thread safety.</strong> Instances are immutable and safe for concurrent use by any
/// number of threads.
/// </para>
/// </remarks>
public sealed class Pool
{
    private readonly IPEndPoint[] _endpoints;
    private readonly NetworkGrant[] _networks;
    private readonly bool _everyEndpoint;

    internal Pool(IPEndPoint[] endpoints, NetworkGrant[] networks, bool everyEndpoint)
    {
        _endpoints = endpoints;
        _networks = networks;
        _everyEndpoint = everyEndpoint;
    }

    /// <summary>A pool that grants nothing.</summary>
    /// <remarks>
    /// <para>
    /// Useful as the authority handed to a component that is not supposed to reach the
    /// network at all: it is a value that can be passed and logged, where a null reference
    /// would be a missing argument and would eventually be filled in with something else.
    /// </para>
    /// <para>
    /// The same instance every time, and safe to share between threads like any other pool.
    /// </para>
    /// </remarks>
    public static Pool Empty { get; } = new([], [], everyEndpoint: false);

    /// <summary>Whether this pool grants every endpoint there is.</summary>
    /// <remarks>
    /// <para>
    /// True only of a pool built with <see cref="PoolBuilder.InsertEveryEndpoint"/>. Worth
    /// asking about in a start-up log: it is the one shape of pool that says nothing about
    /// what a component may reach.
    /// </para>
    /// <para>Safe to read from any thread; the answer never changes.</para>
    /// </remarks>
    public bool GrantsEveryEndpoint => _everyEndpoint;

    /// <summary>Whether this pool grants <paramref name="endpoint"/>.</summary>
    /// <remarks>
    /// Safe to call from any number of threads at once: a pool never changes once built, so
    /// the same endpoint always gets the same answer.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is null.</exception>
    public bool Allows(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return Allows(endpoint.Address, endpoint.Port);
    }

    /// <summary>Whether this pool grants <paramref name="port"/> at <paramref name="address"/>.</summary>
    /// <remarks>
    /// <para>
    /// The address is reduced to one form before it is compared, so the several spellings of
    /// one host are answered identically. See the discussion on <see cref="PoolBuilder"/> of
    /// what a grant over a range does and does not reach.
    /// </para>
    /// <para>
    /// Safe to call from any number of threads at once: a pool never changes once built, so
    /// the same endpoint always gets the same answer.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is null.</exception>
    public bool Allows(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (_everyEndpoint)
        {
            return true;
        }

        IPAddress normalized = EndpointNormalization.Normalize(address);

        foreach (IPEndPoint granted in _endpoints)
        {
            if (granted.Port == port && EndpointNormalization.SameAddress(granted.Address, normalized))
            {
                return true;
            }
        }

        // A grant over a range stops here. The addresses an interface configures for itself
        // are reachable only by being named outright, so that a range written to describe a
        // network cannot also hand over the local configuration service that answers on one
        // of them.
        if (EndpointNormalization.IsLinkLocal(normalized))
        {
            return false;
        }

        foreach (NetworkGrant grant in _networks)
        {
            if (grant.Ports.Contains(port) && grant.Network.Contains(normalized))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Refuses <paramref name="endpoint"/> unless this pool grants it.</summary>
    /// <param name="endpoint">The endpoint the operation would act on.</param>
    /// <param name="operation">
    /// What the operation would do with it, as a verb phrase for the refusal to quote.
    /// </param>
    internal void Demand(IPEndPoint endpoint, string operation)
    {
        if (!Allows(endpoint))
        {
            throw EndpointNotGrantedException.For(endpoint, operation);
        }
    }
}

/// <summary>A range of addresses and the ports granted on them.</summary>
internal readonly struct NetworkGrant
{
    public NetworkGrant(IPNetwork network, PortRange ports)
    {
        Network = network;
        Ports = ports;
    }

    /// <summary>The addresses, already reduced to the form they are compared in.</summary>
    public IPNetwork Network { get; }

    /// <summary>The ports granted at each of them.</summary>
    public PortRange Ports { get; }
}
