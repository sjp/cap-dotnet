using System.Globalization;

namespace Cap.Primitives;

/// <summary>
/// What a request to set a file's times asks for one of them: leave it alone, stamp it with
/// the current time, or give it a particular instant.
/// </summary>
/// <remarks>
/// <para>
/// <strong>"Now" is a request, not a value.</strong> <see cref="Now"/> carries no instant: the
/// time is taken by the filesystem at the moment it records the change, the way it takes one
/// when a file is written. Nothing in this library reads a clock to fill it in on the caller's
/// behalf, so asking for it does not require a clock capability — and grants none, since
/// whoever holds a handle that can write a file can already learn the time by writing it and
/// asking when that happened.
/// </para>
/// <para>
/// The default value is <see cref="Unchanged"/>, so a time left out of a request is never
/// changed by accident.
/// </para>
/// </remarks>
public readonly struct CapFileTime : IEquatable<CapFileTime>
{
    private readonly Request _request;
    private readonly DateTimeOffset _value;

    private CapFileTime(Request request, DateTimeOffset value)
    {
        _request = request;
        _value = value;
    }

    private enum Request : byte
    {
        Unchanged = 0,
        Now,
        At,
    }

    /// <summary>Leave this time as it is. The default value.</summary>
    public static CapFileTime Unchanged => default;

    /// <summary>
    /// Set this time to the moment the filesystem records the change, as it would for a write.
    /// </summary>
    public static CapFileTime Now => new(Request.Now, default);

    /// <summary>Whether this asks for the time to be left as it is.</summary>
    public bool IsUnchanged => _request == Request.Unchanged;

    /// <summary>Whether this asks for the time the filesystem records the change.</summary>
    public bool IsNow => _request == Request.Now;

    /// <summary>Set this time to a given instant.</summary>
    /// <param name="value">
    /// The instant. Its offset does not matter: filesystems record an instant, not a local
    /// time, so two values that name the same instant ask for the same thing. It is stored as
    /// precisely as the filesystem allows, which may be coarser than the value given, and a
    /// filesystem that cannot record an instant this early or this late at all refuses it.
    /// </param>
    public static CapFileTime At(DateTimeOffset value) => new(Request.At, value.ToUniversalTime());

    /// <summary>Reads the instant this asks for, when it asks for one.</summary>
    /// <param name="value">The instant, in UTC, when this returns true.</param>
    /// <returns>True when this was made by <see cref="At"/>.</returns>
    public bool TryGetValue(out DateTimeOffset value)
    {
        value = _value;
        return _request == Request.At;
    }

    /// <inheritdoc/>
    public bool Equals(CapFileTime other) => _request == other._request && _value == other._value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CapFileTime other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_request, _value);

    /// <summary>Whether two values ask for the same thing.</summary>
    public static bool operator ==(CapFileTime left, CapFileTime right) => left.Equals(right);

    /// <summary>Whether two values ask for different things.</summary>
    public static bool operator !=(CapFileTime left, CapFileTime right) => !left.Equals(right);

    /// <summary>Describes the request, for a log line or a debugger.</summary>
    public override string ToString() => _request switch
    {
        Request.Now => nameof(Now),
        Request.At => _value.ToString("O", CultureInfo.InvariantCulture),
        _ => nameof(Unchanged),
    };
}
