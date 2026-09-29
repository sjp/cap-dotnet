using Cap.Primitives.Interop;
using Cap.Tests.Fakes;

namespace Cap.Primitives.Tests;

/// <summary>
/// The lease every backend call takes on a handle before reading its value.
/// </summary>
/// <remarks>
/// Against the simulation, which counts the handles it has given out and not yet had closed,
/// so whether a disposal closed a handle or was held off by a lease can be read directly
/// rather than inferred from a descriptor number.
/// </remarks>
public sealed class HandleLeaseTests
{
    /// <summary>How many handles are disposed while a lease on each is being taken.</summary>
    private const int Rounds = 5_000;

    /// <summary>A lease on a handle already disposed is refused, and letting it go does nothing.</summary>
    [Fact]
    public void A_lease_on_a_disposed_handle_is_invalid()
    {
        FakePlatformOps ops = Simulation();
        SafeDirHandle handle = Open(ops);
        handle.Dispose();

        HandleLease lease = handle.Lease();

        Assert.False(lease.IsValid);
        Assert.Equal(-1, lease.Raw);
        lease.Dispose();
    }

    /// <summary>
    /// A handle disposed while a lease on it is held stays open until the lease is let go.
    /// </summary>
    [Fact]
    public void A_lease_on_an_open_handle_pins_it()
    {
        FakePlatformOps ops = Simulation();
        SafeDirHandle handle = Open(ops);
        int open = ops.OpenHandleCount;

        HandleLease lease = handle.Lease();
        Assert.True(lease.IsValid);

        handle.Dispose();
        Assert.Equal(open, ops.OpenHandleCount);

        lease.Dispose();
        Assert.Equal(open - 1, ops.OpenHandleCount);
    }

    /// <summary>
    /// A disposal that lands while a lease is being taken is reported as a refused lease, never
    /// as an exception.
    /// </summary>
    /// <remarks>
    /// The window is between the lease's own check that the handle is open and the pin, where
    /// the framework throws for a handle that closed in between. Whether a given round lands
    /// in it is up to the scheduler; what is asserted is that no round, wherever it landed,
    /// ended in anything but a refused lease.
    /// </remarks>
    [Fact]
    public void A_handle_disposed_while_a_lease_is_being_taken_never_throws()
    {
        FakePlatformOps ops = Simulation();
        Exception? fault = null;

        for (int round = 0; round < Rounds && fault is null; round++)
        {
            SafeDirHandle handle = Open(ops);
            using Barrier start = new(2);

            Thread leasing = new(() =>
            {
                start.SignalAndWait();
                try
                {
                    while (true)
                    {
                        using HandleLease lease = handle.Lease();
                        if (!lease.IsValid)
                        {
                            return;
                        }
                    }
                }
                catch (Exception e)
                {
                    // Anything thrown would end the thread and take the process with it; it
                    // is carried back to fail the test instead.
                    fault = e;
                }
            });
            leasing.Start();

            start.SignalAndWait(TestContext.Current.CancellationToken);
            Thread.SpinWait(round % 100);
            handle.Dispose();
            leasing.Join();
        }

        Assert.Null(fault);
        Assert.Equal(0, ops.OpenHandleCount);
    }

    private static FakePlatformOps Simulation()
    {
        FakeFileSystem fs = new();
        _ = fs.AddDirectory("sandbox");
        return new FakePlatformOps(fs);
    }

    private static SafeDirHandle Open(FakePlatformOps ops)
    {
        CapResult<SafeDirHandle> result = ops.OpenAmbientDirectory("sandbox", CapAccess.Read);
        Assert.True(result.IsSuccess, result.Error.FailureDescription);
        return result.Value!;
    }
}
