namespace Cap.Tests;

/// <summary>
/// Properties of the host and its filesystem that a case may need, or whose presence changes
/// what a case should see.
/// </summary>
[Flags]
internal enum HostFeature
{
    None = 0,

    /// <summary>Symbolic links can be created here.</summary>
    Symlinks = 1 << 0,

    /// <summary>Hard links can be created here.</summary>
    HardLinks = 1 << 1,

    /// <summary>Windows junctions can be created here.</summary>
    Junctions = 1 << 2,

    /// <summary>The Linux process filesystem, and its synthetic links, are mounted.</summary>
    ProcessFilesystem = 1 << 3,

    /// <summary>Names differing only in case reach the same entry.</summary>
    CaseInsensitive = 1 << 4,

    /// <summary>The composed and decomposed spellings of a name reach the same entry.</summary>
    NormalizationInsensitive = 1 << 5,

    /// <summary>
    /// The volume stores a name containing the characters Windows reserves, as POSIX allows.
    /// Absent on a FAT volume, which refuses them even under Linux.
    /// </summary>
    PosixNames = 1 << 6,

    /// <summary>
    /// A FIFO can be created here. Absent on Windows, on a FAT volume and in a filesystem held
    /// in memory.
    /// </summary>
    SpecialFiles = 1 << 7,
}

/// <summary>
/// The host features a run was set up to have, named in
/// <c>CAPDOTNET_EXPECT_HOST_FEATURES</c>.
/// </summary>
/// <remarks>
/// A test that needs something the host lacks stands aside, which is right on a machine that
/// cannot provide it and wrong on one that was provisioned to. Symbolic links on Windows need a
/// privilege or developer mode, and <c>/proc</c> can be masked, so a runner that lost either
/// would otherwise go green with the heart of the threat model untested. Where the variable
/// names a feature, its absence is a failure rather than a skip.
/// </remarks>
internal static class ExpectedHostFeatures
{
    public const string Variable = "CAPDOTNET_EXPECT_HOST_FEATURES";

    private static readonly Lazy<HostFeature> Configured =
        new(() => Parse(Environment.GetEnvironmentVariable(Variable)));

    /// <summary>The features this run was set up to have; none when the variable is unset.</summary>
    public static HostFeature Value => Configured.Value;

    /// <summary>
    /// Reads a comma-separated list of <see cref="HostFeature"/> names. A name that is not one
    /// is a mistake in whoever set it, and ignoring it would silently expect less than they meant.
    /// </summary>
    public static HostFeature Parse(string? configured)
    {
        HostFeature features = HostFeature.None;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return features;
        }

        foreach (string token in configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            HostFeature feature = Array.Find(
                Enum.GetValues<HostFeature>(),
                candidate => candidate != HostFeature.None &&
                             string.Equals(candidate.ToString(), token, StringComparison.Ordinal));

            if (feature == HostFeature.None)
            {
                throw new InvalidOperationException(
                    $"{Variable} is '{configured}', and '{token}' is not a host feature. Name any of " +
                    $"{string.Join(", ", Enum.GetNames<HostFeature>().Where(name => name != nameof(HostFeature.None)))}.");
            }

            features |= feature;
        }

        return features;
    }

    /// <summary>
    /// Returns when <paramref name="present"/> has everything <paramref name="needed"/> names;
    /// otherwise fails if this run was set up to have what is missing, and skips if it was not.
    /// </summary>
    public static void Require(HostFeature needed, HostFeature present, string reason) =>
        Require(needed, present, Value, reason);

    /// <summary>As the other overloads, for one feature a test has already probed for.</summary>
    public static void Require(HostFeature feature, bool present, string reason) =>
        Require(feature, present ? feature : HostFeature.None, Value, reason);

    /// <summary>As the other overload, against an expectation given rather than configured.</summary>
    public static void Require(HostFeature needed, HostFeature present, HostFeature expected, string reason)
    {
        (HostFeature missing, HostFeature promised) = Shortfall(needed, present, expected);
        if (missing == HostFeature.None)
        {
            return;
        }

        Assert.True(
            promised == HostFeature.None,
            $"This host or volume does not have {promised}, but {Variable} says this run was set up with it. {reason}");
        Assert.Skip($"This host or volume does not have {missing}. {reason}");
    }

    /// <summary>
    /// What of <paramref name="needed"/> the host lacks, and of that, what this run was set up
    /// to have: a test runs when the first is empty, fails when the second is not, and skips
    /// otherwise.
    /// </summary>
    public static (HostFeature Missing, HostFeature Promised) Shortfall(
        HostFeature needed, HostFeature present, HostFeature expected)
    {
        HostFeature missing = needed & ~present;
        return (missing, missing & expected);
    }
}

/// <summary>Probing for a kind of link by making one, for tests that need to plant them.</summary>
/// <remarks>
/// Asked of the filesystem rather than derived from the platform. Windows needs either developer
/// mode or an elevated token for a symbolic link, and which of those a machine has is not
/// something a platform check can answer.
/// </remarks>
internal static class HostLinks
{
    /// <summary>
    /// Runs <paramref name="create"/>, which makes a link of the kind <paramref name="feature"/>
    /// names; if the host refuses, fails or skips as <see cref="ExpectedHostFeatures.Require"/> does.
    /// </summary>
    public static void Require(HostFeature feature, Action create)
    {
        if (!TryCreate(create, out string? refusal))
        {
            ExpectedHostFeatures.Require(feature, HostFeature.None, $"Making one was refused: {refusal}");
        }
    }

    /// <summary>Whether <paramref name="create"/> succeeds, and if not, why.</summary>
    public static bool TryCreate(Action create, out string? refusal)
    {
        try
        {
            create();
            refusal = null;
            return true;
        }
        catch (Exception thrown) when (
            thrown is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            refusal = thrown.Message;
            return false;
        }
    }
}

public sealed class HostFeatureExpectationGuard
{
    /// <summary>
    /// A misspelt expectation fails every assembly, rather than only one whose test happened to
    /// need a feature the host lacked.
    /// </summary>
    [Fact]
    public void The_expected_host_features_are_all_features() => _ = ExpectedHostFeatures.Value;
}
