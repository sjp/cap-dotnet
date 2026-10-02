using Cap.Tests;
using Xunit.Sdk;

namespace Cap.Escape.Tests;

/// <summary>
/// A run set up to have a host feature has it, so that the cases needing it ran rather than
/// stood aside.
/// </summary>
[Collection(CorpusGroup.Name)]
public sealed class HostFeaturesTests
{
    /// <summary>
    /// Everything named in <c>CAPDOTNET_EXPECT_HOST_FEATURES</c> was found. Thousands of corpus
    /// rows fail as well when it was not, but this says why in one place.
    /// </summary>
    [Fact]
    public void The_features_this_run_was_set_up_with_are_present()
    {
        HostFeature missing = ExpectedHostFeatures.Value & ~HostFeatures.Current;

        Assert.True(
            missing == HostFeature.None,
            $"{ExpectedHostFeatures.Variable} says this run was set up with {ExpectedHostFeatures.Value}, " +
            $"but the host or volume has only {HostFeatures.Current}, so it is missing {missing}." +
            HostFeatures.Explain(missing, HostFeatures.Refusals));
    }

    /// <summary>
    /// A feature the probe tried to make and could not carries the refusal into the message, so
    /// a Windows run whose junction probe failed says what the shell said.
    /// </summary>
    [Fact]
    public void A_missing_feature_is_explained_by_what_refused_it()
    {
        Dictionary<HostFeature, string> refusals = new()
        {
            [HostFeature.Junctions] = "The system cannot find the path specified.",
            [HostFeature.Symlinks] = "A required privilege is not held by the client.",
        };

        string explained = HostFeatures.Explain(HostFeature.Junctions | HostFeature.CaseInsensitive, refusals);

        Assert.Equal(" Making Junctions was refused: The system cannot find the path specified.", explained);
    }

    [Fact]
    public void A_feature_present_or_only_looked_for_adds_nothing()
    {
        Dictionary<HostFeature, string> refusals = new() { [HostFeature.Junctions] = "Refused." };

        Assert.Empty(HostFeatures.Explain(HostFeature.None, refusals));
        Assert.Empty(HostFeatures.Explain(HostFeature.CaseInsensitive, refusals));
    }

    [Fact]
    public void The_expectation_is_a_comma_separated_list_of_feature_names()
    {
        Assert.Equal(HostFeature.None, ExpectedHostFeatures.Parse(null));
        Assert.Equal(HostFeature.None, ExpectedHostFeatures.Parse(" "));
        Assert.Equal(
            HostFeature.Symlinks | HostFeature.HardLinks | HostFeature.ProcessFilesystem,
            ExpectedHostFeatures.Parse("Symlinks, HardLinks,ProcessFilesystem"));
    }

    [Theory]
    [InlineData("Symlinks,Hardlinks")]
    [InlineData("Symlinks;HardLinks")]
    [InlineData("None")]
    [InlineData("1")]
    public void An_unknown_feature_name_in_the_expectation_is_refused(string configured)
    {
        InvalidOperationException thrown =
            Assert.Throws<InvalidOperationException>(() => ExpectedHostFeatures.Parse(configured));

        Assert.Contains(ExpectedHostFeatures.Variable, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_case_needing_a_feature_the_run_expects_but_the_host_lacks_fails_naming_it()
    {
        XunitException thrown = Assert.ThrowsAny<XunitException>(() => ExpectedHostFeatures.Require(
            HostFeature.Symlinks | HostFeature.Junctions, HostFeature.Symlinks, HostFeature.Junctions, "Why."));

        Assert.IsNotType<SkipException>(thrown);
        Assert.Contains(nameof(HostFeature.Junctions), thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Asked of the decision rather than of <see cref="ExpectedHostFeatures.Require(HostFeature, HostFeature, HostFeature, string)"/>,
    /// since a skip thrown inside a test skips that test even when caught.
    /// </summary>
    [Fact]
    public void A_case_needing_a_feature_the_run_does_not_expect_is_skipped()
    {
        Assert.Equal(
            (HostFeature.Junctions, HostFeature.None),
            ExpectedHostFeatures.Shortfall(HostFeature.Junctions, HostFeature.Symlinks, HostFeature.Symlinks));
    }

    [Fact]
    public void A_case_whose_needs_are_met_runs()
    {
        Assert.Equal(
            (HostFeature.None, HostFeature.None),
            ExpectedHostFeatures.Shortfall(
                HostFeature.Symlinks, HostFeature.Symlinks | HostFeature.HardLinks, HostFeature.Symlinks));
        ExpectedHostFeatures.Require(
            HostFeature.Symlinks, HostFeature.Symlinks | HostFeature.HardLinks, HostFeature.Symlinks, "Why.");
    }
}
