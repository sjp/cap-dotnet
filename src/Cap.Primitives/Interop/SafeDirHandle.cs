using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Interop;

/// <summary>
/// An open directory: a Unix file descriptor or a Windows kernel handle, and the authority
/// to reach everything beneath it.
/// </summary>
/// <remarks>
/// <para>
/// This is the capability itself in its rawest form. Everything the containment guarantee
/// promises is expressed as "relative to one of these", so the lifetime rules are not
/// bookkeeping — they are security-relevant. The specific hazard is descriptor reuse: on
/// Unix a closed descriptor number is handed straight back out by the next open, so code
/// that keeps a bare <c>int</c> across a close can find itself issuing a confined operation
/// against a directory somewhere else entirely. Deriving from <see cref="SafeHandle"/> is
/// what makes that unrepresentable, and it is why the raw value is only ever reached
/// through <see cref="Lease"/>.
/// </para>
/// <para>
/// One type covers both platforms because every caller of it is platform-neutral resolution
/// code; the difference between a descriptor and a handle lives entirely in
/// <see cref="ReleaseHandle"/>.
/// </para>
/// <para>
/// A file handle is deliberately *not* wrapped by a type of our own. Files are handed to
/// callers as <see cref="SafeFileHandle"/> so that the framework's own random-access and
/// stream APIs accept them directly; a bespoke wrapper would buy nothing and would have to
/// be unwrapped at every boundary.
/// </para>
/// <para>
/// Inherited from <see cref="SafeHandleZeroOrMinusOneIsInvalid"/>: descriptor <c>0</c> is
/// treated as invalid. It is a legal descriptor number, but it is standard input, and a
/// directory open can only return it in a process that has closed standard input first.
/// Refusing it costs nothing real and removes the ambiguity from every validity check.
/// </para>
/// </remarks>
internal sealed partial class SafeDirHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// Wraps an already-open descriptor or handle.
    /// </summary>
    /// <param name="handle">The raw descriptor or handle value.</param>
    /// <param name="ownsHandle">
    /// Whether disposal should close it. A simulated filesystem passes
    /// <see langword="false"/>: its handle values are indices into its own tables and
    /// closing them through the OS would close whatever real object happened to share the
    /// number.
    /// </param>
    /// <param name="access">The authority the open was granted.</param>
    public SafeDirHandle(nint handle, bool ownsHandle, CapAccess access)
        : base(ownsHandle)
    {
        Access = access;
        SetHandle(handle);
    }

    /// <summary>
    /// The authority this handle was opened with.
    /// </summary>
    /// <remarks>
    /// Carried on the handle because there is at least one operation — producing a second,
    /// independent handle to the same directory — that has to reproduce it rather than
    /// choose it. Where that is done by re-opening the object, a copy made without knowing
    /// what the original carried would be granted whatever the directory's permissions
    /// allow, which for a handle deliberately opened with no read access is not a copy at
    /// all but a promotion. Recording it makes reproducing it possible; nothing consults it
    /// to decide whether an operation is permitted, which remains the operating system's
    /// judgement rather than ours.
    /// </remarks>
    public CapAccess Access { get; }

    /// <summary>
    /// Pins the handle open for the duration of a native call.
    /// </summary>
    /// <remarks>
    /// Read the raw value only through this. A concurrent <see cref="SafeHandle.Dispose()"/>
    /// between reading the value and making the syscall would otherwise let the number be
    /// recycled by an unrelated open, and the call would land on the wrong object.
    /// </remarks>
    public HandleLease Lease() => new(this);

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        if (OperatingSystem.IsWindows())
        {
            return CloseHandle(handle);
        }

        // close(2) can report EINTR, and on Linux the descriptor is closed regardless --
        // retrying would close whatever has since taken the number. So the result is read,
        // never acted on.
        return Close(checked((int)handle)) == 0;
    }

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
