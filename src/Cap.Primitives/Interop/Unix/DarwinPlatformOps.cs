using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// The macOS filesystem backend.
/// </summary>
/// <remarks>
/// <para>
/// macOS has no equivalent of the Linux confined open, so every resolution here is a walk:
/// one handle-relative step per component, each taken against the handle the previous step
/// produced. That keeps resolution inside the subtree — a step can only reach an entry of a
/// directory already held — but the sequence is not atomic, and the residual race that
/// leaves is a documented property of the platform rather than something this file can fix.
/// </para>
/// <para>
/// Two hazards are specific to this platform and are not handled here. Volumes are usually
/// case-insensitive, so containment must never rest on comparing names as strings; and the
/// system normalises the Unicode form of filenames, so a name can come back in a different
/// encoding from the one it went in as. Both are properties of the filesystem rather than of
/// these calls, and both belong to the resolution logic that sits above.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class DarwinPlatformOps : IPlatformOps
{
    private const int PathScratchBytes = 512;
    private const int InitialLinkBufferBytes = 256;
    private const int MaxLinkBufferBytes = 64 * 1024;

    /// <inheritdoc/>
    public PlatformCapabilities Capabilities => new(ResolutionBackend.PortableWalk);

    /// <inheritdoc/>
    /// <remarks>Always zero: there is no confined open on this platform to attempt.</remarks>
    public long ConfinedOpenAttempts => 0;

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> OpenAmbientDirectory(string path, CapAccess access)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!IsDirectoryAccess(access))
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        Span<byte> scratch = stackalloc byte[PathScratchBytes];
        using UnixPathBuffer encoded = UnixPathBuffer.Create(path, scratch);
        if (!encoded.IsValid)
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        // Links are followed here and nowhere else, because this is the ambient step that
        // happens before any capability exists. It matters more on this platform than on
        // Linux: the conventional temporary and variable-data directories are themselves
        // links into a private subtree, so refusing to follow one would refuse the most
        // ordinary root a caller could ask for.
        int flags = DarwinConstants.O_RDONLY | DarwinConstants.O_DIRECTORY | DarwinConstants.O_CLOEXEC;
        return OpenDirectoryDescriptor(DarwinConstants.AT_FDCWD, encoded, flags, noFollow: false, access);
    }

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> OpenChildDirectory(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        CapAccess access)
    {
        if (!IsDirectoryAccess(access))
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        using HandleLease lease = parent.Lease();
        if (!lease.IsValid)
        {
            return CapResult<SafeDirHandle>.Fail(CapError.Create(
                CapErrorCategory.Unknown, CapErrorSource.Errno, PosixErrno.EBADF));
        }

        Span<byte> scratch = stackalloc byte[PathScratchBytes];
        using UnixPathBuffer encoded = UnixPathBuffer.Create(name, scratch);
        if (!encoded.IsValid)
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        int flags = DarwinConstants.O_RDONLY | DarwinConstants.O_DIRECTORY |
                    DarwinConstants.O_NOFOLLOW | DarwinConstants.O_CLOEXEC;
        return OpenDirectoryDescriptor(lease.Descriptor, encoded, flags, noFollow: true, access);
    }

    /// <inheritdoc/>
    public CapResult<SafeFileHandle> OpenChildFile(SafeDirHandle parent, ReadOnlySpan<char> name, CapAccess access)
    {
        using HandleLease lease = parent.Lease();
        if (!lease.IsValid)
        {
            return CapResult<SafeFileHandle>.Fail(CapError.Create(
                CapErrorCategory.Unknown, CapErrorSource.Errno, PosixErrno.EBADF));
        }

        Span<byte> scratch = stackalloc byte[PathScratchBytes];
        using UnixPathBuffer encoded = UnixPathBuffer.Create(name, scratch);
        if (!encoded.IsValid)
        {
            return CapResult<SafeFileHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        int flags = AccessFlags(access) | DarwinConstants.O_NOFOLLOW | DarwinConstants.O_CLOEXEC |
                    DarwinConstants.O_NONBLOCK;

        int fd;
        int errno = 0;
        unsafe
        {
            fixed (byte* path = encoded.Bytes)
            {
                fd = DarwinNative.OpenAt(lease.Descriptor, path, flags);
                if (fd < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }

        if (fd < 0)
        {
            return CapResult<SafeFileHandle>.Fail(
                TranslateOpenFailure(lease.Descriptor, encoded.Bytes, errno, noFollow: true));
        }

        SafeFileHandle handle = new(fd, ownsHandle: true);
        ClearNonBlocking(fd);
        return CapResult<SafeFileHandle>.Ok(handle);
    }

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> OpenConfinedDirectory(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        CapAccess access,
        ConfinedResolveOptions options) =>
        CapResult<SafeDirHandle>.Fail(ConfinedOpenUnavailable);

    /// <inheritdoc/>
    public CapResult<SafeFileHandle> OpenConfinedFile(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        CapAccess access,
        ConfinedResolveOptions options) =>
        CapResult<SafeFileHandle>.Fail(ConfinedOpenUnavailable);

    /// <inheritdoc/>
    public CapResult<string> ReadChildLink(SafeDirHandle parent, ReadOnlySpan<char> name)
    {
        using HandleLease lease = parent.Lease();
        if (!lease.IsValid)
        {
            return CapResult<string>.Fail(CapError.Create(
                CapErrorCategory.Unknown, CapErrorSource.Errno, PosixErrno.EBADF));
        }

        Span<byte> scratch = stackalloc byte[PathScratchBytes];
        using UnixPathBuffer encoded = UnixPathBuffer.Create(name, scratch);
        if (!encoded.IsValid)
        {
            return CapResult<string>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        // A full buffer means "possibly truncated", and a truncated link target is a
        // different path rather than a shorter one, so the only safe reading of an exact fit
        // is to try again with more room.
        int capacity = InitialLinkBufferBytes;
        while (true)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(capacity);
            try
            {
                nint written;
                int errno = 0;
                unsafe
                {
                    fixed (byte* path = encoded.Bytes)
                    fixed (byte* target = buffer)
                    {
                        written = DarwinNative.ReadLinkAt(lease.Descriptor, path, target, (nuint)capacity);
                        if (written < 0)
                        {
                            errno = Marshal.GetLastPInvokeError();
                        }
                    }
                }

                if (written < 0)
                {
                    return CapResult<string>.Fail(DarwinErrno.ToError(errno));
                }

                if (written < capacity)
                {
                    return CapResult<string>.Ok(PathEncoding.GetString(buffer.AsSpan(0, (int)written)));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (capacity >= MaxLinkBufferBytes)
            {
                return CapResult<string>.Fail(CapError.Create(
                    CapErrorCategory.NameTooLong, CapErrorSource.Errno, DarwinErrno.ENAMETOOLONG));
            }

            capacity = Math.Min(capacity * 2, MaxLinkBufferBytes);
        }
    }

    /// <inheritdoc/>
    public CapError StatChild(SafeDirHandle parent, ReadOnlySpan<char> name, out CapNodeInfo info)
    {
        info = default;

        using HandleLease lease = parent.Lease();
        if (!lease.IsValid)
        {
            return CapError.Create(CapErrorCategory.Unknown, CapErrorSource.Errno, PosixErrno.EBADF);
        }

        Span<byte> scratch = stackalloc byte[PathScratchBytes];
        using UnixPathBuffer encoded = UnixPathBuffer.Create(name, scratch);
        if (!encoded.IsValid)
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        return StatChildInto(lease.Descriptor, encoded.Bytes, out info);
    }

    /// <inheritdoc/>
    public CapError StatHandle(SafeDirHandle handle, out CapNodeInfo info)
    {
        info = default;

        using HandleLease lease = handle.Lease();
        if (!lease.IsValid)
        {
            return CapError.Create(CapErrorCategory.Unknown, CapErrorSource.Errno, PosixErrno.EBADF);
        }

        // This platform has no way to ask about a descriptor through the name-relative call,
        // so the descriptor is asked about directly. The effect is the same and the
        // important property is kept: nothing here names the object a second time.
        DarwinStat stat = default;
        int result;
        int errno = 0;
        unsafe
        {
            result = DarwinNative.FStat(lease.Descriptor, &stat);
            if (result < 0)
            {
                errno = Marshal.GetLastPInvokeError();
            }
        }

        if (result < 0)
        {
            return DarwinErrno.ToError(errno);
        }

        info = new CapNodeInfo(stat.NodeType, stat.VolumeId, stat.Inode);
        return CapError.Success;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The platform answers this one directly from the descriptor, with no process
    /// filesystem in the way. The answer is still a snapshot and still only one of the names
    /// the object may have — a hard-linked file has several and this call names one of them
    /// — which is why it is good for a log line and for nothing else.
    /// </remarks>
    public CapResult<string> GetHandlePath(SafeDirHandle handle)
    {
        using HandleLease lease = handle.Lease();
        if (!lease.IsValid)
        {
            return CapResult<string>.Fail(CapError.Create(
                CapErrorCategory.Unknown, CapErrorSource.Errno, PosixErrno.EBADF));
        }

        // Exactly the size the platform demands. The call is not told how much room it has,
        // so anything smaller would be written past.
        Span<byte> buffer = stackalloc byte[DarwinConstants.MaxPathBytes];
        buffer.Clear();

        int result;
        int errno = 0;
        unsafe
        {
            fixed (byte* target = buffer)
            {
                result = DarwinNative.FcntlBuffer(
                    lease.Descriptor, DarwinConstants.F_GETPATH, target);
                if (result < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }

        if (result < 0)
        {
            return CapResult<string>.Fail(DarwinErrno.ToError(errno));
        }

        int length = buffer.IndexOf((byte)0);
        if (length <= 0)
        {
            // No terminator, or an empty answer. Either way the reply is not a path, and
            // inventing one from a buffer whose contents are unaccounted for would be worse
            // than saying so.
            return CapResult<string>.Fail(CapError.FromCategory(CapErrorCategory.Unknown));
        }

        return CapResult<string>.Ok(PathEncoding.GetString(buffer[..length]));
    }

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> DuplicateDirectory(SafeDirHandle handle)
    {
        using HandleLease lease = handle.Lease();
        if (!lease.IsValid)
        {
            return CapResult<SafeDirHandle>.Fail(CapError.Create(
                CapErrorCategory.Unknown, CapErrorSource.Errno, PosixErrno.EBADF));
        }

        // The duplicate is asked for with close-on-exec already set, rather than set
        // afterwards: a process started in the gap would inherit the directory, and with it
        // the authority the handle carries.
        int fd = DarwinNative.Fcntl(lease.Descriptor, DarwinConstants.F_DUPFD_CLOEXEC, 0);
        if (fd < 0)
        {
            return CapResult<SafeDirHandle>.Fail(DarwinErrno.ToError(Marshal.GetLastPInvokeError()));
        }

        return CapResult<SafeDirHandle>.Ok(new SafeDirHandle(fd, ownsHandle: true, handle.Access));
    }

    /// <inheritdoc/>
    public CapError CreateChildDirectory(SafeDirHandle parent, ReadOnlySpan<char> name)
    {
        using HandleLease lease = parent.Lease();
        if (!lease.IsValid)
        {
            return CapError.Create(CapErrorCategory.Unknown, CapErrorSource.Errno, PosixErrno.EBADF);
        }

        Span<byte> scratch = stackalloc byte[PathScratchBytes];
        using UnixPathBuffer encoded = UnixPathBuffer.Create(name, scratch);
        if (!encoded.IsValid)
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        int result;
        int errno = 0;
        unsafe
        {
            fixed (byte* path = encoded.Bytes)
            {
                result = DarwinNative.MkdirAt(
                    lease.Descriptor, path, DarwinConstants.DirectoryCreateMode);
                if (result < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }

        return result < 0 ? DarwinErrno.ToError(errno) : CapError.Success;
    }

    /// <inheritdoc/>
    public CapError RemoveChildFile(SafeDirHandle parent, ReadOnlySpan<char> name) =>
        Unlink(parent, name, flags: 0);

    /// <inheritdoc/>
    public CapError RemoveChildDirectory(SafeDirHandle parent, ReadOnlySpan<char> name) =>
        Unlink(parent, name, DarwinConstants.AT_REMOVEDIR);

    /// <inheritdoc/>
    public CapError RenameChild(
        SafeDirHandle fromParent,
        ReadOnlySpan<char> fromName,
        SafeDirHandle toParent,
        ReadOnlySpan<char> toName,
        bool replaceExisting)
    {
        using HandleLease fromLease = fromParent.Lease();
        using HandleLease toLease = toParent.Lease();
        if (!fromLease.IsValid || !toLease.IsValid)
        {
            return CapError.Create(CapErrorCategory.Unknown, CapErrorSource.Errno, PosixErrno.EBADF);
        }

        Span<byte> fromScratch = stackalloc byte[PathScratchBytes];
        Span<byte> toScratch = stackalloc byte[PathScratchBytes];
        using UnixPathBuffer from = UnixPathBuffer.Create(fromName, fromScratch);
        using UnixPathBuffer to = UnixPathBuffer.Create(toName, toScratch);
        if (!from.IsValid || !to.IsValid)
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        int result;
        int errno = 0;
        unsafe
        {
            fixed (byte* fromPath = from.Bytes)
            fixed (byte* toPath = to.Bytes)
            {
                result = replaceExisting
                    ? DarwinNative.RenameAt(fromLease.Descriptor, fromPath, toLease.Descriptor, toPath)
                    : DarwinNative.RenameAtX(
                        fromLease.Descriptor,
                        fromPath,
                        toLease.Descriptor,
                        toPath,
                        DarwinConstants.RENAME_EXCL);

                if (result < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }

        if (result >= 0)
        {
            return CapError.Success;
        }

        return replaceExisting ? DarwinErrno.ToError(errno) : TranslateNoReplaceFailure(errno);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The kind of the target is not recorded on this platform, so the request for a
    /// directory link and the request for a file link produce the same link.
    /// </remarks>
    public CapError CreateChildSymbolicLink(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        ReadOnlySpan<char> target,
        bool targetIsDirectory)
    {
        using HandleLease lease = parent.Lease();
        if (!lease.IsValid)
        {
            return CapError.Create(CapErrorCategory.Unknown, CapErrorSource.Errno, PosixErrno.EBADF);
        }

        Span<byte> nameScratch = stackalloc byte[PathScratchBytes];
        Span<byte> targetScratch = stackalloc byte[PathScratchBytes];
        using UnixPathBuffer link = UnixPathBuffer.Create(name, nameScratch);
        using UnixPathBuffer stored = UnixPathBuffer.Create(target, targetScratch);
        if (!link.IsValid || !stored.IsValid)
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        int result;
        int errno = 0;
        unsafe
        {
            fixed (byte* linkPath = link.Bytes)
            fixed (byte* targetPath = stored.Bytes)
            {
                result = DarwinNative.SymlinkAt(targetPath, lease.Descriptor, linkPath);
                if (result < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }

        return result < 0 ? DarwinErrno.ToError(errno) : CapError.Success;
    }

    /// <inheritdoc/>
    public CapError CreateChildHardLink(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        SafeDirHandle toParent,
        ReadOnlySpan<char> toName)
    {
        using HandleLease lease = parent.Lease();
        using HandleLease toLease = toParent.Lease();
        if (!lease.IsValid || !toLease.IsValid)
        {
            return CapError.Create(CapErrorCategory.Unknown, CapErrorSource.Errno, PosixErrno.EBADF);
        }

        Span<byte> fromScratch = stackalloc byte[PathScratchBytes];
        Span<byte> toScratch = stackalloc byte[PathScratchBytes];
        using UnixPathBuffer from = UnixPathBuffer.Create(name, fromScratch);
        using UnixPathBuffer to = UnixPathBuffer.Create(toName, toScratch);
        if (!from.IsValid || !to.IsValid)
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        int result;
        int errno = 0;
        unsafe
        {
            fixed (byte* fromPath = from.Bytes)
            fixed (byte* toPath = to.Bytes)
            {
                result = DarwinNative.LinkAt(
                    lease.Descriptor, fromPath, toLease.Descriptor, toPath, flags: 0);
                if (result < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }

        return result < 0 ? DarwinErrno.ToError(errno) : CapError.Success;
    }

    /// <summary>Removes one name beneath a directory descriptor.</summary>
    private static CapError Unlink(SafeDirHandle parent, ReadOnlySpan<char> name, int flags)
    {
        using HandleLease lease = parent.Lease();
        if (!lease.IsValid)
        {
            return CapError.Create(CapErrorCategory.Unknown, CapErrorSource.Errno, PosixErrno.EBADF);
        }

        Span<byte> scratch = stackalloc byte[PathScratchBytes];
        using UnixPathBuffer encoded = UnixPathBuffer.Create(name, scratch);
        if (!encoded.IsValid)
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        int result;
        int errno = 0;
        unsafe
        {
            fixed (byte* path = encoded.Bytes)
            {
                result = DarwinNative.UnlinkAt(lease.Descriptor, path, flags);
                if (result < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }

        return result < 0 ? DarwinErrno.ToError(errno) : CapError.Success;
    }

    /// <summary>
    /// Reads the failure of a rename that refused to replace its destination.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A filesystem that has not implemented the flag reports the operation unsupported, and
    /// a system predating the call reports it missing. Neither is answered by looking the
    /// destination up first and renaming if it was free: something appearing between those
    /// two would be destroyed by the call that was told not to destroy anything. So it is
    /// reported as unsupported, and a caller content to replace can ask for that and get an
    /// ordinary rename.
    /// </para>
    /// <para>
    /// An invalid-argument report is not among them. It is the code for a request that is
    /// wrong rather than unsupported — moving a directory inside itself, most often — and
    /// reading it as a platform limitation would tell a caller their filesystem is old when
    /// what is actually wrong is what they asked for.
    /// </para>
    /// </remarks>
    private static CapError TranslateNoReplaceFailure(int errno) =>
        errno is DarwinErrno.ENOSYS or DarwinErrno.ENOTSUP or DarwinErrno.EOPNOTSUPP
            ? CapError.Create(CapErrorCategory.NotSupported, CapErrorSource.Errno, errno)
            : DarwinErrno.ToError(errno);

    private static CapError ConfinedOpenUnavailable =>
        CapError.Create(CapErrorCategory.NotSupported, CapErrorSource.Errno, DarwinErrno.ENOSYS);

    /// <summary>
    /// Whether the authority asked of a directory is one a directory can carry.
    /// </summary>
    /// <remarks>
    /// This platform has no way to open a directory for traversal alone: every directory
    /// descriptor it can produce also permits the directory to be listed. So a request for
    /// the narrower handle is honoured by returning the wider one — narrower is what the
    /// caller would prefer, not what it depends on — and only the request no directory open
    /// can satisfy, to write one, is refused.
    ///
    /// The difference is visible in one place. A directory that grants execute permission
    /// without read permission can be walked through on a kernel offering a traversal-only
    /// open and cannot be opened at all here, so a tree using that permission pattern is
    /// reachable on one platform and not on this one. That is a property of the platform
    /// rather than a choice made here, and it is recorded so that it is not mistaken for a
    /// resolution bug.
    /// </remarks>
    private static bool IsDirectoryAccess(CapAccess access) =>
        access is CapAccess.None or CapAccess.Read;

    private static int AccessFlags(CapAccess access) => access switch
    {
        CapAccess.ReadWrite => DarwinConstants.O_RDWR,
        CapAccess.Write => DarwinConstants.O_WRONLY,
        _ => DarwinConstants.O_RDONLY,
    };

    /// <summary>
    /// Removes the non-blocking flag the open was issued with, so that the handle handed
    /// back behaves like any other. The flag is set on the open only so that naming a FIFO
    /// inside the sandbox cannot stop the thread that resolved it.
    /// </summary>
    private static void ClearNonBlocking(int fd)
    {
        int current = DarwinNative.Fcntl(fd, DarwinConstants.F_GETFL, 0);
        if (current >= 0 && (current & DarwinConstants.O_NONBLOCK) != 0)
        {
            _ = DarwinNative.Fcntl(fd, DarwinConstants.F_SETFL, current & ~DarwinConstants.O_NONBLOCK);
        }
    }

    private static CapResult<SafeDirHandle> OpenDirectoryDescriptor(
        int directoryFd,
        in UnixPathBuffer encoded,
        int flags,
        bool noFollow,
        CapAccess access)
    {
        int fd;
        int errno = 0;
        unsafe
        {
            fixed (byte* path = encoded.Bytes)
            {
                fd = DarwinNative.OpenAt(directoryFd, path, flags);
                if (fd < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }

        return fd < 0
            ? CapResult<SafeDirHandle>.Fail(
                TranslateOpenFailure(directoryFd, encoded.Bytes, errno, noFollow))
            : CapResult<SafeDirHandle>.Ok(new SafeDirHandle(fd, ownsHandle: true, access));
    }

    /// <summary>
    /// Reads the failure of a single-component open that refused to follow links.
    /// </summary>
    /// <param name="directoryFd">The directory the name was looked up in.</param>
    /// <param name="name">The encoded name that failed to open.</param>
    /// <param name="errno">The code the open failed with.</param>
    /// <param name="noFollow">
    /// True when the open refused to follow links, so that a link is an ordinary step in a
    /// walk rather than an error.
    /// </param>
    /// <remarks>
    /// Two codes are ambiguous and the walk reacts to them in opposite ways. A directory open
    /// that refuses links reports "not a directory" for a symbolic link exactly as it does
    /// for a plain file, and the link-loop code covers both a link the open declined to
    /// follow and a chain too long to follow. So the name is asked about again, without
    /// following it, on the failure path only. The answer is a hint about what to try next,
    /// never a decision about what may be reached: a walk told "this is a link" goes on to
    /// read the link, and finds out there if it is no longer one.
    /// </remarks>
    private static CapError TranslateOpenFailure(
        int directoryFd,
        ReadOnlySpan<byte> name,
        int errno,
        bool noFollow)
    {
        if (noFollow && errno is DarwinErrno.ELOOP or PosixErrno.ENOTDIR &&
            StatChildInto(directoryFd, name, out CapNodeInfo info).IsSuccess &&
            info.Type == CapNodeType.SymbolicLink)
        {
            return CapError.Create(CapErrorCategory.SymbolicLink, CapErrorSource.Errno, errno);
        }

        return DarwinErrno.ToError(errno);
    }

    /// <summary>Describes a name relative to a directory descriptor, without following it.</summary>
    private static CapError StatChildInto(int directoryFd, ReadOnlySpan<byte> name, out CapNodeInfo info)
    {
        info = default;

        DarwinStat stat = default;
        int result;
        int errno = 0;
        unsafe
        {
            fixed (byte* path = name)
            {
                result = DarwinNative.FStatAt(
                    directoryFd, path, &stat, DarwinConstants.AT_SYMLINK_NOFOLLOW);
                if (result < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }

        if (result < 0)
        {
            return DarwinErrno.ToError(errno);
        }

        info = new CapNodeInfo(stat.NodeType, stat.VolumeId, stat.Inode);
        return CapError.Success;
    }
}
