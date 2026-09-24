# The clock as a capability

`Cap.Time` hands out the system clock the way everything else in this library hands out
authority from outside the process: only against an `AmbientAuthority` token, so that the
place it was taken can be found.

```csharp
using Cap.Primitives;
using Cap.Time;

TimeProvider clock = CapClock.System(AmbientAuthority.Acquire());
```

That is the whole API. The capability is a `System.TimeProvider`, and what comes back is
`TimeProvider.System` itself, not a wrapper around it.

## Take it once, pass it down

A component that needs the time takes a `TimeProvider` and reads the time from nothing else:

```csharp
sealed class SessionCache(TimeProvider clock)
{
    public bool IsStale(Session s) => clock.GetUtcNow() >= s.ExpiresAt;

    public Task WaitForSweepAsync(CancellationToken ct) =>
        Task.Delay(TimeSpan.FromMinutes(1), clock, ct);
}
```

The composition root takes the system clock once and hands it over:

```csharp
var cache = new SessionCache(CapClock.System(AmbientAuthority.Acquire()));
```

and a test hands over a clock it controls instead, with nothing in between. That is the main
thing this buys: the fake in `Microsoft.Extensions.TimeProvider.Testing` works through the
capability unchanged.

```csharp
var clock = new FakeTimeProvider(start);
var cache = new SessionCache(clock);

clock.Advance(TimeSpan.FromMinutes(30));
Assert.True(cache.IsStale(session));
```

Each part of the provider is available to the component and to the fake alike: wall time
(`GetUtcNow`, `GetLocalNow`), the monotonic timestamp (`GetTimestamp`, `GetElapsedTime`),
timers (`CreateTimer`), and the base-library members that accept a provider, such as
`Task.Delay(TimeSpan, TimeProvider, CancellationToken)`, `Task.WaitAsync` and
`CancellationTokenSource(TimeSpan, TimeProvider)`. A sleep or a delay goes through the clock
that was passed in, never through `Thread.Sleep` or the `Task.Delay` overloads that do not
take one.

## Why not a clock type of our own

cap-std's clock crate defines its own `SystemClock` and `MonotonicClock`, and a port could
have carried them over. .NET already has the abstraction those types exist to provide,
and it is a good one. It has everything a clock capability needs, and the rest of the
ecosystem already accepts it: `HttpClient` handlers, retry and resilience policies, caches,
rate limiters. A parallel type would have to be turned back into a `TimeProvider` at each of
those boundaries. Whatever it had kept apart would be merged at that point anyway, and every
consumer would have to write the same adapter to test with the standard fake.

So the capability is the type the platform already has, and this library adds only the part
the platform does not: requiring a token at the one point the real clock is taken.

## What this gives up

**The wall clock and the monotonic clock are one grant, not two.** cap-std and the WASI clock
interfaces keep them separate, so that code allowed to measure an interval cannot also read
the time of day. A `TimeProvider` carries both. If a component must not learn the time of
day, do not give it a provider that reports it. Derive one that overrides `GetUtcNow` to
return a fixed instant and leaves the timestamp and timers alone. A host that exposes the
WASI interfaces can make the same split when it implements them over a provider it was given.

**A handle that can write a file can also tell the time.** Every write stamps the file with
the time it was made, and describing the file reads that stamp back. `CapFileTime.Now`, which
asks the filesystem to stamp a file's times with the moment of the change, is the same thing
asked for directly, so it needs no clock and grants none. On Unix the kernel supplies that
moment itself. The Windows call that sets times has no way to ask for it, so the Windows
backend reads the system time at that point and passes it in: the same time the system would
have stamped a write with. A component that must not learn the time of day must not be given
a handle it can write through either.

**It is an audit, not a lock.** The system clock can be reached from anywhere in the process
through `DateTime.UtcNow` and its relatives, and nothing here changes that for code outside
this library. The token makes the place the clock enters searchable, and it is recorded like
every other acquisition (see [ambient-authority.md](ambient-authority.md)). Keeping everything
else from reaching around it takes a build rule, like the one below.

## The rule inside this repository

Every assembly under `src/` is built with the ambient clock banned, by rule `CAP0006` of the
analyzer that ships in the `Cap.Std` package (see [analyzers.md](analyzers.md)):
`DateTime.Now`, `UtcNow` and `Today`, `DateTimeOffset.Now` and `UtcNow`, `TimeProvider.System`,
`Thread.Sleep`, and the `Task.Delay` overloads that take no provider. `CapClock` is the one
place allowed to name `TimeProvider.System`. A consuming project gets the same rule by
turning `CAP0006` on, or by marking its assembly `[assembly: CapabilityStrict]`.

`Stopwatch` is deliberately left alone. It measures an interval from a starting point of the
caller's choosing and cannot say what time it is, which is why the record of ambient-authority
acquisitions uses it for the times it reports.
