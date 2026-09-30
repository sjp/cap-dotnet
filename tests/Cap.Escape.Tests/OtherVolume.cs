namespace Cap.Escape.Tests;

/// <summary>
/// A directory on a different filesystem from the one arenas are built on, named in
/// <see cref="Variable"/>, for the attacks that need a move or a link to cross between two.
/// </summary>
/// <remarks>
/// Named rather than found: which directories sit on another volume is a fact about the
/// machine, and the one place that knows it is whoever set the run up. Where the run was set up
/// with one, <see cref="ExpectedVariable"/> says so, and a missing or unsuitable directory fails
/// the test instead of skipping it.
/// </remarks>
internal static class OtherVolume
{
    /// <summary>The environment variable naming the directory.</summary>
    public const string Variable = "CAPDOTNET_TEST_OTHER_VOLUME";

    /// <summary>Set to <c>1</c> where the run was set up with such a directory.</summary>
    public const string ExpectedVariable = "CAPDOTNET_EXPECT_OTHER_VOLUME";

    /// <summary>
    /// The directory, or a skip (or a failure, where one was promised) when there is none.
    /// </summary>
    public static string Require()
    {
        bool promised = Environment.GetEnvironmentVariable(ExpectedVariable) == "1";
        string? configured = Environment.GetEnvironmentVariable(Variable);

        if (HostTree.InMemory)
        {
            Assert.Skip($"The filesystem held in memory is one volume, and {Variable} names a place on the disk.");
        }

        if (string.IsNullOrEmpty(configured))
        {
            Assert.False(
                promised,
                $"No second volume was named for this run, but {ExpectedVariable} says it was set " +
                $"up with one. Name a directory on another filesystem in {Variable}.");
            Assert.Skip($"No second volume was named for this run. Name a directory on another filesystem in {Variable}.");
        }

        Assert.True(
            HostDirectory.Exists(configured),
            $"{Variable} names '{configured}', which is not a directory.");
        return configured;
    }
}
