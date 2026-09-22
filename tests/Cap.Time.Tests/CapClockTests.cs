using Cap.Primitives;
using Microsoft.Extensions.Time.Testing;

namespace Cap.Time.Tests;

/// <summary>
/// The system clock handed out behind a token, and the fake that stands in for it.
/// </summary>
/// <remarks>
/// <para>
/// There is very little to the capability itself: it is the system <see cref="TimeProvider"/>,
/// given only to a caller who presents ambient authority. What matters more is the
/// other half of the arrangement. A component written against the capability takes a
/// <see cref="TimeProvider"/> and nothing else, so a test can substitute a clock it controls
/// and the component cannot tell. If that substitution needed an adapter, or lost any part of
/// what the provider can do, the capability would be one more thing a test has to work around
/// rather than the thing that makes it deterministic.
/// </para>
/// <para>
/// <see cref="Lease"/> below is written the way a consumer would write one. Every test drives
/// it through the fake unchanged, over each part of the provider: wall time, the monotonic
/// timestamp, delays and timers.
/// </para>
/// </remarks>
public sealed class CapClockTests
{
    private static readonly DateTimeOffset Start = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A value nobody acquired is not a token, and gets nobody the clock.</summary>
    [Fact]
    public void The_system_clock_requires_an_acquired_token()
    {
        Assert.Throws<ArgumentException>("authority", () => CapClock.System(default));
    }

    /// <summary>
    /// The capability is the system provider itself, not a wrapper around it, so it can be
    /// handed straight to anything in the ecosystem that accepts one.
    /// </summary>
    [Fact]
    public void The_system_clock_is_the_system_provider()
    {
        Assert.Same(TimeProvider.System, CapClock.System(AmbientAuthority.Acquire()));
    }

    /// <summary>The component reads wall time from the clock it was handed.</summary>
    [Fact]
    public void Wall_time_comes_from_the_clock_passed_in()
    {
        var clock = new FakeTimeProvider(Start);
        var lease = new Lease(clock, TimeSpan.FromMinutes(5));

        Assert.Equal(Start.AddMinutes(5), lease.ExpiresAt);
        Assert.False(lease.IsExpired);

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(lease.IsExpired);
    }

    /// <summary>
    /// The monotonic timestamp moves when the fake is advanced, and only then, so an interval
    /// measured in a test is exactly the interval the test chose.
    /// </summary>
    [Fact]
    public void Elapsed_time_comes_from_the_clock_passed_in()
    {
        var clock = new FakeTimeProvider(Start);
        var lease = new Lease(clock, TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.Zero, lease.Held);

        clock.Advance(TimeSpan.FromSeconds(90));

        Assert.Equal(TimeSpan.FromSeconds(90), lease.Held);
    }

    /// <summary>
    /// A delay taken through the clock waits on the fake rather than on real time: it does not
    /// finish until the test moves time past it.
    /// </summary>
    [Fact]
    public async Task A_delay_waits_on_the_clock_passed_in()
    {
        var clock = new FakeTimeProvider(Start);
        var lease = new Lease(clock, TimeSpan.FromMinutes(5));

        Task expiry = lease.WaitForExpiryAsync(TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(expiry.IsCompleted);

        clock.Advance(TimeSpan.FromMinutes(1));
        await expiry.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>A timer created through the clock fires when the fake reaches its due time.</summary>
    [Fact]
    public void A_timer_fires_on_the_clock_passed_in()
    {
        var clock = new FakeTimeProvider(Start);
        var lease = new Lease(clock, TimeSpan.FromMinutes(5));
        int renewals = 0;

        using ITimer timer = lease.RenewEvery(TimeSpan.FromMinutes(1), () => renewals++);

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, renewals);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, renewals);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(3, renewals);
    }

    /// <summary>
    /// The same component runs on the real clock taken through the capability, which is how it
    /// is meant to be composed outside a test.
    /// </summary>
    [Fact]
    public async Task The_same_component_runs_on_the_system_clock()
    {
        TimeProvider clock = CapClock.System(AmbientAuthority.Acquire());
        var lease = new Lease(clock, TimeSpan.FromMilliseconds(20));

        await lease.WaitForExpiryAsync(TestContext.Current.CancellationToken);

        // Only the monotonic reading is asserted: the wall clock is a different source and
        // may be stepped by the system while the test runs.
        Assert.True(lease.Held >= TimeSpan.FromMilliseconds(20));
    }

    /// <summary>
    /// A consumer of the clock capability, written without knowing whether it will be given the
    /// system clock or a fake.
    /// </summary>
    private sealed class Lease(TimeProvider clock, TimeSpan duration)
    {
        private readonly long _taken = clock.GetTimestamp();

        public DateTimeOffset ExpiresAt { get; } = clock.GetUtcNow() + duration;

        public bool IsExpired => clock.GetUtcNow() >= ExpiresAt;

        public TimeSpan Held => clock.GetElapsedTime(_taken);

        public async Task WaitForExpiryAsync(CancellationToken cancellationToken)
        {
            // A timer may be serviced a little before its due time on some platforms, so wait
            // until the interval has actually passed rather than trusting one delay.
            for (TimeSpan remaining = duration - Held; remaining > TimeSpan.Zero; remaining = duration - Held)
            {
                await Task.Delay(remaining, clock, cancellationToken);
            }
        }

        public ITimer RenewEvery(TimeSpan period, Action renew) =>
            clock.CreateTimer(_ => renew(), null, period, period);
    }
}
