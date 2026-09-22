using System.Globalization;

namespace Cap.Primitives;

/// <summary>
/// One place in a program that took ambient authority, and how often it did so.
/// </summary>
/// <remarks>
/// <para>
/// A site is a source location, not an occurrence: a line inside a loop that takes authority
/// a thousand times is one site with a count of a thousand, and the record of it costs the
/// same as a line that took authority once. That is deliberate. The question the record
/// exists to answer is <em>where</em> a process reaches outside itself, which is a property
/// of the program rather than of the run, and an answer shaped that way stays a fixed size
/// however long the process lives — so a deployment can leave the recording on.
/// </para>
/// <para>
/// The file and line are what the compiler filled in at the call, so they name the machine
/// the library was built on rather than anything present at run time, and a line number
/// describes the source as it was then. The member name is the part that survives an edit,
/// which is why it is recorded alongside them.
/// </para>
/// <para>
/// Times are measured from the first acquisition this process recorded, not from a wall
/// clock. Elapsed time is what makes the record legible — whether a site fired during
/// start-up or an hour into serving traffic is the interesting part — and reading the time
/// of day to answer that would mean this library consulted an ambient clock in order to
/// report on ambient authority.
/// </para>
/// </remarks>
public readonly struct AmbientAuthoritySite
{
    internal AmbientAuthoritySite(string? file, int line, string? member, int count, TimeSpan firstAcquired)
    {
        File = file;
        Line = line;
        Member = member;
        Count = count;
        FirstAcquired = firstAcquired;
    }

    /// <summary>
    /// The source file of the call, as the compiler recorded it, or <see langword="null"/>
    /// when the caller supplied the argument itself and passed nothing.
    /// </summary>
    public string? File { get; }

    /// <summary>The line of the call, or zero when it was not recorded.</summary>
    public int Line { get; }

    /// <summary>
    /// The member the call was written in, or <see langword="null"/> when it was not
    /// recorded.
    /// </summary>
    public string? Member { get; }

    /// <summary>How many times this site has taken authority so far.</summary>
    public int Count { get; }

    /// <summary>
    /// How long after the first recorded acquisition in this process this site first took
    /// authority. Zero for the site that was first.
    /// </summary>
    public TimeSpan FirstAcquired { get; }

    /// <summary>
    /// The site as one line of a report.
    /// </summary>
    /// <remarks>
    /// For people reading a dump. Nothing parses it and the exact wording is not part of the
    /// contract.
    /// </remarks>
    public override string ToString()
    {
        string where = File is null
            ? Member is null ? "an unrecorded location" : $"{Member} (no source location recorded)"
            : Member is null ? $"{File}:{Line}" : $"{File}:{Line} in {Member}";

        string often = Count == 1
            ? "taken once"
            : string.Create(CultureInfo.InvariantCulture, $"taken {Count} times, first");

        string when = string.Create(CultureInfo.InvariantCulture, $"+{FirstAcquired.TotalSeconds:0.000}s");

        return $"{where}: {often} at {when}";
    }
}
