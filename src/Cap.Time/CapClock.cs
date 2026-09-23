using Cap.Primitives;

namespace Cap.Time;

/// <summary>
/// The one way to get the system clock as a capability.
/// </summary>
/// <remarks>
/// <para>
/// The capability is a <see cref="TimeProvider"/>. This type does not wrap one in anything:
/// it hands out the real system provider to a caller who presents ambient authority, and that
/// is all. A component that needs the time takes a <see cref="TimeProvider"/> as a
/// constructor parameter and uses nothing else, so what it can observe of time is exactly
/// what it was handed. In production that is the system clock, taken once at the composition
/// root. In a test it is a fake the test controls, and the component cannot tell the
/// difference.
/// </para>
/// <para>
/// <strong>Why there is no clock type of our own.</strong> <see cref="TimeProvider"/> already
/// has the shape a clock capability needs: wall time, a monotonic timestamp, timers, and the
/// delays and timeouts in the base library that accept one. The rest of the ecosystem already
/// accepts it, from <c>HttpClient</c> handlers to retry policies and caches. A parallel
/// abstraction would have to be converted back into a <see cref="TimeProvider"/> at every one
/// of those boundaries, and whatever it had kept apart would be merged again at that point.
/// Wrapping it would buy nothing that passing it does not.
/// </para>
/// <para>
/// <strong>What this gives up.</strong> cap-std and the WASI clock interfaces keep the wall
/// clock and the monotonic clock as separate grants, so that code allowed to measure an
/// interval cannot also read the time of day. A <see cref="TimeProvider"/> carries both, and
/// so does anything handed one. Code that must be kept from the time of day should not be
/// given a <see cref="TimeProvider"/> that reports it; a derived provider that overrides
/// <see cref="TimeProvider.GetUtcNow"/> to return a fixed instant is the way to hand out
/// intervals alone.
/// </para>
/// <para>
/// <strong>This is an audit, not a lock.</strong> The system clock is reachable from anywhere
/// in the process through <c>DateTime.UtcNow</c> and its relatives, and nothing here
/// changes that for code outside this library. What the token buys is that a codebase which
/// takes its clock from here, and bans the ambient members, has one searchable place where the
/// clock enters, recorded like every other acquisition of ambient authority.
/// </para>
/// </remarks>
public static class CapClock
{
    /// <summary>
    /// The system clock: wall time, monotonic timestamps and timers, as the operating system
    /// reports them.
    /// </summary>
    /// <param name="authority">
    /// Ambient authority, because the system clock is not derived from anything the caller
    /// holds.
    /// </param>
    /// <returns>
    /// <c>TimeProvider.System</c>. The same instance every time, so nothing is gained by
    /// caching it and nothing is lost by calling this more than once.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="authority"/> is <c>default</c> rather than a token that was acquired.
    /// </exception>
    public static TimeProvider System(AmbientAuthority authority)
    {
        authority.Demand(nameof(authority));

        // The one place in the library that is allowed to name the ambient provider: this is
        // where the clock enters, behind the token.
#pragma warning disable CAP0006
        return TimeProvider.System;
#pragma warning restore CAP0006
    }
}
