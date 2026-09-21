namespace Cap.Primitives.Interop;

/// <summary>
/// Which numbering scheme a <see cref="CapError.RawCode"/> belongs to.
/// </summary>
/// <remarks>
/// A Unix <c>errno</c> and a Windows <c>NTSTATUS</c> are both small integers and neither is
/// meaningful in the other's namespace: <c>2</c> is <c>ENOENT</c> under one and
/// <c>STATUS_WAIT_2</c> under the other. Carrying the scheme with the number means a code
/// can be printed, logged or compared without the reader having to know which platform
/// produced it.
/// </remarks>
internal enum CapErrorSource
{
    /// <summary>No failure, so no code.</summary>
    None = 0,

    /// <summary>A Unix <c>errno</c> value.</summary>
    Errno,

    /// <summary>A Windows <c>NTSTATUS</c> value, as returned by the native API.</summary>
    NtStatus,

    /// <summary>A Windows Win32 error code, from an API that reports through the last-error slot.</summary>
    Win32,
}
