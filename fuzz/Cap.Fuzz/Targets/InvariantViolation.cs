namespace Cap.Fuzz.Targets;

/// <summary>
/// Thrown when code under test broke a rule it promises to keep.
/// </summary>
/// <remarks>
/// An exception rather than a return value because the fuzzer's only signal is the process
/// failing. An unhandled exception is what the fuzzer records as a crash, and it keeps the
/// input that caused it. An exception the code under test throws by itself is recorded the
/// same way, since none of the targets is allowed to throw.
/// </remarks>
internal sealed class InvariantViolation : Exception
{
    public InvariantViolation(string message)
        : base(message)
    {
    }

    public InvariantViolation()
    {
    }

    public InvariantViolation(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Throws unless <paramref name="condition"/> holds.</summary>
    public static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvariantViolation(message);
        }
    }

    /// <summary>
    /// Shows a string with its control and non-ASCII characters escaped, so that a failure
    /// message names the input exactly.
    /// </summary>
    public static string Show(string text)
    {
        System.Text.StringBuilder shown = new(text.Length + 2);
        _ = shown.Append('"');
        foreach (char c in text)
        {
            _ = c is >= ' ' and <= '~' and not '"' and not '\\'
                ? shown.Append(c)
                : shown.Append(System.Globalization.CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
        }

        return shown.Append('"').ToString();
    }
}
