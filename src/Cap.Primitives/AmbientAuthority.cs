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
/// <c>default(AmbientAuthority)</c> is not a token. A structure that could be conjured from
/// nothing would make the parameter a formality, so the only value that satisfies a
/// requirement for one is a value <see cref="Acquire"/> produced; anything else is refused
/// where it is presented, as an argument error rather than a security failure, because it is
/// a mistake in the calling code rather than an attack on the containment guarantee.
/// </para>
/// </remarks>
public readonly struct AmbientAuthority
{
    private readonly bool _acquired;
    private readonly string? _file;
    private readonly int _line;

    private AmbientAuthority(string? file, int line)
    {
        _acquired = true;
        _file = file;
        _line = line;
    }

    /// <summary>
    /// Takes ambient authority, recording the call site.
    /// </summary>
    /// <param name="file">
    /// Filled in by the compiler with the source file of the call. Passing it explicitly is
    /// possible and pointless; the value is used only for diagnostics.
    /// </param>
    /// <param name="line">Filled in by the compiler with the line of the call.</param>
    /// <remarks>
    /// Costs nothing and checks nothing. The call is the declaration: this line is where the
    /// process reaches past what it was given, and it is meant to be findable by searching
    /// for this method by name.
    /// </remarks>
    public static AmbientAuthority Acquire(
        [CallerFilePath] string? file = null,
        [CallerLineNumber] int line = 0) =>
        new(file, line);

    /// <summary>
    /// Where this token was acquired, or a note that it was never acquired at all.
    /// </summary>
    /// <remarks>
    /// For logs and assertion messages. Nothing parses it, and the exact wording is not part
    /// of the contract.
    /// </remarks>
    public override string ToString() =>
        _acquired
            ? _file is null
                ? "ambient authority"
                : $"ambient authority acquired at {_file}:{_line}"
            : "ambient authority (never acquired)";

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
