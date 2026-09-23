namespace Cap.Stress.Tests;

/// <summary>
/// Where the counts from each race go once it is over.
/// </summary>
/// <remarks>
/// <para>
/// A race passes or fails on two outcomes only — reaching outside, and reaching something that
/// cannot be vouched for — but the rest of what it counts is the measurement. How often a
/// resolution was redirected to a different object inside the sandbox is the size of the window
/// the name-at-a-time walk leaves open, and how often an attack was seen and refused says the
/// race was really fought. Those numbers are written to the test log every time, and, when the
/// run names a file, appended to it as a table.
/// </para>
/// </remarks>
internal static class StressReport
{
    private static readonly Lock Gate = new();
    private static bool s_headerWritten;

    /// <summary>Writes one race's counts to the log, and to the report file if there is one.</summary>
    public static void Publish(ITestOutputHelper output, string race, string backend, Tally tally)
    {
        output.WriteLine($"{race} on {backend}: {tally}");

        if (StressSettings.ReportPath is not { } path)
        {
            return;
        }

        lock (Gate)
        {
            using StreamWriter writer = File.AppendText(path);
            if (!s_headerWritten)
            {
                writer.WriteLine();
                writer.WriteLine($"### Stress races ({StressSettings.Iterations:N0} attempts per race and backend, {Environment.OSVersion.Platform} {Environment.OSVersion.Version}, {Environment.ProcessorCount} processors)");
                writer.WriteLine();
                writer.WriteLine("| Race | Backend | Attempts | Consistent | Redirected | Refused as escape | Missing | Other refusal | Escaped | Unidentified | Adversary cycles |");
                writer.WriteLine("|---|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|");
                s_headerWritten = true;
            }

            writer.WriteLine(
                $"| {race} | {backend} | {tally.Attempts:N0} | {tally[Outcome.Consistent]:N0} | {tally[Outcome.Redirected]:N0} | " +
                $"{tally[Outcome.RefusedAsEscape]:N0} | {tally[Outcome.Missing]:N0} | {tally[Outcome.OtherRefusal]:N0} | " +
                $"{tally[Outcome.Escaped]:N0} | {tally[Outcome.Unidentified]:N0} | " +
                $"{(tally.AdversaryCycles is { } cycles ? cycles.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : "n/a")} |");
        }
    }
}
