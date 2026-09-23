using System.Runtime.CompilerServices;

namespace Cap.Primitives;

/// <summary>
/// A token that must be presented wherever authority enters this process from outside it.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in this library derives its authority from something a caller already
/// holds: a directory handle names what is beneath it, and a handle derived from one carries
/// no more than its parent did. That chain has to begin somewhere, and the places it begins
/// — opening the first directory by an ordinary path, reading the system clock, taking
/// entropy from the operating system — are the only points at which a component can reach
/// something nobody handed it.
/// </para>
/// <para>
/// <strong>This is an auditing mechanism, not an enforcement one.</strong> Any code that can
/// call anything can call <see cref="Acquire"/>; nothing stops it and nothing is meant to.
/// What the token buys is that those points are enumerable. Searching a codebase for
/// <c>AmbientAuthority.Acquire</c> produces the complete list of places the process takes
/// authority it was not given, and a review can then ask of each one whether it belongs
/// there. An API that quietly reached the filesystem without a token would not appear on
/// that list, which is the only thing the token exists to prevent.
/// </para>
/// <para>
/// A token records where it was acquired, which is why <see cref="Acquire"/> has parameters
/// nobody passes. The site travels with the value, so a component handed a token can say in
/// a log where the authority it is exercising came from — and a token that has been passed
/// through three layers still names the line that took it rather than the line that used it.
/// </para>
/// <para>
/// The same sites can be collected for the whole process rather than one at a time. With
/// the recording switched on, every acquisition is recorded against its call site, and
/// <see cref="DescribeRecordedSites"/> answers what the search through the source answers,
/// for the program as it actually ran — including any authority taken inside a dependency
/// whose source nobody searched. See <see cref="RecordingSwitchName"/>.
/// </para>
/// <para>
/// <c>default(AmbientAuthority)</c> is not a token. A structure that could be conjured from
/// nothing would make the parameter a formality, so the only value that satisfies a
/// requirement for one is a value <see cref="Acquire"/> produced; anything else is refused
/// where it is presented, as an argument error rather than a security failure, because it is
/// a mistake in the calling code rather than an attack on the containment guarantee.
/// </para>
/// <para>
/// <strong>Thread safety.</strong> A token is immutable and may be passed between and used
/// from any number of threads. The static members, recording included, are safe to call
/// from any thread at once.
/// </para>
/// </remarks>
public readonly struct AmbientAuthority
{
    /// <summary>
    /// The application context switch that turns the recording of acquisition sites on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set it in the application's project file, which is the form that covers authority
    /// taken before the first line of user code runs:
    /// </para>
    /// <code>
    /// &lt;RuntimeHostConfigurationOption Include="Cap.Primitives.RecordAmbientAuthority" Value="true" /&gt;
    /// </code>
    /// <para>
    /// or from code, before anything takes authority:
    /// <c>AppContext.SetSwitch(AmbientAuthority.RecordingSwitchName, true)</c>. Read afresh
    /// at each acquisition, so it can also be turned on around one suspect phase of
    /// start-up; what was recorded while it was on stays recorded when it goes off.
    /// </para>
    /// </remarks>
    public const string RecordingSwitchName = "Cap.Primitives.RecordAmbientAuthority";

    /// <summary>
    /// The environment variable that turns the recording of acquisition sites on, for an
    /// operator who cannot rebuild: <c>CAPDOTNET_RECORD_AMBIENT_AUTHORITY=1</c>.
    /// </summary>
    /// <remarks>
    /// Read once, when the recording is first consulted, so it cannot be changed under a
    /// running process.
    /// </remarks>
    public const string RecordingVariableName = "CAPDOTNET_RECORD_AMBIENT_AUTHORITY";

    private readonly bool _acquired;
    private readonly string? _file;
    private readonly int _line;
    private readonly string? _member;

    private AmbientAuthority(string? file, int line, string? member)
    {
        _acquired = true;
        _file = file;
        _line = line;
        _member = member;
    }

    /// <summary>
    /// Whether an acquisition happening now would be added to the recorded sites.
    /// </summary>
    /// <remarks>
    /// Safe to read from any thread. The answer can change as soon as it is given, since the
    /// switch is read afresh each time and can be set from anywhere in the process.
    /// </remarks>
    public static bool IsRecording => AmbientAuthorityLog.IsRecording;

    /// <summary>
    /// Every place this process has taken ambient authority, in the order the places first
    /// did so, or an empty list when nothing was recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty is not the same answer as "nothing took any": with the recording off, which is
    /// the default, nothing is recorded however much authority is taken. Check
    /// <see cref="IsRecording"/> before reading anything into an empty list.
    /// </para>
    /// <para>
    /// Safe to read from any thread, while other threads are still acquiring. Each read
    /// returns a new list that nothing changes afterwards: a snapshot of what had been
    /// recorded by then, which a concurrent acquisition may already have added to.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<AmbientAuthoritySite> RecordedSites => AmbientAuthorityLog.Sites;

    /// <summary>
    /// The recorded sites as a report to print — typically once, after start-up, so that a
    /// deployment states in its own log what it reached for.
    /// </summary>
    /// <returns>The report, several lines long when anything was recorded.</returns>
    /// <remarks>
    /// <para>
    /// Says so plainly when the recording is off, rather than producing an empty report that
    /// reads like a clean bill of health.
    /// </para>
    /// <para>
    /// Safe to call from any thread; it reports from the same kind of snapshot
    /// <see cref="RecordedSites"/> takes.
    /// </para>
    /// </remarks>
    public static string DescribeRecordedSites() => AmbientAuthorityLog.Describe();

    /// <summary>
    /// Takes ambient authority, recording the call site.
    /// </summary>
    /// <param name="file">
    /// Filled in by the compiler with the source file of the call. Passing it explicitly is
    /// possible and pointless; the value is used only for diagnostics.
    /// </param>
    /// <param name="line">Filled in by the compiler with the line of the call.</param>
    /// <param name="member">
    /// Filled in by the compiler with the member the call was written in. Kept alongside the
    /// line because it is the half of the location that survives the file being edited.
    /// </param>
    /// <returns>A token naming this call site.</returns>
    /// <remarks>
    /// <para>
    /// Checks nothing, and costs nothing beyond a switch lookup. The call is the
    /// declaration: this line is where the process reaches past what it was given, and it is
    /// meant to be findable both by searching for this method by name and, at run time,
    /// through <see cref="RecordedSites"/>.
    /// </para>
    /// <para>
    /// Safe to call from any thread, any number of times at once. Concurrent acquisitions at
    /// one site are all counted.
    /// </para>
    /// </remarks>
    public static AmbientAuthority Acquire(
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0,
        [CallerMemberName] string? member = null)
    {
        if (AmbientAuthorityLog.IsRecording)
        {
            AmbientAuthorityLog.Add(file, line, member);
        }

        return new AmbientAuthority(file, line, member);
    }

    /// <summary>
    /// Where this token was acquired, or a note that it was never acquired at all.
    /// </summary>
    /// <returns>A description of the acquisition site, for a person to read.</returns>
    /// <remarks>
    /// For logs and assertion messages. Nothing parses it, and the exact wording is not part
    /// of the contract.
    /// </remarks>
    public override string ToString()
    {
        if (!_acquired)
        {
            return "ambient authority (never acquired)";
        }

        if (_file is null)
        {
            return _member is null
                ? "ambient authority"
                : $"ambient authority acquired in {_member}";
        }

        return _member is null
            ? $"ambient authority acquired at {_file}:{_line}"
            : $"ambient authority acquired at {_file}:{_line} in {_member}";
    }

    /// <summary>
    /// True when this value came from <see cref="Acquire"/> rather than from
    /// <c>default</c>.
    /// </summary>
    internal bool IsAcquired => _acquired;

    /// <summary>
    /// Refuses a token that was never acquired.
    /// </summary>
    /// <param name="parameterName">The parameter the token arrived through.</param>
    /// <remarks>
    /// Called by every API that requires the token, before it does anything else. The check
    /// is what keeps the requirement from being satisfiable by <c>default</c>, and so what
    /// keeps the search for acquisition sites complete.
    /// </remarks>
    internal void Demand(string parameterName)
    {
        if (!_acquired)
        {
            throw new ArgumentException(
                "Ambient authority must be taken explicitly, by calling " +
                $"{nameof(AmbientAuthority)}.{nameof(Acquire)}(). A default value is not a token: " +
                "accepting one would let this call reach outside the capability graph without " +
                "appearing in a search for the places that do.",
                parameterName);
        }
    }
}
