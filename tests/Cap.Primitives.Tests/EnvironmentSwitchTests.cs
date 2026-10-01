using Cap.Primitives.Interop.Unix;

namespace Cap.Primitives.Tests;

/// <summary>
/// Which values of the backends' environment variables turn a switch on, which leave it off,
/// and which are refused.
/// </summary>
/// <remarks>
/// Fed values directly rather than through the environment, so every leg runs it and no
/// case changes what a test running alongside it sees. The dangerous direction is a value
/// meant to turn a switch on being read as off, so <c>True</c> — what .NET and PowerShell
/// write for a boolean — is the case this exists for.
/// </remarks>
public sealed class EnvironmentSwitchTests
{
    private const string Variable = "CAPDOTNET_TEST_SWITCH";

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("yes", true)]
    [InlineData("Yes", true)]
    [InlineData("on", true)]
    [InlineData("ON", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    public void A_recognised_value_is_read_as_on_or_off(string? value, bool expected) =>
        Assert.Equal(expected, EnvironmentSwitch.IsEnabled(Variable, value));

    [Theory]
    [InlineData("maybe")]
    [InlineData("2")]
    [InlineData(" 1")]
    [InlineData("true ")]
    [InlineData("enabled")]
    public void An_unrecognised_value_is_refused_naming_the_variable_and_value(string value)
    {
        InvalidOperationException refused =
            Assert.Throws<InvalidOperationException>(() => EnvironmentSwitch.IsEnabled(Variable, value));

        Assert.Contains(Variable, refused.Message, StringComparison.Ordinal);
        Assert.Contains("'" + value + "'", refused.Message, StringComparison.Ordinal);
    }
}
