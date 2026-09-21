namespace Cap.Tests;

/// <summary>
/// Preconditions that must hold in every cap-dotnet test assembly.
/// </summary>
internal static class TestEnvironment
{
    /// <summary>
    /// True when the test process is running as root (Unix) or elevated (Windows).
    /// </summary>
    /// <remarks>
    /// This matters more than it looks. Most of the escape corpus (0023) consists of
    /// negative tests -- "this operation must fail". Running as root makes several of
    /// them pass for entirely the wrong reason: a DAC permission check that would have
    /// stopped the attack in a normal process is skipped, so the test goes green without
    /// ever exercising the containment logic it was written to protect.
    /// </remarks>
    public static bool IsPrivileged => Environment.IsPrivilegedProcess;
}

public sealed class ProcessPrivilegeGuard
{
    [Fact]
    public void Test_process_is_not_privileged()
    {
        Assert.False(
            TestEnvironment.IsPrivileged,
            "cap-dotnet tests must not run as root or elevated: negative containment " +
            "tests silently pass when DAC checks are bypassed. Re-run as an ordinary user.");
    }
}
