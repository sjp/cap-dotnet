using System.Diagnostics.CodeAnalysis;

namespace Cap.Primitives.Interop;

/// <summary>
/// The outcome of a platform filesystem operation: a portable category plus the exact code
/// the platform produced.
/// </summary>
/// <remarks>
/// <para>
/// Every member of <see cref="IPlatformOps"/> returns one of these instead of throwing.
/// That is not a style preference. Resolution treats several failures as ordinary control
/// flow — a component turning out to be a symbolic link, a confined open losing a race with
/// a concurrent rename — and those happen on the hot path of every walk. Building the loop
/// out of exceptions would make the common case the expensive case, and would push the
/// decision of which failures are fatal into <c>catch</c> filters far from the syscall that
/// produced them.
/// </para>
/// <para>
/// Translating to an exception happens once, at the boundary where a handle is handed back
/// to a caller. That is also the only place where a category may be deliberately blurred:
/// the containment guarantee treats the existence of a file outside the sandbox as something
/// not to reveal, so a public API may answer <see cref="CapErrorCategory.NotFound"/> where
/// the kernel said something more specific. This type never does that blurring itself — a
/// shim that lies to its own resolver cannot be reasoned about.
/// </para>
/// </remarks>
internal readonly struct CapError : IEquatable<CapError>
{
    private CapError(CapErrorCategory category, CapErrorSource source, int rawCode)
    {
        Category = category;
        Source = source;
        RawCode = rawCode;
    }

    /// <summary>Success. The only value for which <see cref="IsSuccess"/> is true.</summary>
    public static CapError Success => default;

    /// <summary>What this failure means, independent of platform.</summary>
    public CapErrorCategory Category { get; }

    /// <summary>Which numbering scheme <see cref="RawCode"/> belongs to.</summary>
    public CapErrorSource Source { get; }

    /// <summary>
    /// The unmodified platform code: an <c>errno</c>, an <c>NTSTATUS</c>, or a Win32 error.
    /// Kept so a backend that needs a distinction this type's categories do not draw can
    /// still make it, and so a bug report can quote the number the kernel actually returned.
    /// </summary>
    public int RawCode { get; }

    /// <summary>True when the operation succeeded.</summary>
    [MemberNotNullWhen(false, nameof(FailureDescription))]
    public bool IsSuccess => Category == CapErrorCategory.None;

    /// <summary>True when the operation failed.</summary>
    public bool IsFailure => Category != CapErrorCategory.None;

    /// <summary>
    /// A short description of the failure, or <see langword="null"/> on success. For
    /// diagnostics and assertion messages; nothing parses it.
    /// </summary>
    public string? FailureDescription => IsSuccess ? null : ToString();

    /// <summary>
    /// Builds a failure from a platform code and the reading a platform-specific table gave
    /// it.
    /// </summary>
    /// <remarks>
    /// Classification lives with the platform, not here. The obvious-looking alternative — a
    /// single errno table — is wrong: only the first thirty-odd values are common POSIX, and
    /// above those Linux and macOS disagree outright. <c>EAGAIN</c> is 11 on Linux and 35 on
    /// macOS, where 35 is <c>EDEADLK</c>; a shared table would read a deadlock report as the
    /// retry signal that confined resolution depends on.
    /// </remarks>
    public static CapError Create(CapErrorCategory category, CapErrorSource source, int rawCode) =>
        category == CapErrorCategory.None ? Success : new CapError(category, source, rawCode);

    /// <summary>
    /// A failure this layer produced itself rather than reading from the platform — a
    /// precondition the shim refuses before it reaches a syscall.
    /// </summary>
    public static CapError FromCategory(CapErrorCategory category) =>
        category == CapErrorCategory.None
            ? Success
            : new CapError(category, CapErrorSource.None, 0);

    /// <inheritdoc/>
    public bool Equals(CapError other) =>
        Category == other.Category && Source == other.Source && RawCode == other.RawCode;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CapError other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Category, Source, RawCode);

    /// <inheritdoc/>
    public override string ToString() => Source switch
    {
        CapErrorSource.None when IsSuccess => "success",
        CapErrorSource.None => Category.ToString(),
        CapErrorSource.Errno => $"{Category} (errno {RawCode})",
        CapErrorSource.NtStatus => $"{Category} (NTSTATUS 0x{RawCode:X8})",
        CapErrorSource.Win32 => $"{Category} (Win32 {RawCode})",
        _ => Category.ToString(),
    };

    public static bool operator ==(CapError left, CapError right) => left.Equals(right);

    public static bool operator !=(CapError left, CapError right) => !left.Equals(right);
}
