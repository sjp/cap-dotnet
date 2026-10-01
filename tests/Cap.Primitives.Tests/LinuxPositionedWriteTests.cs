using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;

namespace Cap.Primitives.Tests;

/// <summary>
/// The Linux appending write's loop, driven by a write that misbehaves on demand.
/// </summary>
/// <remarks>
/// A real file accepts every byte at once and is never interrupted mid-call, so the two
/// branches that matter — carrying on after a short write, and retrying after an
/// interruption — are never taken against one. Dropping either would lose the tail of a
/// write or fail it for no reason, and neither would show on disk in a test. The loop is
/// handed a write here that does both.
/// </remarks>
public sealed unsafe class LinuxPositionedWriteTests
{
    private const int Descriptor = 42;
    private const long StartOffset = 1000;

    [ThreadStatic]
    private static List<(int Fd, int Count, long Offset)>? t_calls;

    [ThreadStatic]
    private static byte[]? t_file;

    [ThreadStatic]
    private static int t_failingErrno;

    /// <summary>
    /// A write that takes half, then is interrupted, still lands every byte, in order, at the
    /// offsets the bytes belong at.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    public void A_short_write_and_an_interruption_still_write_everything()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The appending write is the Linux backend's.");
            return;
        }

        byte[] payload = [.. Enumerable.Range(1, 100).Select(i => (byte)i)];
        t_calls = [];
        t_file = new byte[payload.Length];

        CapError result;
        fixed (byte* start = payload)
        {
            result = LinuxPlatformOps.WritePositioned(Descriptor, start, payload.Length, StartOffset, &HalfThenInterrupted);
        }

        Assert.True(result.IsSuccess, result.FailureDescription);
        Assert.Equal(payload, t_file);
        Assert.Equal(
            [
                (Descriptor, 100, StartOffset),
                (Descriptor, 50, StartOffset + 50),
                (Descriptor, 50, StartOffset + 50),
            ],
            t_calls);
    }

    /// <summary>
    /// A failure other than an interruption ends the write and is reported as itself.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    public void A_failure_other_than_an_interruption_is_reported()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The appending write is the Linux backend's.");
            return;
        }

        byte[] payload = new byte[10];
        t_calls = [];
        t_failingErrno = PosixErrno.EIO;

        CapError result;
        fixed (byte* start = payload)
        {
            result = LinuxPlatformOps.WritePositioned(Descriptor, start, payload.Length, StartOffset, &Failing);
        }

        Assert.True(result.IsFailure);
        Assert.Equal(CapErrorSource.Errno, result.Source);
        Assert.Equal(PosixErrno.EIO, result.RawCode);
        Assert.Single(t_calls);
    }

    /// <summary>Takes half of the first request, is interrupted on the second, then takes the rest.</summary>
    private static nint HalfThenInterrupted(int fd, byte* buffer, nuint count, long offset)
    {
        t_calls!.Add((fd, (int)count, offset));
        switch (t_calls.Count)
        {
            case 1:
                return Accept(buffer, (int)count / 2, offset);
            case 2:
                Marshal.SetLastPInvokeError(PosixErrno.EINTR);
                return -1;
            default:
                return Accept(buffer, (int)count, offset);
        }
    }

    private static nint Failing(int fd, byte* buffer, nuint count, long offset)
    {
        t_calls!.Add((fd, (int)count, offset));
        Marshal.SetLastPInvokeError(t_failingErrno);
        return -1;
    }

    private static nint Accept(byte* buffer, int count, long offset)
    {
        new ReadOnlySpan<byte>(buffer, count).CopyTo(t_file.AsSpan((int)(offset - StartOffset)));
        return count;
    }
}
