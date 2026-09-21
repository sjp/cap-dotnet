using System.Diagnostics.CodeAnalysis;

namespace Cap.Primitives.Interop;

/// <summary>
/// Either a value the platform produced or the reason it did not.
/// </summary>
/// <typeparam name="T">
/// The value type, constrained to a reference type so that "no value" is expressible
/// without a second flag and the compiler can enforce that a failed result is never read.
/// </typeparam>
/// <remarks>
/// The constraint is what makes this worth having over an <c>out</c> parameter: a handle
/// returned through <c>out</c> has to be given some value on the failure path, and the
/// obvious choices — a null that the compiler cannot see is conditional, or a freshly
/// allocated invalid handle — are respectively unsafe and wasteful. Here, checking
/// <see cref="IsSuccess"/> is what makes <see cref="Value"/> readable at all.
/// </remarks>
internal readonly struct CapResult<T>
    where T : class
{
    private CapResult(T? value, CapError error)
    {
        Value = value;
        Error = error;
    }

    /// <summary>The value, valid only when <see cref="IsSuccess"/> is true.</summary>
    public T? Value { get; }

    /// <summary>Why the operation failed, or <see cref="CapError.Success"/>.</summary>
    public CapError Error { get; }

    /// <summary>True when <see cref="Value"/> holds a result.</summary>
    [MemberNotNullWhen(true, nameof(Value))]
    public bool IsSuccess => Error.IsSuccess && Value is not null;

    /// <summary>Wraps a successful value.</summary>
    public static CapResult<T> Ok(T value) => new(value, CapError.Success);

    /// <summary>Wraps a failure.</summary>
    public static CapResult<T> Fail(CapError error) =>
        new(null, error.IsFailure ? error : CapError.FromCategory(CapErrorCategory.Unknown));
}
