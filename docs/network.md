# Capability sockets

`Cap.Net` is the network half of the library: a `Pool` of permitted endpoints that a
component is handed, and socket types that will not reach anything the pool does not cover.

Read [the section on what it does not promise](#what-a-pool-is-not) before anything else. The
filesystem side of this library is enforced by the operating system; this side is not, and
the difference is the most important thing on this page.

```csharp
using Cap.Net;
using Cap.Primitives;

// Decided once, by code that has the standing to decide it.
Pool pool = new PoolBuilder()
    .InsertIpNet(IPNetwork.Parse("10.4.0.0/16"), PortRange.Only(5432), AmbientAuthority.Acquire())
    .InsertSocketAddress(new IPEndPoint(IPAddress.Parse("93.184.216.34"), 443), AmbientAuthority.Acquire())
    .Build();

// Handed on. Nothing the recipient can call widens it.
using CapTcpStream stream = await CapTcpStream.ConnectAsync(
    pool, new IPEndPoint(IPAddress.Parse("10.4.1.9"), 5432));
```

## What a pool is not

A `Dir` handle is authority the kernel itself enforces: code that was never given one cannot
reach what is beneath it, however it asks. A `Pool` has no such backing. Code that can call
anything can open a socket of its own, and nothing here stops it — there is no equivalent of
"you do not hold the descriptor" for a network address.

What a pool buys is that the network reach of *cooperating* code is stated in one place, in
a form a review can read and an outside enforcer can act on. That is worth having:

- A component's reach becomes a value that is passed in rather than a property of the machine
  it happens to be running on, so it can be read off the call site that built it.
- A host that already controls what the process can do — a WASI runtime, a container's
  network policy, a service mesh — can be configured from the same list, and the two then
  agree by construction rather than by someone remembering to update both.
- A refusal is a distinct exception type, so exceeding a grant is a thing an application can
  alert on without reading message text.

None of that is containment. Do not describe it as containment.

## Grants are made once and then fixed

`PoolBuilder` collects grants; `Build()` fixes them as a `Pool`. The split is not
bookkeeping. If one type did both, a component handed a pool could add to it — and an
authority its holder can widen is not an authority, it is a suggestion. Worse, the widening
would be visible to everybody else holding the same pool, so one component could quietly
extend another's reach.

Every grant demands an `AmbientAuthority` token, because deciding what may be reached is a
decision made from outside the capability graph rather than derived from something the caller
already holds. The token puts each of those decisions into the list a search of the source
produces, alongside the places the process opens its first directory. See
[ambient-authority.md](ambient-authority.md).

| Grant | Reaches |
|---|---|
| `InsertSocketAddress(endpoint, authority)` | That one address and port, whatever kind of address it is. |
| `InsertIpNet(network, ports, authority)` | Those addresses at those ports, **except** the ones an interface configures for itself. |
| `InsertEveryEndpoint(authority)` | Everything. |

`InsertEveryEndpoint` is named so that it cannot be arrived at by accident and so that it is
findable. There is no address or port that produces it from any other member.

## No name is ever resolved

There is no member anywhere in `Cap.Net` that turns a host name into an address. A pool holds
addresses, every operation takes an address, and the address that was checked is the address
that is connected to.

This is not squeamishness about DNS. Resolving a name and then connecting are two separate
lookups of the same name, and whoever answers them can answer differently — the first with an
address the pool grants, the second with one it does not. An allowlist checked that way
refuses nothing, and it does so while looking exactly like an allowlist that works. There is
no careful way to write it; the only fix is to check the address that is actually reached,
which means never resolving a name in between.

A caller who starts from a host name resolves it in their own code, where the step is visible
for what it is: a question asked of a server whose answer somebody else controls. `System.Net.Dns`
is a banned symbol in every assembly of this library, and the analyzer will flag it in
consuming code that has opted in — not to forbid it, but to make it appear in the same list
as every other place authority enters.

## One host, several spellings

An address can be written more than one way, and the spellings are not string variants — they
are different address families holding the same thirty-two bits. A check against `127.0.0.1`
handed `::ffff:127.0.0.1` sees another family, answers that it is not in the list, and the
connection that follows reaches the loopback interface exactly as the refused one would have.

Everything a grant holds and everything a grant is tested against is reduced to one form
first. Two spellings fold into the address they name:

- the mapped form, `::ffff:a.b.c.d`, which is what a dual-stack socket reports for a peer that
  arrived over IPv4;
- the compatible form, `::a.b.c.d`, deprecated for two decades and still accepted by every
  address parser, which is reason enough for it to turn up in a request somebody hoped would
  not be checked.

A range given in the newer family that lies wholly inside the embedding prefix — `::ffff:10.0.0.0/104`
— is read as the older family's range it describes, so it covers those addresses however they
are spelled.

Two things are deliberately *not* folded in. `::` and `::1` sit inside the embedding prefix
without being embeddings, and stay themselves. Tunnelling addresses embed an IPv4 address
without being one: they reach that host through a relay, are a different endpoint at the layer
this library checks at, and folding them in would make a grant cover a path nobody granted.

Scope identifiers are dropped before comparison. They name an interface rather than a host,
they are absent from most written forms, and a grant that matched only when the caller had
guessed the same interface index would be a grant that usually failed.

## A range grant does not reach link-local addresses

`169.254.0.0/16` and `fe80::/10` are never covered by `InsertIpNet`, however wide the range —
`0.0.0.0/0` does not reach them.

On a hosted machine, `169.254.169.254` is where the instance answers configuration questions
about itself, credentials included, and it is reachable from every process on the machine
without any routing at all. A grant written to describe a network would otherwise hand that
over as a side effect of describing something else, and nothing in the way such a grant was
written would show it.

Naming the endpoint outright with `InsertSocketAddress` still reaches it, and now says plainly
that it was meant — which is the whole point: it becomes one line a review can find. A range
that could *only* ever cover such addresses is refused at the point it is written, rather than
accepted and then never matched, because a grant that silently covers nothing reads exactly
like a grant that works.

`InsertEveryEndpoint` reaches them, as it reaches everything.

Note what this rule does **not** cover: loopback is not special-cased. A grant of `0.0.0.0/0`
includes `127.0.0.1` and every service listening on it.

## The endpoint checked is the endpoint reached

Every operation checks what actually happened, not only what was asked for.

A listener that binds port `0` is asking the system to choose, so there is nothing to check
until it has. The bind is made, the port the system settled on is checked against the pool,
and a result the pool does not grant is closed again — between the bind and the `listen`, so
the socket has a name by then but nothing can have connected to it.

## Listening: the pool governs the name, not the callers

Binding publishes an address and a port that anything able to route to them can connect to,
and which of those a component may publish is exactly the kind of decision a pool exists to
record. Who then connects is not.

A listener that refused peers it had not been told about in advance could not serve anything
public, and the address a connection arrives from is not evidence of anything anyway — it is
what the network says, not what the peer proved. For a datagram it is weaker still: nothing
acknowledged it, so the sender address is a field somebody wrote. Filtering on it would give
an allowlist the appearance of a security control without the substance of one.

So the peer is reported — `CapTcpStream.RemoteEndPoint`, the result of a receive — and left to
the caller, who can filter on it if they want to and should authenticate if they need it to
mean something. An accepted connection carries the listener's pool for whatever it does next,
so a component handed one has the reach the listening component had, and no more; accepting is
not a way to acquire authority nobody granted.

A UDP socket that only sends can be opened with `CapUdpSocket.Open`, which claims no endpoint
and so needs none granted. `CapUdpSocket.Bind` is for a socket peers are told how to reach.
Every datagram's destination is checked either way.

## Unix domain sockets are a `Dir` capability

A socket in the Unix domain is named by a filesystem path, so the authority to reach one is
authority over that path — a `Dir`, not a `Pool`. A second allowlist for it would be a second
answer to a question that already has one.

```csharp
using Dir sockets = Dir.Open("/run/app", AmbientAuthority.Acquire());

using CapUnixListener listener = CapUnixListener.Bind(sockets, "api.sock");
using CapUnixStream client = CapUnixStream.Connect(sockets, "api.sock");
```

The attacks are the filesystem's attacks, and they are refused for the filesystem's reasons: a
path that climbs out, a path that was absolute all along, and a middle component that turns out
to be a link pointing elsewhere. The last component is not followed either — a name holding a
symbolic link is refused rather than resolved, even when the link points somewhere the handle
does cover.

That last rule deserves its reason stated. The calls that use these addresses take a path and
resolve it themselves, with the whole process's authority, following links as they go. Once
that call is doing the resolving, where the link happens to point is not something this library
decided. The refusal is about who resolves, not about where one particular link went.

### Why it is Linux-only

Reaching a socket through a handle needs a way to name it relative to an open directory, and
there is no `connectat` or `bindat` to do it with. What makes it possible on Linux is the
descriptor directory: an address written as `/proc/self/fd/N` resolves to the object that
descriptor refers to, rather than to whatever the name it was opened under holds now — so the
only lookup the kernel performs is one this library has already decided is allowed.

- **Connecting** opens the name for position alone, without following it, and names that
  descriptor. The socket call resolves nothing. Because an open of that form returns a
  descriptor *on* a symbolic link rather than refusing one, what the descriptor landed on is
  read back from the descriptor itself and anything that is not a socket is refused.
- **Binding** cannot open what does not exist yet, so it names the descriptor of the directory
  and leaves the kernel to create the single component beneath it. That lookup creates rather
  than follows: a name already taken, by a link or by anything else, fails the bind instead of
  redirecting it.

macOS has no equivalent, and the Windows implementation takes a path with no handle-relative
form at all. On those platforms `CapUnixStream.IsSupported` is false and the operations throw
`PlatformNotSupportedException`. The alternative would be to resolve the path and hand the
result to a socket call — which is the technique a directory handle exists to replace, and
would make the guarantee the type states untrue rather than merely weaker. Ask
`IsSupported` before offering the feature; the answer does not change while a process runs.

### Closing a listener leaves the name

As it does for every other program that binds one. Removing it is `Dir.DeleteFile`, and it is
left to the caller because a removal acts on the name rather than on the object: by the time a
process has finished, the name may hold something somebody else put there, and unlinking it as
a courtesy would delete that.
