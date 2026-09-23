using System.Diagnostics;

namespace Cap.Stress.Tests;

/// <summary>
/// Running out of descriptors part-way through resolution.
/// </summary>
/// <remarks>
/// <para>
/// The work is done in a child process, for the reasons given on <see cref="ExhaustionChild"/>;
/// this side builds the tree, starts the child and reads its verdict.
/// </para>
/// <para>
/// Unix only. Windows has no descriptor limit to lower: a process may hold millions of handles
/// before the kernel refuses another, which is not a limit a test can reach in reasonable time,
/// and nothing short of it produces the refusal this test is about. What Windows does get is
/// the half that does not need the limit — every race in this suite checks, on every platform,
/// that the handles it opened were all closed.
/// </para>
/// </remarks>
public sealed class ExhaustionTests(ITestOutputHelper output)
{
    /// <summary>
    /// Running out of descriptors is a failure to open and nothing else: no escape, no demotion,
    /// and no descriptor left behind.
    /// </summary>
    [Fact]
    public async Task Running_out_of_descriptors_fails_closed_and_leaves_nothing_open()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("Only Unix has a descriptor limit a test can lower and reach.");
        }

        using StressArena arena = new();
        arena.RequireSymbolicLinks();

        string deep = arena.Inside(ExhaustionChild.DeepDirectories);
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Join(deep, StressArena.FileName), "inside");
        Directory.CreateDirectory(arena.Inside(StressArena.DirectoryName));
        Directory.CreateSymbolicLink(arena.Inside(ExhaustionChild.OutwardLink), arena.Outside(StressArena.DirectoryName));
        string outsideBefore = arena.SnapshotOutside();

        using Process child = ChildProcess.Start(new Dictionary<string, string>
        {
            [ExhaustionChild.RequestVariable] = arena.SandboxPath,
        });

        Task<string> errors = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        string report = await child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await child.WaitForExitAsync(TestContext.Current.CancellationToken);
        string errorText = await errors;
        output.WriteLine(report);
        output.WriteLine(errorText);

        Assert.True(
            child.ExitCode == 0 && report.Contains(ExhaustionChild.Passed, StringComparison.Ordinal),
            $"The child exited with {child.ExitCode}:\n{report}\n{errorText}");
        Assert.Equal(outsideBefore, arena.SnapshotOutside());
    }
}
