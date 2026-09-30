using System.Runtime.Versioning;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Unix;

namespace Cap.Primitives.Tests;

/// <summary>
/// Resolving a path through a directory somebody else is renaming.
/// </summary>
/// <remarks>
/// <para>
/// Confined resolution has an outcome that is neither success nor failure. When the tree
/// moves while the kernel is walking it, the kernel abandons the walk and says the request
/// should be made again — nothing escaped, nothing is broken, the answer simply is not
/// trustworthy yet. Left to reach the caller, it would appear as an error nobody can act on
/// and would make ordinary churn inside a directory look like a fault.
/// </para>
/// <para>
/// So it is retried, and the retry is bounded, because whatever can rename inside the
/// subtree can rename in a loop for as long as it likes. Those two decisions pull in
/// opposite directions and both are checked here: that the retry is taken, and that it stops
/// and reports rather than hanging the caller.
/// </para>
/// </remarks>
[Collection(PlatformOpsTestGroup.Name)]
public sealed class ConfinedOpenRaceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cap-race-").FullName;
    private readonly ITestOutputHelper _output;
    private volatile bool _stopRenaming;

    public ConfinedOpenRaceTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A lost race is retried, up to the bound, and nothing else is.</summary>
    [Fact]
    public void Only_a_lost_race_is_retried_and_only_so_many_times()
    {
        Assert.True(ConfinedRetryPolicy.ShouldRetry(LinuxErrno.EAGAIN, 0));
        Assert.True(ConfinedRetryPolicy.ShouldRetry(LinuxErrno.EAGAIN, ConfinedRetryPolicy.RetryLimit - 1));

        // The bound has to actually bind. An attempt count that could reach the limit and
        // keep going would be a loop an attacker chooses the length of.
        Assert.False(ConfinedRetryPolicy.ShouldRetry(LinuxErrno.EAGAIN, ConfinedRetryPolicy.RetryLimit));

        // Every other failure is an answer, not a request to ask again. Retrying one would
        // turn a refusal to escape into the same refusal sixteen times over.
        Assert.False(ConfinedRetryPolicy.ShouldRetry(PosixErrno.EXDEV, 0));
        Assert.False(ConfinedRetryPolicy.ShouldRetry(PosixErrno.ENOENT, 0));
        Assert.False(ConfinedRetryPolicy.ShouldRetry(PosixErrno.EACCES, 0));
    }

    /// <summary>
    /// A lost race the kernel reports is retried, and the open succeeds once the race is won.
    /// </summary>
    /// <remarks>
    /// The race itself cannot be scheduled, so the backend is told to fail the first attempts
    /// with the code the kernel gives for one. That tests the path from the code to the retry
    /// and the counter, which the provoked race below reaches only when the timing allows.
    /// </remarks>
    [Fact]
    [SupportedOSPlatform("linux")]
    public void A_lost_race_reported_by_the_kernel_is_retried_until_it_is_won()
    {
        const int lost = 3;
        LinuxPlatformOps ops = ConfinedOps();
        using SafeDirHandle root = OpenRootWithInner(ops);
        ops.ConfinedOpenFault = attempt => attempt < lost ? LinuxErrno.EAGAIN : 0;
        long attemptsBefore = ops.ConfinedOpenAttempts;

        CapResult<SafeDirHandle> result = ops.OpenConfinedDirectory(
            root, "inner", CapAccess.Read, ConfinedResolveOptions.None);

        Assert.True(result.IsSuccess, result.Error.FailureDescription);
        result.Value.Dispose();
        Assert.Equal(lost, ops.ConfinedOpenRaceRetries);
        Assert.Equal(lost + 1, ops.ConfinedOpenAttempts - attemptsBefore);
    }

    /// <summary>
    /// A race that keeps being lost is given up on at the bound and reported as a race.
    /// </summary>
    /// <remarks>
    /// This is the half of the policy that stands between a rename loop and a hung caller.
    /// Every attempt is failed, so only the bound can end the loop, and the caller must be
    /// told the resolution kept losing rather than something it would act on differently.
    /// </remarks>
    [Fact]
    [SupportedOSPlatform("linux")]
    public void A_race_that_keeps_being_lost_is_given_up_at_the_limit()
    {
        LinuxPlatformOps ops = ConfinedOps();
        using SafeDirHandle root = OpenRootWithInner(ops);
        ops.ConfinedOpenFault = _ => LinuxErrno.EAGAIN;
        long attemptsBefore = ops.ConfinedOpenAttempts;

        CapResult<SafeDirHandle> result = ops.OpenConfinedDirectory(
            root, "inner", CapAccess.Read, ConfinedResolveOptions.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(CapErrorCategory.Raced, result.Error.Category);
        Assert.Equal(ConfinedRetryPolicy.RetryLimit, ops.ConfinedOpenRaceRetries);
        Assert.Equal(ConfinedRetryPolicy.RetryLimit + 1, ops.ConfinedOpenAttempts - attemptsBefore);
    }

    /// <summary>Any other failure the kernel reports is the answer, and is not retried.</summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    public void A_failure_other_than_a_lost_race_is_not_retried()
    {
        LinuxPlatformOps ops = ConfinedOps();
        using SafeDirHandle root = OpenRootWithInner(ops);

        // The directory is there, so a missing name can only have come from the fault.
        ops.ConfinedOpenFault = attempt => attempt == 0 ? PosixErrno.ENOENT : 0;
        long attemptsBefore = ops.ConfinedOpenAttempts;

        CapResult<SafeDirHandle> result = ops.OpenConfinedDirectory(
            root, "inner", CapAccess.Read, ConfinedResolveOptions.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(CapErrorCategory.NotFound, result.Error.Category);
        Assert.Equal(0, ops.ConfinedOpenRaceRetries);
        Assert.Equal(1, ops.ConfinedOpenAttempts - attemptsBefore);
    }

    /// <summary>
    /// Under a directory being renamed back and forth, resolution either succeeds or reports
    /// that the name was not there — never that it lost a race.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The race is the point and it cannot be scheduled, so this provokes it rather than
    /// arranging it: one thread renames a directory in the middle of the path while another
    /// resolves through it.
    /// </para>
    /// <para>
    /// The path climbs back through a parent link on purpose. Confined resolution only
    /// reports a lost race where it has had to reconsider which directory it is standing in,
    /// which is what a <c>..</c> component makes it do; a path of plain names resolving
    /// through the very same rename never produces one. So a path without <c>..</c> would
    /// exercise nothing here no matter how hard the renaming ran, and a resolver reached only
    /// by such paths would never take the retry at all.
    /// </para>
    /// <para>
    /// Missing is a legitimate answer: for part of every cycle the name genuinely does not
    /// exist. What is never legitimate is a lost race reaching the caller while the budget
    /// still had attempts left in it.
    /// </para>
    /// </remarks>
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task A_rename_under_a_resolution_is_absorbed_rather_than_reported()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The confined open is the Linux backend's.");
            return;
        }

        LinuxPlatformOps ops = new();
        if (!ops.Capabilities.SupportsConfinedOpen)
        {
            Assert.Skip("This host has no confined open: " + ops.ConfinedOpenUnavailableReason);
        }

        string present = Path.Combine(_root, "moving");
        string absent = Path.Combine(_root, "moved-away");
        Directory.CreateDirectory(Path.Combine(present, "inner"));

        CapResult<SafeDirHandle> rootResult = ops.OpenAmbientDirectory(_root, CapAccess.Read);
        Assert.True(rootResult.IsSuccess, rootResult.Error.FailureDescription);
        using SafeDirHandle root = rootResult.Value;

        Task churn = Task.Run(
            () =>
            {
                while (!_stopRenaming)
                {
                    TryRename(present, absent);
                    TryRename(absent, present);
                }
            },
            TestContext.Current.CancellationToken);

        try
        {
            for (int attempt = 0; attempt < Resolutions; attempt++)
            {
                CapResult<SafeDirHandle> result = ops.OpenConfinedDirectory(
                    root, "moving/inner/../inner", CapAccess.Read, ConfinedResolveOptions.None);

                if (result.IsSuccess)
                {
                    result.Value.Dispose();
                    continue;
                }

                Assert.True(
                    result.Error.Category == CapErrorCategory.NotFound,
                    "Resolving through a directory being renamed reported " +
                    result.Error.FailureDescription + ". The only outcomes a caller can make " +
                    "sense of here are the handle it asked for and the name not being there; " +
                    "a lost race is the implementation's business and must not escape it.");
            }
        }
        finally
        {
            _stopRenaming = true;
            await churn;

            // Whichever half of the cycle the churn stopped in, the fixture has to be put
            // back before it can be cleaned up.
            TryRename(absent, present);
        }

        _output.WriteLine(
            $"Resolutions: {Resolutions}. Races absorbed by retrying: {ops.ConfinedOpenRaceRetries}.");

        // Whether the kernel ever reported a lost race is down to how the two threads
        // interleaved, which no test can insist on. It can insist on knowing: a run where the
        // race never happened has checked that nothing went wrong and has not checked the
        // retry at all, and saying so is better than passing as though it had.
        if (ops.ConfinedOpenRaceRetries == 0)
        {
            Assert.Skip(
                "The kernel did not report a lost race in " + Resolutions + " resolutions " +
                "against a directory being renamed, so the retry was never reached on this " +
                "run and remains unexercised here.");
        }
    }

    /// <summary>A backend of its own, so a fault set on it reaches no other test.</summary>
    [SupportedOSPlatform("linux")]
    private static LinuxPlatformOps ConfinedOps()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The confined open is the Linux backend's.");
        }

        LinuxPlatformOps ops = new();
        if (!ops.Capabilities.SupportsConfinedOpen)
        {
            Assert.Skip("This host has no confined open: " + ops.ConfinedOpenUnavailableReason);
        }

        return ops;
    }

    [SupportedOSPlatform("linux")]
    private SafeDirHandle OpenRootWithInner(LinuxPlatformOps ops)
    {
        Directory.CreateDirectory(Path.Combine(_root, "inner"));
        CapResult<SafeDirHandle> rootResult = ops.OpenAmbientDirectory(_root, CapAccess.Read);
        Assert.True(rootResult.IsSuccess, rootResult.Error.FailureDescription);
        return rootResult.Value;
    }

    private static void TryRename(string from, string to)
    {
        try
        {
            Directory.Move(from, to);
        }
        catch (IOException)
        {
            // The other side of the cycle got there first, or the name is momentarily gone.
            // Both are the churn this is trying to create rather than anything to report.
        }
    }

    private const int Resolutions = 2_000;
}
