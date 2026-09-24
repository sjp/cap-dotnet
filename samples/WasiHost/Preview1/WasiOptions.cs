using Cap.Rand;
using Cap.Std;

namespace WasiHost.Preview1;

/// <summary>
/// Everything a guest is given. The guest can reach nothing else.
/// </summary>
/// <remarks>
/// Every source of authority is a value handed in: each preopened directory is a
/// <see cref="Dir"/>, the clock is a <see cref="TimeProvider"/>, and entropy is an
/// <see cref="IRandomSource"/>. The adapter is built with the analyzer in strict mode, so it
/// could not reach for the filesystem, the clock or the system's random numbers on its own
/// even by mistake; whatever the guest gets, the host put here.
/// </remarks>
public sealed class WasiOptions
{
    /// <summary>The guest's arguments, the program name first.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>The guest's environment, as <c>NAME=value</c> pairs.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Environment { get; init; } = [];

    /// <summary>
    /// The directories the guest starts with, and the name each is announced under. The
    /// adapter takes ownership of each <see cref="Dir"/> and disposes it when the guest closes
    /// the descriptor or the adapter is disposed.
    /// </summary>
    public IReadOnlyList<(string GuestName, Dir Dir)> Preopens { get; init; } = [];

    /// <summary>What <c>clock_time_get</c> reads.</summary>
    public required TimeProvider Clock { get; init; }

    /// <summary>What <c>random_get</c> draws from.</summary>
    public required IRandomSource Random { get; init; }

    /// <summary>The guest's standard input. Empty when not given.</summary>
    public Stream StandardInput { get; init; } = Stream.Null;

    /// <summary>Where the guest's standard output goes. Discarded when not given.</summary>
    public Stream StandardOutput { get; init; } = Stream.Null;

    /// <summary>Where the guest's standard error goes. Discarded when not given.</summary>
    public Stream StandardError { get; init; } = Stream.Null;
}
