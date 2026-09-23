using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// Turns a directory handle and a single name into an address a socket call will accept,
/// without letting that call resolve anything this library has not already confined.
/// </summary>
/// <remarks>
/// <para>
/// A socket in the Unix domain is named by a filesystem path, and the calls that use one
/// take a path rather than a descriptor: there is no <c>connectat</c> or <c>bindat</c> on
/// Linux, and the address a connection is made to is resolved by the kernel with the whole
/// process's authority, following symbolic links as it goes. Handing such a call a path that
/// this library assembled would put the containment decision back where the rest of the
/// library exists to take it from.
/// </para>
/// <para>
/// What closes that gap is the descriptor directory. Each entry in it stands for a
/// descriptor this process already holds, and resolving one lands on exactly the object that
/// descriptor refers to rather than on whatever the name it was opened under holds now. So
/// an address written in terms of a descriptor is not a path that can be raced: the only
/// lookup the kernel performs is the one this library has already decided is allowed.
/// </para>
/// <para>
/// The two directions need different things from that, and the difference is the whole of
/// why there are two members here.
/// </para>
/// <list type="bullet">
/// <item>
/// Reaching an existing socket opens the name itself, for position alone and without
/// following it, and names that descriptor. Nothing is resolved by the socket call at all,
/// so a name swapped after the open is not merely refused — it is not consulted. What the
/// open landed on is reported alongside, because an open of this form succeeds on an object
/// of any kind, a symbolic link included, and deciding what to do about that is the caller's.
/// </item>
/// <item>
/// Creating one cannot open what does not exist yet, so it names the descriptor of the
/// directory that is to hold it and leaves the kernel to create the single component
/// beneath. That lookup creates rather than follows: a name already taken, by a link or by
/// anything else, fails the call instead of being resolved through.
/// </item>
/// </list>
/// <para>
/// <strong>Linux only, and reported as unsupported elsewhere.</strong> The mechanism is a
/// property of that kernel's descriptor directory; macOS has no equivalent, and the Windows
/// Unix-domain implementation takes a path with no handle-relative form at all. The
/// alternative would be to resolve a path and hand it over, which is the technique this
/// exists to avoid — so where the mechanism is absent the operation is refused rather than
/// performed with a weaker guarantee.
/// </para>
/// </remarks>
internal static class UnixSocketNaming
{
    /// <summary>
    /// Where the kernel publishes an entry per descriptor the calling process holds.
    /// </summary>
    /// <remarks>
    /// Each entry behaves as a link to the object rather than to the name it was opened
    /// under, which is the property the whole mechanism rests on.
    /// </remarks>
    private const string DescriptorDirectory = "/proc/self/fd";

    /// <summary>
    /// How many bytes of address a socket in this domain can carry.
    /// </summary>
    /// <remarks>
    /// The kernel's address structure holds 108 bytes for the name, and the last of them has
    /// to be the terminator. It is a hard limit rather than a buffer size of ours, so a name
    /// that does not fit is refused rather than truncated: a truncated address names a
    /// different socket, and binding one would silently publish the wrong name.
    /// </remarks>
    private const int AddressLimit = 107;

    private static readonly bool DescriptorDirectoryPresent = ProbeDescriptorDirectory();

    /// <summary>
    /// Whether a socket in the Unix domain can be reached through a directory handle here.
    /// </summary>
    public static bool IsSupported => DescriptorDirectoryPresent;

    /// <summary>
    /// Names an existing socket held under <paramref name="holder"/>.
    /// </summary>
    /// <param name="holder">The directory a confined resolution has already arrived at.</param>
    /// <param name="name">A single component within it.</param>
    /// <remarks>
    /// <para>
    /// Opens the name for position alone, which matters because a socket cannot be opened
    /// for data at all, and without following it.
    /// </para>
    /// <para>
    /// <strong>An open of that form does not refuse a link; it opens the link.</strong> The
    /// two flags together are defined to produce a descriptor on the symbolic link itself
    /// rather than to fail, and an address written from such a descriptor would be resolved
    /// through the link by the connection attempt — which is the escape this whole mechanism
    /// exists to prevent. So what the descriptor landed on is read from the descriptor and
    /// reported, and the caller decides. Reading it from the descriptor rather than from the
    /// name is the part that cannot be raced: the answer is about the object the address
    /// already names.
    /// </para>
    /// </remarks>
    public static CapResult<UnixSocketName> ForConnect(SafeDirHandle holder, ReadOnlySpan<char> name)
    {
        if (!IsSupported)
        {
            return CapResult<UnixSocketName>.Fail(CapError.FromCategory(CapErrorCategory.NotSupported));
        }

        using HandleLease lease = holder.Lease();
        if (!lease.IsValid)
        {
            return CapResult<UnixSocketName>.Fail(HandleLease.ClosedError);
        }

        Span<byte> scratch = stackalloc byte[512];
        using UnixPathBuffer encoded = UnixPathBuffer.Create(name, scratch);
        if (!encoded.IsValid)
        {
            return CapResult<UnixSocketName>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        int flags = LinuxConstants.O_PATH | LinuxConstants.O_NOFOLLOW | LinuxConstants.O_CLOEXEC;

        int fd;
        int errno;
        unsafe
        {
            fixed (byte* path = encoded.Bytes)
            {
                fd = LinuxNative.OpenAt(lease.Descriptor, path, flags);
                errno = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
        }

        if (fd < 0)
        {
            return CapResult<UnixSocketName>.Fail(LinuxErrno.ToError(errno));
        }

        var anchor = new SafeFileHandle(fd, ownsHandle: true);
        CapError described = PlatformOps.Current.DescribeHandle(anchor, out CapNodeStat stat);
        if (described.IsFailure)
        {
            anchor.Dispose();
            return CapResult<UnixSocketName>.Fail(described);
        }

        return CapResult<UnixSocketName>.Ok(
            new UnixSocketName(anchor, Describe(fd, name: default), stat.Type));
    }

    /// <summary>
    /// Names a socket that is about to be created under <paramref name="holder"/>.
    /// </summary>
    /// <param name="holder">The directory a confined resolution has already arrived at.</param>
    /// <param name="name">The single component the socket is to take.</param>
    /// <remarks>
    /// <para>
    /// Takes a copy of the directory descriptor rather than borrowing the caller's, because
    /// the address stays meaningful only while the descriptor it names is open, and that has
    /// to outlast this call by as long as the address is in use. The copy carries the same
    /// authority as the handle it was made from; nothing here widens it.
    /// </para>
    /// <para>
    /// The name itself is left to the kernel to look up, which is safe in exactly this
    /// direction: creating a name refuses one that is already taken, so a link planted in
    /// the way fails the bind instead of redirecting it.
    /// </para>
    /// </remarks>
    public static CapResult<UnixSocketName> ForBind(SafeDirHandle holder, ReadOnlySpan<char> name)
    {
        if (!IsSupported)
        {
            return CapResult<UnixSocketName>.Fail(CapError.FromCategory(CapErrorCategory.NotSupported));
        }

        if (PathEncoding.GetByteCount(name) < 0)
        {
            return CapResult<UnixSocketName>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        CapResult<SafeDirHandle> copy = PlatformOps.Current.DuplicateDirectory(holder);
        if (!copy.IsSuccess)
        {
            return CapResult<UnixSocketName>.Fail(copy.Error);
        }

        SafeDirHandle anchor = copy.Value;
        using HandleLease lease = anchor.Lease();
        if (!lease.IsValid)
        {
            anchor.Dispose();
            return CapResult<UnixSocketName>.Fail(HandleLease.ClosedError);
        }

        string address = Describe(lease.Descriptor, name);
        if (PathEncoding.GetByteCount(address) > AddressLimit)
        {
            anchor.Dispose();
            return CapResult<UnixSocketName>.Fail(CapError.FromCategory(CapErrorCategory.NameTooLong));
        }

        return CapResult<UnixSocketName>.Ok(
            new UnixSocketName(anchor, address, CapFileType.Unknown));
    }

    /// <summary>
    /// Writes the address for a descriptor, and for a name beneath it when there is one.
    /// </summary>
    private static string Describe(int descriptor, ReadOnlySpan<char> name)
    {
        // Written in the invariant culture rather than the running one. The number is a
        // descriptor the kernel will parse back, not text for a person to read.
        string root = string.Concat(
            DescriptorDirectory, "/", descriptor.ToString(CultureInfo.InvariantCulture));

        return name.IsEmpty ? root : string.Concat(root, "/", name);
    }

    /// <summary>
    /// Asks once whether this kernel publishes the descriptor directory at all.
    /// </summary>
    /// <remarks>
    /// Opened rather than assumed, because it is absent from a process whose mount namespace
    /// does not carry the kernel's process filesystem — a container built without one, or a
    /// deliberately minimal root. Finding that out at the moment of a connection would report
    /// a missing socket, which is the wrong answer to a question nobody asked.
    /// </remarks>
    private static bool ProbeDescriptorDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        Span<byte> scratch = stackalloc byte[64];
        using UnixPathBuffer encoded = UnixPathBuffer.Create(DescriptorDirectory, scratch);
        if (!encoded.IsValid)
        {
            return false;
        }

        int flags = LinuxConstants.O_PATH | LinuxConstants.O_DIRECTORY | LinuxConstants.O_CLOEXEC;

        int fd;
        unsafe
        {
            fixed (byte* path = encoded.Bytes)
            {
                fd = LinuxNative.OpenAt(LinuxConstants.AT_FDCWD, path, flags);
            }
        }

        if (fd < 0)
        {
            return false;
        }

        using var opened = new SafeDirHandle(fd, ownsHandle: true, CapAccess.None);
        return true;
    }
}
