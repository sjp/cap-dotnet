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
/// One type covers every platform because every caller of it is platform-neutral resolution
/// code; the difference between a descriptor and a handle lives entirely in the
/// <see cref="Backend"/> that issued it, which is also what closes it.
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
internal sealed class SafeDirHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// Wraps an already-open descriptor or handle.
    /// </summary>
    /// <param name="handle">The raw descriptor or handle value.</param>
    /// <param name="backend">
    /// The implementation that issued it, and so the only one that knows what the value
    /// refers to.
    /// </param>
    /// <param name="access">The authority the open was granted.</param>
    /// <param name="ownsHandle">
    /// Whether disposal should close it. False only where something else has taken the value
    /// over and will close it itself, such as a directory stream built on the descriptor.
    /// </param>
    public SafeDirHandle(nint handle, IPlatformOps backend, CapAccess access, bool ownsHandle = true)
        : base(ownsHandle)
    {
        ArgumentNullException.ThrowIfNull(backend);

        Backend = backend;
        Access = access;
        SetHandle(handle);
    }

    /// <summary>
    /// The implementation that issued this handle, which every operation on it goes through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carried on the handle rather than chosen per process, so that two filesystems can be
    /// in use at once: a simulated tree in a test alongside the disk the test runner itself
    /// is writing to. Each handle reaches the filesystem it came from, whatever else the
    /// process is doing.
    /// </para>
    /// <para>
    /// The value is meaningless to any other implementation. To a simulated filesystem a
    /// real descriptor number is an index into its own table, and a simulated index handed
    /// to the kernel names whatever real object happens to hold that number. So an operation
    /// that involves two handles has to check that they share a backend before calling
    /// either one, and a handle produced from another — a copy, or a step of a walk — records
    /// the same backend as the one it came from.
    /// </para>
    /// </remarks>
    public IPlatformOps Backend { get; }

    /// <summary>
    /// Whether <paramref name="other"/> may be passed to this handle's backend in the same
    /// call, as the destination of a rename or a link.
    /// </summary>
    /// <remarks>
    /// True when both came from the same implementation, and also when both are the
    /// operating system's own objects, because every implementation over the host shares the
    /// kernel's table. Otherwise the answer is no, and the caller must refuse the operation
    /// before calling either backend. A rename between filesystems is refused by the kernel
    /// as a move across devices, and that is what a caller should report here too.
    /// </remarks>
    public bool SharesBackendWith(SafeDirHandle other) =>
        ReferenceEquals(Backend, other.Backend) ||
        (Backend.IssuesKernelHandles && other.Backend.IssuesKernelHandles);

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
    /// <remarks>
    /// Closes through the backend that issued the handle, because only that backend knows
    /// whether the value is a kernel object or an entry in a table of its own. That is one
    /// interface call on the finalizer thread for a handle nobody disposed; the host
    /// implementations answer it with the bare close and nothing else, so the finalizer does
    /// no more work than it did when the close was made here directly.
    /// </remarks>
    protected override bool ReleaseHandle() => Backend.CloseDirectory(handle);
}
