using System.Runtime.InteropServices;

namespace Cap.Primitives.Interop;

/// <summary>
/// Keeps a <see cref="SafeHandle"/> open across a native call and exposes its raw value.
/// </summary>
/// <remarks>
/// <para>
/// The problem this solves is specific and not hypothetical. A descriptor number freed by a
/// close is immediately reusable, and on Unix the kernel reuses the lowest free one, so a
/// thread that read a descriptor out of a handle and then lost the race with a
/// <c>Dispose</c> on another thread can make its syscall against a completely unrelated
/// object that has taken the number in the meantime. For a library whose entire guarantee is
/// "this operation is relative to that directory", that is a containment failure, not a
/// crash.
/// </para>
/// <para>
/// A lease that could not be taken reports <see cref="IsValid"/> as false rather than
/// throwing, because every caller is already returning a <see cref="CapError"/> and has a
/// natural place to put the failure.
/// </para>
/// </remarks>
internal readonly ref struct HandleLease
{
    private readonly SafeHandle? _handle;
    private readonly bool _acquired;

    /// <summary>Takes a lease on <paramref name="handle"/>.</summary>
    public HandleLease(SafeHandle? handle)
    {
        _handle = handle;
        if (handle is null || handle.IsInvalid || handle.IsClosed)
        {
            return;
        }

        bool acquired = false;
        handle.DangerousAddRef(ref acquired);
        _acquired = acquired;
    }

    /// <summary>True when the handle was open and is now pinned.</summary>
    public bool IsValid => _acquired;

    /// <summary>
    /// The raw descriptor or handle value. Meaningful only while <see cref="IsValid"/> is
    /// true and only until <see cref="Dispose"/>.
    /// </summary>
    public nint Raw => _acquired && _handle is not null ? _handle.DangerousGetHandle() : -1;

    /// <summary>The raw value as a Unix file descriptor.</summary>
    public int Descriptor => checked((int)Raw);

    /// <summary>Releases the lease.</summary>
    public void Dispose()
    {
        if (_acquired)
        {
            _handle?.DangerousRelease();
        }
    }
}
