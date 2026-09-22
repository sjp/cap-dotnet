using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Cap.Primitives;

/// <summary>
/// The process-wide record of where ambient authority was taken.
/// </summary>
/// <remarks>
/// <para>
/// Off unless asked for, and free when off: a process that never turns the recording on
/// pays one switch lookup per acquisition, at a call that already costs a directory open or
/// a system-entropy read. When it is on, the record is keyed by call site rather than by
/// occurrence, so it stops growing once every site that will ever fire has fired.
/// </para>
/// <para>
/// There is no way to clear it. A record that can be erased answers a weaker question than
/// the one being asked — a component could take authority and then tidy up after itself —
/// and the property worth having is that whatever was recorded stays recorded for as long
/// as the process runs.
/// </para>
/// <para>
/// The switch is read at each acquisition rather than latched, so a host can turn the
/// recording on and off around a suspect phase of start-up. Turning it off stops new
/// entries; it does not discard the ones already taken.
/// </para>
/// </remarks>
internal static class AmbientAuthorityLog
{
    private static readonly ConcurrentDictionary<SiteKey, Entry> Entries = new();

    /// <summary>
    /// The environment's answer, read once. Unlike the application context switch, this one
    /// cannot change during the run, and reading it per acquisition would allocate a string
    /// each time to reach the same conclusion.
    /// </summary>
    private static readonly bool RecordingForcedByEnvironment =
        Environment.GetEnvironmentVariable(AmbientAuthority.RecordingVariableName) is "1" or "true" or "TRUE";

    /// <summary>
    /// The timestamp every recorded time is measured from: the first acquisition that was
    /// recorded, so that the first line of a report reads as zero.
    /// </summary>
    private static long _origin;

    /// <summary>Hands out the order sites are reported in.</summary>
    private static int _sequence;

    /// <summary>Whether an acquisition happening now would be recorded.</summary>
    internal static bool IsRecording =>
        RecordingForcedByEnvironment ||
        (AppContext.TryGetSwitch(AmbientAuthority.RecordingSwitchName, out bool enabled) && enabled);

    /// <summary>Records one acquisition against its call site.</summary>
    internal static void Add(string? file, int line, string? member)
    {
        long now = Stopwatch.GetTimestamp();

        // The first acquisition recorded in this process establishes the origin; every later
        // one finds it already set. Two threads racing here can leave one of them with a
        // timestamp fractionally before the origin they lost the race to, which is reported
        // as zero rather than as a negative age.
        Interlocked.CompareExchange(ref _origin, now, 0);
        TimeSpan age = Stopwatch.GetElapsedTime(Volatile.Read(ref _origin), now);

        Entry entry = Entries.GetOrAdd(
            new SiteKey(file, line, member),
            static (_, first) => new Entry(Interlocked.Increment(ref _sequence), first),
            age < TimeSpan.Zero ? TimeSpan.Zero : age);

        Interlocked.Increment(ref entry.Count);
    }

    /// <summary>
    /// Every site recorded so far, in the order they first took authority.
    /// </summary>
    /// <remarks>
    /// A snapshot, taken while the process may still be acquiring. It is therefore a list of
    /// what had happened by the time it was asked for, which is all any answer to this
    /// question can be.
    /// </remarks>
    internal static IReadOnlyList<AmbientAuthoritySite> Sites =>
        [.. Entries
            .OrderBy(pair => pair.Value.Ordinal)
            .Select(pair => new AmbientAuthoritySite(
                pair.Key.File,
                pair.Key.Line,
                pair.Key.Member,
                Volatile.Read(ref pair.Value.Count),
                pair.Value.FirstAcquired))];

    /// <summary>The record as a report meant to be printed.</summary>
    internal static string Describe()
    {
        IReadOnlyList<AmbientAuthoritySite> sites = Sites;

        if (sites.Count == 0)
        {
            return IsRecording
                ? "Ambient authority: recording is on; nothing has taken any."
                : "Ambient authority: recording is off, so nothing was recorded. Turn it on with the " +
                  $"{AmbientAuthority.RecordingSwitchName} application context switch, or " +
                  $"{AmbientAuthority.RecordingVariableName}=1, before the process takes any.";
        }

        StringBuilder report = new();
        report.Append("Ambient authority was taken at ")
              .Append(sites.Count)
              .Append(sites.Count == 1 ? " site" : " sites")
              .Append(':');

        foreach (AmbientAuthoritySite site in sites)
        {
            report.AppendLine().Append("  ").Append(site.ToString());
        }

        return report.ToString();
    }

    /// <summary>What makes two acquisitions the same site.</summary>
    private readonly record struct SiteKey(string? File, int Line, string? Member);

    /// <summary>
    /// What is kept per site. A class rather than a value so that the count can be raised
    /// in place by whichever thread arrives, without replacing the entry the others hold.
    /// </summary>
    private sealed class Entry(int ordinal, TimeSpan firstAcquired)
    {
        /// <summary>Raised by <see cref="Interlocked"/>, so not a property.</summary>
        public int Count;

        public int Ordinal { get; } = ordinal;

        public TimeSpan FirstAcquired { get; } = firstAcquired;
    }
}
