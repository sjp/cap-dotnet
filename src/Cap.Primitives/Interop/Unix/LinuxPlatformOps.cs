using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Interop.Unix;

/// <summary>
/// The Linux filesystem backend.
/// </summary>
/// <remarks>
/// <para>
/// Linux is the one platform where confined resolution can be a single kernel operation,
/// through <c>openat2</c> with the flag that pins resolution below a directory. That is a
/// materially stronger guarantee than the walk every other platform uses, because it leaves
/// no window between components for anything to be swapped into. Whether it is available is
/// settled once, at first use, and never asked again.
/// </para>
/// <para>
/// "Available" has three answers, not two. The syscall may be absent on a kernel older than
/// 5.6; it may be present but blocked by a sandbox filter, which several container runtimes
/// did for a long stretch after it was introduced; or it may appear absent because this
/// library described its argument structure wrongly. The first two are facts about the host
/// and the fallback is the correct response. The third is a bug here, and the probe records
/// the kernel's exact complaint so a test can tell the cases apart — an invalid-argument
/// answer means the structure, not the kernel.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class LinuxPlatformOps : IPlatformOps
{
    /// <summary>Scratch space for a single component or a short path, before renting.</summary>
    private const int PathScratchBytes = 512;

    /// <summary>Initial buffer for a link target. Grown, not truncated, when it does not fit.</summary>
    private const int InitialLinkBufferBytes = 256;

    /// <summary>An absolute ceiling on a link target, well above any filesystem's own limit.</summary>
    private const int MaxLinkBufferBytes = 64 * 1024;

    private readonly Openat2Probe _probe = Openat2Probe.Run();
    private long _confinedOpenAttempts;
    private long _confinedOpenRaceRetries;
    private long _componentOpens;

    /// <inheritdoc/>
    public PlatformCapabilities Capabilities =>
        new(
            _probe.Supported ? ResolutionBackend.ConfinedOpen : ResolutionBackend.PortableWalk,
            overlappedFileHandles: false);

    /// <inheritdoc/>
    public long ConfinedOpenAttempts => Interlocked.Read(ref _confinedOpenAttempts);

    /// <summary>
    /// The <c>errno</c> the capability probe saw, or zero if the confined open is available.
    /// </summary>
    /// <remarks>
    /// Exposed so that a test can insist on the difference between "this kernel does not
    /// have the syscall" and "this kernel rejected our arguments". Only the first is a
    /// legitimate reason to be running the fallback.
    /// </remarks>
    public int ConfinedOpenProbeErrno => _probe.Errno;

    /// <summary>Why the confined open is unavailable, in words, or <see langword="null"/>.</summary>
    public string? ConfinedOpenUnavailableReason => _probe.Reason;

    /// <summary>
    /// How many single-name opens have been issued beneath an existing handle.
    /// </summary>
    /// <remarks>
    /// The counterpart of the confined-open counter, and it exists for the mirror-image
    /// reason. That one lets a run prove it never reached the fast path; this one lets a run
    /// prove the fast path did not quietly become a walk. A confined resolution is supposed
    /// to be one operation no matter how many names the path has, and the only way to tell
    /// that from outside — without a tracer, which not every host will permit — is to count
    /// the opens that a walk would have had to make and find none.
    ///
    /// Opening the first handle by an ordinary path is not counted: it resolves a whole path
    /// with the process's own authority and is not a step in anything.
    /// </remarks>
    public long ComponentOpens => Interlocked.Read(ref _componentOpens);

    /// <summary>
    /// How many times a confined open has been retried after the kernel reported that
    /// resolution lost a race.
    /// </summary>
    /// <remarks>
    /// The retry is invisible from the outside by design — a caller sees a successful open,
    /// not the race that preceded it — and something invisible is something no test can
    /// insist happened. This is how a test that provokes the race can tell the difference
    /// between having exercised the retry and merely having run the loop once.
    /// </remarks>
    public long ConfinedOpenRaceRetries => Interlocked.Read(ref _confinedOpenRaceRetries);

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> OpenAmbientDirectory(string path, CapAccess access)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!TryDirectoryAccessFlag(access, out int accessFlag))
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        Span<byte> scratch = stackalloc byte[PathScratchBytes];
        using UnixPathBuffer encoded = UnixPathBuffer.Create(path, scratch);
        if (!encoded.IsValid)
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        // Symbolic links *are* followed here, unlike everywhere else in this type. This is
        // the ambient step, before any capability exists: the caller named a directory by an
        // ordinary path and expects ordinary resolution, and several conventional locations
        // -- the temporary directory among them -- are links on some systems.
        int flags = accessFlag | LinuxConstants.O_DIRECTORY | LinuxConstants.O_CLOEXEC;
        return OpenDirectoryDescriptor(LinuxConstants.AT_FDCWD, encoded, flags, noFollow: false, access);
    }

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> OpenChildDirectory(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        CapAccess access)
    {
        if (!TryDirectoryAccessFlag(access, out int accessFlag))
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

        Interlocked.Increment(ref _componentOpens);

        // O_DIRECTORY is what keeps the traversal-only open honest about links. Without it,
        // a no-follow open of that kind succeeds on a symbolic link and hands back a
        // descriptor to the link itself, which a walk would then treat as the directory it
        // asked for. With it, the same case is refused.
        int flags = accessFlag | LinuxConstants.O_DIRECTORY |
                    LinuxConstants.O_NOFOLLOW | LinuxConstants.O_CLOEXEC;
        return OpenDirectoryDescriptor(lease.Descriptor, encoded, flags, noFollow: true, access);
    }

    /// <inheritdoc/>
    public CapResult<SafeFileHandle> OpenChildFile(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        in FileOpenRequest request)
    {
        if (!TryFileFlags(in request, out int flags, out CapError unsupported))
        {
            return CapResult<SafeFileHandle>.Fail(unsupported);
        }

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

        Interlocked.Increment(ref _componentOpens);

        flags |= LinuxConstants.O_NOFOLLOW | LinuxConstants.O_CLOEXEC | LinuxConstants.O_NONBLOCK;

        int fd;
        int errno;
        unsafe
        {
            fixed (byte* path = encoded.Bytes)
            {
                fd = request.Creates
                    ? LinuxNative.OpenAtWithMode(lease.Descriptor, path, flags, LinuxConstants.FileCreateMode)
                    : LinuxNative.OpenAt(lease.Descriptor, path, flags);
                errno = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
        }

        if (fd < 0)
        {
            return CapResult<SafeFileHandle>.Fail(
                TranslateOpenFailure(lease.Descriptor, encoded.Bytes, errno, noFollow: true));
        }

        return FinishFileOpen(fd, in request);
    }

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> OpenConfinedDirectory(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        CapAccess access,
        ConfinedResolveOptions options)
    {
        if (!TryDirectoryAccessFlag(access, out int accessFlag))
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        int flags = accessFlag | LinuxConstants.O_DIRECTORY | LinuxConstants.O_CLOEXEC;
        CapError error = OpenConfinedDescriptor(root, path, flags, mode: 0, options, out int fd);
        return error.IsFailure
            ? CapResult<SafeDirHandle>.Fail(error)
            : CapResult<SafeDirHandle>.Ok(new SafeDirHandle(fd, ownsHandle: true, access));
    }

    /// <inheritdoc/>
    public CapResult<SafeFileHandle> OpenConfinedFile(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        in FileOpenRequest request,
        ConfinedResolveOptions options)
    {
        if (!TryFileFlags(in request, out int flags, out CapError unsupported))
        {
            return CapResult<SafeFileHandle>.Fail(unsupported);
        }

        flags |= LinuxConstants.O_CLOEXEC | LinuxConstants.O_NONBLOCK;

        // The creation mode is read by the kernel only when the flags ask for creation, and
        // passing a non-zero one when they do not is rejected outright rather than ignored.
        uint mode = request.Creates ? LinuxConstants.FileCreateMode : 0;

        CapError error = OpenConfinedDescriptor(root, path, flags, mode, options, out int fd);
        if (error.IsFailure)
        {
            return CapResult<SafeFileHandle>.Fail(error);
        }

        return FinishFileOpen(fd, in request);
    }

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

        return ReadLinkText(lease.Descriptor, encoded.Bytes);
    }

    /// <summary>
    /// Reads the link at <paramref name="path"/> relative to <paramref name="directoryFd"/>,
    /// growing the buffer until the target fits.
    /// </summary>
    /// <remarks>
    /// The growth is not an optimisation detail. <c>readlinkat</c> truncates rather than
    /// reporting that the target did not fit, so a completely full buffer is
    /// indistinguishable from an exact fit and the only safe reading is "try again with more
    /// room". A truncated target is not a shorter target; it is a different path, and acting
    /// on one would be acting on a name nobody wrote.
    /// </remarks>
    private static CapResult<string> ReadLinkText(int directoryFd, ReadOnlySpan<byte> path)
    {
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
                    fixed (byte* name = path)
                    fixed (byte* target = buffer)
                    {
                        written = LinuxNative.ReadLinkAt(directoryFd, name, target, (nuint)capacity);
                        if (written < 0)
                        {
                            errno = Marshal.GetLastPInvokeError();
                        }
                    }
                }

                if (written < 0)
                {
                    return CapResult<string>.Fail(LinuxErrno.ToError(errno));
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
                    CapErrorCategory.NameTooLong, CapErrorSource.Errno, LinuxErrno.ENAMETOOLONG));
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

        // AT_NO_AUTOMOUNT so that merely looking at a name cannot cause a filesystem to be
        // mounted. Triggering a mount is an effect on the host, and asking what something is
        // should not have effects.
        int flags = LinuxConstants.AT_SYMLINK_NOFOLLOW | LinuxConstants.AT_NO_AUTOMOUNT;
        return StatInto(lease.Descriptor, encoded.Bytes, flags, out info);
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

        // An empty name with AT_EMPTY_PATH asks about the descriptor itself, which is the
        // only way to do it that does not involve naming the object again -- and naming it
        // again would ask about whatever holds that name now, not about what was opened.
        ReadOnlySpan<byte> empty = [0];
        return StatInto(lease.Descriptor, empty, LinuxConstants.AT_EMPTY_PATH, out info);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Answered by asking the process filesystem what the descriptor points at. That is the
    /// only mechanism this platform offers, and it is exactly as approximate as the contract
    /// says: the reply is the path the object is reachable by at that moment, one of
    /// possibly several, suffixed by the kernel with a note of its own when the object has
    /// been unlinked. Where the process filesystem is not mounted — a minimal container,
    /// most often — there is no answer at all and the call fails rather than guessing.
    /// </remarks>
    public CapResult<string> GetHandlePath(SafeDirHandle handle)
    {
        using HandleLease lease = handle.Lease();
        if (!lease.IsValid)
        {
            return CapResult<string>.Fail(CapError.Create(
                CapErrorCategory.Unknown, CapErrorSource.Errno, PosixErrno.EBADF));
        }

        // Built on the stack rather than interpolated, so that asking a handle where it is
        // does not allocate anything but the answer.
        const string Prefix = "/proc/self/fd/";
        Span<char> name = stackalloc char[Prefix.Length + 16];
        Prefix.CopyTo(name);
        if (!lease.Descriptor.TryFormat(
                name[Prefix.Length..], out int digits, provider: CultureInfo.InvariantCulture))
        {
            return CapResult<string>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        Span<byte> scratch = stackalloc byte[PathScratchBytes];
        using UnixPathBuffer encoded = UnixPathBuffer.Create(name[..(Prefix.Length + digits)], scratch);
        if (!encoded.IsValid)
        {
            return CapResult<string>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        return ReadLinkText(LinuxConstants.AT_FDCWD, encoded.Bytes);
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

        // Not dup(2): the copy it makes does not have the close-on-exec flag, so a process
        // started between the duplication and a later attempt to set it would inherit the
        // directory -- and with it the authority the handle carries. F_DUPFD_CLOEXEC does
        // both in one step and leaves no such window.
        //
        // A duplicate shares the original's access mode rather than being opened afresh, so
        // a copy of a traversal-only handle is traversal-only too. Nothing here can widen
        // what it was handed.
        int fd = LinuxNative.Fcntl(lease.Descriptor, LinuxConstants.F_DUPFD_CLOEXEC, 0);
        if (fd < 0)
        {
            return CapResult<SafeDirHandle>.Fail(LinuxErrno.ToError(Marshal.GetLastPInvokeError()));
        }

        return CapResult<SafeDirHandle>.Ok(new SafeDirHandle(fd, ownsHandle: true, handle.Access));
    }

    /// <inheritdoc/>
    public CapResult<SafeFileHandle> DuplicateFile(SafeFileHandle handle)
    {
        using HandleLease lease = new(handle);
        if (!lease.IsValid)
        {
            return CapResult<SafeFileHandle>.Fail(CapError.Create(
                CapErrorCategory.Unknown, CapErrorSource.Errno, PosixErrno.EBADF));
        }

        // The same reasoning as for a directory copy: the plain duplication call leaves the
        // copy without the close-on-exec flag, so a process started in the gap before the
        // flag could be set would inherit the file and the authority to read or write it.
        // One call that does both leaves no such gap.
        //
        // The copy shares the original's open description, so it shares its access mode, its
        // append behaviour and its position. That is what makes it a copy of this handle
        // rather than a second opinion about the name it came from.
        int fd = LinuxNative.Fcntl(lease.Descriptor, LinuxConstants.F_DUPFD_CLOEXEC, 0);
        if (fd < 0)
        {
            return CapResult<SafeFileHandle>.Fail(LinuxErrno.ToError(Marshal.GetLastPInvokeError()));
        }

        return CapResult<SafeFileHandle>.Ok(new SafeFileHandle(fd, ownsHandle: true));
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
                result = LinuxNative.MkdirAt(
                    lease.Descriptor, path, LinuxConstants.DirectoryCreateMode);
                if (result < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }

        return result < 0 ? LinuxErrno.ToError(errno) : CapError.Success;
    }

    /// <inheritdoc/>
    public CapError RemoveChildFile(SafeDirHandle parent, ReadOnlySpan<char> name) =>
        Unlink(parent, name, flags: 0);

    /// <inheritdoc/>
    public CapError RemoveChildDirectory(SafeDirHandle parent, ReadOnlySpan<char> name) =>
        Unlink(parent, name, LinuxConstants.AT_REMOVEDIR);

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

        long result;
        int errno = 0;
        unsafe
        {
            fixed (byte* fromPath = from.Bytes)
            fixed (byte* toPath = to.Bytes)
            {
                result = replaceExisting
                    ? LinuxNative.RenameAt(fromLease.Descriptor, fromPath, toLease.Descriptor, toPath)
                    : LinuxNative.RenameAt2(
                        LinuxConstants.SYS_renameat2,
                        fromLease.Descriptor,
                        fromPath,
                        toLease.Descriptor,
                        toPath,
                        LinuxConstants.RENAME_NOREPLACE);

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

        return replaceExisting ? LinuxErrno.ToError(errno) : TranslateNoReplaceFailure(errno);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The kind of the target is not recorded on this platform, so it is not asked for:
    /// a link is a stored string and what it turns out to name is decided when something
    /// follows it.
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
                result = LinuxNative.SymlinkAt(targetPath, lease.Descriptor, linkPath);
                if (result < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }

        return result < 0 ? LinuxErrno.ToError(errno) : CapError.Success;
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
                // No flags, so the existing name is taken as written. The flag that would
                // follow a symbolic link is deliberately absent: a second name for a link
                // is a second name for the link.
                result = LinuxNative.LinkAt(
                    lease.Descriptor, fromPath, toLease.Descriptor, toPath, flags: 0);
                if (result < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }

        return result < 0 ? LinuxErrno.ToError(errno) : CapError.Success;
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
                result = LinuxNative.UnlinkAt(lease.Descriptor, path, flags);
                if (result < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }

        return result < 0 ? LinuxErrno.ToError(errno) : CapError.Success;
    }

    /// <summary>
    /// Reads the failure of a rename that refused to replace its destination.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two codes mean this kernel, or this filesystem, cannot make the refusal part of the
    /// rename: an older kernel has no such call at all, and a filesystem that has not
    /// implemented the flag says the operation is unsupported. Neither is answered by
    /// looking the destination up first and renaming if it was absent — between those two
    /// the destination can appear, and the rename would then destroy exactly what the caller
    /// asked not to destroy. So it is reported as unsupported, and a caller content to
    /// replace can say so and get an ordinary rename.
    /// </para>
    /// <para>
    /// An invalid-argument report is deliberately not among them, although a handful of
    /// filesystems have historically used it for an unimplemented flag. It is the code for a
    /// request that is wrong rather than unsupported — moving a directory inside itself,
    /// most often — and reading every one of those as a platform limitation would tell a
    /// caller their filesystem is old when what is actually wrong is what they asked for.
    /// </para>
    /// </remarks>
    private static CapError TranslateNoReplaceFailure(int errno) =>
        errno is LinuxErrno.ENOSYS or LinuxErrno.EOPNOTSUPP
            ? CapError.Create(CapErrorCategory.NotSupported, CapErrorSource.Errno, errno)
            : LinuxErrno.ToError(errno);

    private static int AccessFlags(CapAccess access) => access switch
    {
        CapAccess.ReadWrite => LinuxConstants.O_RDWR,
        CapAccess.Write => LinuxConstants.O_WRONLY,
        _ => LinuxConstants.O_RDONLY,
    };

    /// <summary>
    /// Turns a file open request into the flags that express it, or reports that this
    /// platform cannot express it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two of the framework's options have no counterpart a handle can carry here, and both
    /// are refused rather than dropped. Removing a file when its last handle closes is done
    /// elsewhere by the filesystem, against the object; the nearest thing available here is
    /// to unlink a name afterwards, which removes whatever holds that name at the time and
    /// not necessarily the file that was opened. Encrypting a file at rest is a property of
    /// a filesystem that does not exist on this platform. Quietly ignoring either would
    /// leave a caller who asked carefully believing they had got what they asked for.
    /// </para>
    /// <para>
    /// The two access hints are a different case and are dropped rather than refused. They
    /// say how the file is expected to be read, the kernel is free to ignore them anywhere,
    /// and a handle that ignores them behaves exactly as the caller asked — only more
    /// slowly, perhaps. A hint is the one kind of request it is honest to drop.
    /// </para>
    /// </remarks>
    private static bool TryFileFlags(in FileOpenRequest request, out int flags, out CapError error)
    {
        error = CapError.Success;

        const FileOptions Unsupported = FileOptions.DeleteOnClose | FileOptions.Encrypted;
        if ((request.Options & Unsupported) != 0)
        {
            flags = 0;
            error = CapError.FromCategory(CapErrorCategory.NotSupported);
            return false;
        }

        flags = request.Access switch
        {
            FileAccess.ReadWrite => LinuxConstants.O_RDWR,
            FileAccess.Write => LinuxConstants.O_WRONLY,
            _ => LinuxConstants.O_RDONLY,
        };

        switch (request.Mode)
        {
            case FileMode.CreateNew:
                flags |= LinuxConstants.O_CREAT | LinuxConstants.O_EXCL;
                break;
            case FileMode.Create:
                flags |= LinuxConstants.O_CREAT | LinuxConstants.O_TRUNC;
                break;
            case FileMode.OpenOrCreate:
                flags |= LinuxConstants.O_CREAT;
                break;
            case FileMode.Truncate:
                flags |= LinuxConstants.O_TRUNC;
                break;
            case FileMode.Append:
                flags |= LinuxConstants.O_CREAT | LinuxConstants.O_APPEND;
                break;
            default:
                break;
        }

        if ((request.Options & FileOptions.WriteThrough) != 0)
        {
            flags |= LinuxConstants.O_SYNC;
        }

        return true;
    }

    /// <summary>
    /// Turns a freshly opened descriptor into a handle, once the open's after-effects have
    /// been applied.
    /// </summary>
    /// <remarks>
    /// Asynchrony is deliberately absent. A descriptor here has no overlapped mode to be put
    /// into: every file read on this platform completes when the kernel says it does, and
    /// what the framework calls an asynchronous file handle is a promise about which thread
    /// waits, not about the descriptor. So the request's asynchrony is carried by the layer
    /// that hands the handle to a caller and changes nothing about the open itself.
    /// </remarks>
    private static CapResult<SafeFileHandle> FinishFileOpen(int fd, in FileOpenRequest request)
    {
        ClearNonBlocking(fd);

        CapError kind = RefuseIfDirectory(fd);
        CapError reserved = kind.IsFailure ? kind : Preallocate(fd, in request);
        if (reserved.IsFailure)
        {
            new SafeFileHandle(fd, ownsHandle: true).Dispose();
            return CapResult<SafeFileHandle>.Fail(reserved);
        }

        return CapResult<SafeFileHandle>.Ok(new SafeFileHandle(fd, ownsHandle: true));
    }

    /// <summary>
    /// Refuses a descriptor that turned out to refer to a directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Necessary because a read-only open of a directory succeeds on this platform. Nothing
    /// can then be read through the descriptor, but the failure arrives at the first read
    /// rather than at the open, and in between the caller is holding something that says it
    /// is a file. That is the one confusion this pair of types must not permit: a directory
    /// handle and a file handle carry different authority and are not interchangeable, and
    /// a caller who asked for a file and was given a directory has been told something
    /// untrue.
    /// </para>
    /// <para>
    /// Asked of the descriptor rather than of the name. The name may already refer to
    /// something else; the descriptor is what was opened, and it is what the answer has to be
    /// about.
    /// </para>
    /// </remarks>
    private static CapError RefuseIfDirectory(int fd)
    {
        ReadOnlySpan<byte> empty = [0];
        CapError error = StatInto(fd, empty, LinuxConstants.AT_EMPTY_PATH, out CapNodeInfo info);

        if (error.IsFailure)
        {
            return error;
        }

        return info.Type == CapNodeType.Directory
            ? CapError.FromCategory(CapErrorCategory.IsADirectory)
            : CapError.Success;
    }

    /// <summary>
    /// Reserves the space the request asked for, on a file the open brought into existence
    /// or emptied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only for an open that starts the file from nothing. Reserving space on a file that
    /// was opened as it stood would extend somebody else's data, which is not what the
    /// request means, and there is no honest way to tell from here whether a name that could
    /// have been created was.
    /// </para>
    /// <para>
    /// The file's length is left alone. The reservation claims room behind the end of the
    /// file rather than filling it, so a caller who asked for room in advance gets an empty
    /// file that a later write cannot run out of space in — which is what the same request
    /// produces on every other platform, and what the framework's own file open produces
    /// here.
    /// </para>
    /// <para>
    /// A refusal for want of room fails the open, because a caller who asked for the space
    /// in advance asked precisely so that a later write would not fail for that reason.
    /// Every other complaint is ignored: filesystems that cannot reserve space report a
    /// variety of things, and the file is perfectly usable without the reservation.
    /// </para>
    /// <para>
    /// <strong>A file the open created is left behind when this fails.</strong> Removing it
    /// would mean removing a name, and by then the name may hold something else — so the
    /// alternative to leaving debris is deleting a stranger's file. The handle is closed and
    /// the failure reported; clearing up is the caller's, who knows what they asked for.
    /// </para>
    /// </remarks>
    private static CapError Preallocate(int fd, in FileOpenRequest request)
    {
        if (request.PreallocationSize <= 0 || !(request.Creates || request.Truncates))
        {
            return CapError.Success;
        }

        if (LinuxNative.Fallocate(
            fd, LinuxConstants.FALLOC_FL_KEEP_SIZE, 0, request.PreallocationSize) == 0)
        {
            return CapError.Success;
        }

        int errno = Marshal.GetLastPInvokeError();
        return errno is PosixErrno.ENOSPC or PosixErrno.EFBIG
            ? LinuxErrno.ToError(errno)
            : CapError.Success;
    }

    /// <summary>
    /// Translates the authority asked of a directory into the flag that expresses it.
    /// </summary>
    /// <returns>
    /// False when no directory open can mean what was asked, which is the case for any
    /// request to write one.
    /// </returns>
    /// <remarks>
    /// The traversal-only flag is not a smaller version of the read flag; it produces a
    /// descriptor with no data access at all, which can be resolved against but never
    /// listed. Asking for it is how an open says it wants a position in the tree rather than
    /// a directory to read — and on a directory that grants execute permission without read
    /// permission, it is the only open that succeeds.
    /// </remarks>
    private static bool TryDirectoryAccessFlag(CapAccess access, out int flag)
    {
        switch (access)
        {
            case CapAccess.None:
                flag = LinuxConstants.O_PATH;
                return true;

            case CapAccess.Read:
                flag = LinuxConstants.O_RDONLY;
                return true;

            default:
                flag = 0;
                return false;
        }
    }

    /// <summary>
    /// Removes the non-blocking flag the open was issued with.
    /// </summary>
    /// <remarks>
    /// The flag is set on the open so that naming a FIFO cannot hang the calling thread
    /// forever waiting for a writer -- a name inside the sandbox must not be able to stop
    /// the program that resolved it. It is cleared immediately afterwards so that the handle
    /// the caller receives reads and writes like any other.
    /// </remarks>
    private static void ClearNonBlocking(int fd)
    {
        int current = LinuxNative.Fcntl(fd, LinuxConstants.F_GETFL, 0);
        if (current >= 0 && (current & LinuxConstants.O_NONBLOCK) != 0)
        {
            _ = LinuxNative.Fcntl(fd, LinuxConstants.F_SETFL, current & ~LinuxConstants.O_NONBLOCK);
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
        int errno;
        unsafe
        {
            fixed (byte* path = encoded.Bytes)
            {
                fd = LinuxNative.OpenAt(directoryFd, path, flags);
                errno = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
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
    /// <para>
    /// Two codes are ambiguous here and both have to be resolved, because the walk reacts to
    /// them in opposite ways. A directory open that refuses links reports "not a directory"
    /// for a symbolic link and for a plain file alike — the link is not a directory, which is
    /// true and useless. And the link-loop code covers both a link the open declined to
    /// follow and a chain of links too long to follow, which are "read it and carry on" and
    /// "stop" respectively.
    /// </para>
    /// <para>
    /// So the name is asked about again, without following it. That is a second call, on the
    /// failure path only, and it can disagree with the first if the name was reassigned in
    /// between — but the answer it produces is a hint about what to try next, never a
    /// decision about what may be reached. A walk told "this is a link" reads the link, and
    /// finds out there for itself if it is no longer one.
    /// </para>
    /// </remarks>
    private static CapError TranslateOpenFailure(
        int directoryFd,
        ReadOnlySpan<byte> name,
        int errno,
        bool noFollow)
    {
        if (noFollow && errno is LinuxErrno.ELOOP or PosixErrno.ENOTDIR)
        {
            int flags = LinuxConstants.AT_SYMLINK_NOFOLLOW | LinuxConstants.AT_NO_AUTOMOUNT;
            if (StatInto(directoryFd, name, flags, out CapNodeInfo info).IsSuccess &&
                info.Type == CapNodeType.SymbolicLink)
            {
                return CapError.Create(CapErrorCategory.SymbolicLink, CapErrorSource.Errno, errno);
            }
        }

        return LinuxErrno.ToError(errno);
    }

    private static CapError StatInto(int directoryFd, ReadOnlySpan<byte> path, int flags, out CapNodeInfo info)
    {
        info = default;

        StatxBuffer buffer = default;
        long result;
        int errno = 0;
        unsafe
        {
            fixed (byte* name = path)
            {
                result = LinuxNative.Statx(
                    LinuxConstants.SYS_statx,
                    directoryFd,
                    name,
                    flags | LinuxConstants.AT_STATX_DONT_SYNC,
                    LinuxConstants.STATX_TYPE | LinuxConstants.STATX_MODE | LinuxConstants.STATX_INO,
                    &buffer);
                if (result < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }

        if (result < 0)
        {
            return LinuxErrno.ToError(errno);
        }

        // The mask reports what the kernel actually filled in, which is not always what was
        // asked for. A type or an inode that was not returned would otherwise be read as
        // zero -- an "unknown" type, or an inode that compares equal to every other missing
        // one, which would make an identity check agree when it should not.
        const uint Required = LinuxConstants.STATX_TYPE | LinuxConstants.STATX_INO;
        if ((buffer.Mask & Required) != Required)
        {
            return CapError.Create(CapErrorCategory.NotSupported, CapErrorSource.Errno, LinuxErrno.EOPNOTSUPP);
        }

        info = new CapNodeInfo(buffer.NodeType, buffer.VolumeId, buffer.Inode);
        return CapError.Success;
    }

    private CapError OpenConfinedDescriptor(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        int flags,
        uint mode,
        ConfinedResolveOptions options,
        out int descriptor)
    {
        descriptor = -1;

        if (!_probe.Supported)
        {
            return CapError.Create(CapErrorCategory.NotSupported, CapErrorSource.Errno, _probe.Errno);
        }

        using HandleLease lease = root.Lease();
        if (!lease.IsValid)
        {
            return CapError.Create(CapErrorCategory.Unknown, CapErrorSource.Errno, PosixErrno.EBADF);
        }

        Span<byte> scratch = stackalloc byte[PathScratchBytes];
        using UnixPathBuffer encoded = UnixPathBuffer.Create(path, scratch);
        if (!encoded.IsValid)
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        OpenHow how = new()
        {
            Flags = (ulong)(uint)flags,
            Mode = mode,
            Resolve = (ulong)ResolveFor(options),
        };

        for (int attempt = 0; ; attempt++)
        {
            Interlocked.Increment(ref _confinedOpenAttempts);

            long result;
            int errno = 0;
            unsafe
            {
                fixed (byte* name = encoded.Bytes)
                {
                    result = LinuxNative.OpenAt2(
                        LinuxConstants.SYS_openat2, lease.Descriptor, name, &how, OpenHow.Size);
                    if (result < 0)
                    {
                        errno = Marshal.GetLastPInvokeError();
                    }
                }
            }

            if (result >= 0)
            {
                descriptor = (int)result;
                return CapError.Success;
            }

            // The kernel says the tree moved under it and the resolution it had built is no
            // longer trustworthy. That is the expected outcome of resolving a path in a
            // directory something else is renaming inside, and the answer is to start again,
            // not to report a failure the caller cannot act on.
            if (ConfinedRetryPolicy.ShouldRetry(errno, attempt))
            {
                Interlocked.Increment(ref _confinedOpenRaceRetries);
                continue;
            }

            return TranslateConfinedFailure(errno);
        }
    }

    private static ResolveFlags ResolveFor(ConfinedResolveOptions options)
    {
        ResolveFlags resolve = ResolveFlags.Beneath | ResolveFlags.NoMagicLinks;

        if ((options & ConfinedResolveOptions.RefuseSymlinks) != 0)
        {
            resolve |= ResolveFlags.NoSymlinks;
        }

        if ((options & ConfinedResolveOptions.RefuseMountCrossing) != 0)
        {
            resolve |= ResolveFlags.NoCrossDevice;
        }

        return resolve;
    }

    /// <summary>
    /// Reads the failure of a confined open.
    /// </summary>
    /// <remarks>
    /// The cross-device code means something different here from what it means anywhere
    /// else. Ordinarily it reports a rename between filesystems; under confined resolution it
    /// is how the kernel says the path tried to leave the subtree, or tried to cross a mount
    /// the request forbade. Both are refusals to escape, so both are reported as one.
    /// </remarks>
    private static CapError TranslateConfinedFailure(int errno) =>
        errno == PosixErrno.EXDEV
            ? CapError.Create(CapErrorCategory.Escaped, CapErrorSource.Errno, errno)
            : LinuxErrno.ToError(errno);
}
