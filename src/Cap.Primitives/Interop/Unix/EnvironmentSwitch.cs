namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// Reads the environment variables that change how the Unix backends resolve and enumerate.
/// </summary>
/// <remarks>
/// <para>
/// The spellings are the ones people actually write: <c>1</c>, <c>true</c>, <c>yes</c> and
/// <c>on</c> turn a switch on, and an unset or empty variable, <c>0</c>, <c>false</c>,
/// <c>no</c> and <c>off</c> leave it off, all regardless of case. <c>True</c> in particular
/// has to count, because it is what .NET and PowerShell write for a boolean.
/// </para>
/// <para>
/// Anything else is refused rather than read as off. These switches exist so an operator can
/// change the posture without a rebuild, and the way that goes wrong is a value that was
/// meant to turn one on being quietly ignored: the process then runs with a configuration
/// other than the one its operator believes it has, and nothing says so. Failing at the
/// first use, with the variable and its value named, is the only way that mistake is seen.
/// </para>
/// </remarks>
internal static class EnvironmentSwitch
{
    /// <summary>Reads <paramref name="variableName"/> from the environment.</summary>
    /// <exception cref="InvalidOperationException">The variable holds an unrecognised value.</exception>
    public static bool IsEnabled(string variableName) =>
        IsEnabled(variableName, Environment.GetEnvironmentVariable(variableName));

    /// <summary>Interprets <paramref name="value"/> as the value of <paramref name="variableName"/>.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> is not a recognised spelling.</exception>
    public static bool IsEnabled(string variableName, string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            value == "0" ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value == "1" ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        throw new InvalidOperationException(
            $"The environment variable {variableName} is set to '{value}', which is not a recognised value. " +
            "Use 1, true, yes or on to turn it on, or 0, false, no, off or an empty value to leave it off.");
    }
}
