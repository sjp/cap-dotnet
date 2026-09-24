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
    /// <summary>
    /// The name every directory has for itself.
    /// </summary>
    /// <remarks>
    /// Written as the byte it is rather than as text, because it is used as a name handed
    /// straight to the kernel and encoding it would be a round trip through a decoder for a
    /// single ASCII character. It is the one name that cannot be reassigned: resolved
    /// against a descriptor it always means the object that descriptor refers to.
    /// </remarks>
    private const byte SelfName = (byte)'.';

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
    public CapResult<string> GetSystemTemporaryDirectory() =>
        CapResult<string>.Ok(UnixTemporaryDirectory.Location);

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
            return CapResult<SafeDirHandle>.Fail(HandleLease.ClosedError);
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
            return CapResult<SafeFileHandle>.Fail(HandleLease.ClosedError);
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
    /// <remarks>
    /// A read-only open without <c>O_DIRECTORY</c> succeeds on a directory here, and the
    /// descriptor it gives is the one a directory open for reading gives, so one open serves
    /// both kinds and the descriptor is asked afterwards which it reached.
    /// </remarks>
    public CapResult<OpenedNode> OpenChildNode(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        in FileOpenRequest request)
    {
        if (!TryFileFlags(in request, out int flags, out CapError unsupported))
        {
            return CapResult<OpenedNode>.Fail(unsupported);
        }

        using HandleLease lease = parent.Lease();
        if (!lease.IsValid)
        {
            return CapResult<OpenedNode>.Fail(HandleLease.ClosedError);
        }

        Span<byte> scratch = stackalloc byte[PathScratchBytes];
        using UnixPathBuffer encoded = UnixPathBuffer.Create(name, scratch);
        if (!encoded.IsValid)
        {
            return CapResult<OpenedNode>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        Interlocked.Increment(ref _componentOpens);

        flags |= LinuxConstants.O_NOFOLLOW | LinuxConstants.O_CLOEXEC | LinuxConstants.O_NONBLOCK;

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
            return CapResult<OpenedNode>.Fail(
                TranslateOpenFailure(lease.Descriptor, encoded.Bytes, errno, noFollow: true));
        }

        return FinishNodeOpen(fd);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// The name handed to the kernel is the directory itself, because that is the whole of
    /// what this open names: the flag asks for storage from the filesystem holding that
    /// directory and for no entry in it. Nothing is resolved, so there is no component for a
    /// link to be planted in and nothing for the confinement rules to decide.
    /// </para>
    /// <para>
    /// Older kernels and filesystems without an implementation refuse the flag, and they do
    /// not agree on how. What they have in common is that the file was not created, so every
    /// one of those answers is reported as <see cref="CapErrorCategory.NotSupported"/> and a
    /// caller that has a fallback can take it.
    /// </para>
    /// </remarks>
    public CapResult<SafeFileHandle> OpenAnonymousChildFile(SafeDirHandle parent, FileAccess access)
    {
        int accessFlag = access switch
        {
            FileAccess.ReadWrite => LinuxConstants.O_RDWR,
            FileAccess.Write => LinuxConstants.O_WRONLY,

            // A file nothing can open and nobody has written is empty and will stay empty,
            // so a read-only handle on one describes no operation.
            _ => -1,
        };

        if (accessFlag < 0)
        {
            return CapResult<SafeFileHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        using HandleLease lease = parent.Lease();
        if (!lease.IsValid)
        {
            return CapResult<SafeFileHandle>.Fail(HandleLease.ClosedError);
        }

        int flags = accessFlag | LinuxConstants.O_TMPFILE | LinuxConstants.O_CLOEXEC;

        int fd;
        int errno;
        unsafe
        {
            ReadOnlySpan<byte> here = [(byte)'.', 0];
            fixed (byte* path = here)
            {
                fd = LinuxNative.OpenAtWithMode(
                    lease.Descriptor, path, flags, LinuxConstants.OwnerOnlyFileCreateMode);
                errno = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
        }

        if (fd < 0)
        {
            return CapResult<SafeFileHandle>.Fail(AnonymousOpenFailure(errno));
        }

        return CapResult<SafeFileHandle>.Ok(new SafeFileHandle(fd, ownsHandle: true));
    }

    /// <summary>
    /// Reads the refusal of a nameless-file open.
    /// </summary>
    /// <remarks>
    /// Three errors mean the same thing here and none of them says so plainly. A kernel that
    /// predates the flag sees a request to open a directory for writing and says so; a
    /// kernel that knows the flag but meets a filesystem without an implementation says the
    /// operation is unsupported; and some report the combination as simply invalid. All
    /// three are "this filesystem will not give you one", which is the distinction a caller
    /// with a fallback needs to draw, so they are drawn as that and everything else is left
    /// as the platform reported it.
    /// </remarks>
    private static CapError AnonymousOpenFailure(int errno) =>
        errno is PosixErrno.EISDIR or PosixErrno.EINVAL or LinuxErrno.EOPNOTSUPP
            ? CapError.Create(CapErrorCategory.NotSupported, CapErrorSource.Errno, errno)
            : LinuxErrno.ToError(errno);

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> OpenConfinedDirectory(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        CapAccess access,
        ConfinedResolveOptions options,
        bool followFinalLink = true)
    {
        if (!TryDirectoryAccessFlag(access, out int accessFlag))
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        int flags = accessFlag | LinuxConstants.O_DIRECTORY | LinuxConstants.O_CLOEXEC;
        if (!followFinalLink)
        {
            flags |= LinuxConstants.O_NOFOLLOW;
        }

        CapError error = OpenConfinedDescriptor(root, path, flags, mode: 0, options, out int fd);
        if (error.IsFailure)
        {
            return CapResult<SafeDirHandle>.Fail(
                !followFinalLink && error.Category == CapErrorCategory.NotADirectory
                    ? ClassifyRefusedDirectory(root, path, options, error)
                    : error);
        }

        return CapResult<SafeDirHandle>.Ok(new SafeDirHandle(fd, ownsHandle: true, access));
    }

    /// <summary>
    /// Reads a directory open that refused a final link and said "not a directory".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The kernel checks that the last component is a directory before it checks whether it
    /// is a link it was told not to follow, so a refused link comes back as "not a
    /// directory" — true of the link, and not what the walk reports for the same refusal.
    /// The name is looked at again, confined as before and without following it, and a link
    /// found there is reported as the refused link it was.
    /// </para>
    /// <para>
    /// A second call on the failure path only, so it can disagree with the first if the name
    /// was replaced between them. Either way the open has already failed: the answer decides
    /// which failure is reported and never what may be reached.
    /// </para>
    /// </remarks>
    private CapError ClassifyRefusedDirectory(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        ConfinedResolveOptions options,
        CapError notADirectory)
    {
        int flags = LinuxConstants.O_PATH | LinuxConstants.O_NOFOLLOW | LinuxConstants.O_CLOEXEC;
        if (OpenConfinedDescriptor(root, path, flags, mode: 0, options, out int fd).IsFailure)
        {
            return notADirectory;
        }

        using SafeFileHandle found = new(fd, ownsHandle: true);
        ReadOnlySpan<byte> self = [0];
        return StatInto(fd, self, LinuxConstants.AT_EMPTY_PATH, out CapNodeInfo info).IsSuccess &&
               info.Type == CapNodeType.SymbolicLink
            ? CapError.Create(CapErrorCategory.SymbolicLinkLoop, CapErrorSource.Errno, LinuxErrno.ELOOP)
            : notADirectory;
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

        // An open that may create or empty the file refuses a link at the last component,
        // as the walk does. The kernel reports it as the same error it gives a link the
        // policy refuses, so both backends come to the same refusal.
        if (!request.FollowsFinalLink)
        {
            flags |= LinuxConstants.O_NOFOLLOW;
        }

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
    public CapResult<OpenedNode> OpenConfinedNode(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        in FileOpenRequest request,
        ConfinedResolveOptions options)
    {
        if (!TryFileFlags(in request, out int flags, out CapError unsupported))
        {
            return CapResult<OpenedNode>.Fail(unsupported);
        }

        flags |= LinuxConstants.O_CLOEXEC | LinuxConstants.O_NONBLOCK;
        if (!request.FollowsFinalLink)
        {
            flags |= LinuxConstants.O_NOFOLLOW;
        }

        CapError error = OpenConfinedDescriptor(root, path, flags, mode: 0, options, out int fd);
        if (error.IsFailure)
        {
            return CapResult<OpenedNode>.Fail(error);
        }

        return FinishNodeOpen(fd);
    }

    /// <inheritdoc/>
    public unsafe CapResult<DirectoryReader> OpenDirectoryReader(SafeDirHandle directory)
    {
        if ((directory.Access & CapAccess.Read) == 0)
        {
            return CapResult<DirectoryReader>.Fail(
                CapError.Create(CapErrorCategory.PermissionDenied, CapErrorSource.Errno, PosixErrno.EACCES));
        }

        using HandleLease lease = directory.Lease();
        if (!lease.IsValid)
        {
            return CapResult<DirectoryReader>.Fail(HandleLease.ClosedError);
        }

        // Opened by the name a directory has for itself, which is the one name in the
        // filesystem that cannot be reassigned: it resolves to the object the descriptor
        // already refers to, whatever a concurrent rename does to the name the caller
        // reached it by. So this is a re-open of the same object rather than a lookup, and
        // it is a re-open rather than a duplicate because a duplicate would share the
        // position a directory read advances.
        ReadOnlySpan<byte> self = [SelfName, 0];
        int fd;
        fixed (byte* name = self)
        {
            fd = LinuxNative.OpenAt(
                lease.Descriptor,
                name,
                LinuxConstants.O_RDONLY | LinuxConstants.O_DIRECTORY | LinuxConstants.O_CLOEXEC);
        }

        if (fd < 0)
        {
            return CapResult<DirectoryReader>.Fail(LinuxErrno.ToError(Marshal.GetLastPInvokeError()));
        }

        return CapResult<DirectoryReader>.Ok(
            new LinuxDirectoryReader(new SafeDirHandle(fd, ownsHandle: true, CapAccess.Read)));
    }

    /// <inheritdoc/>
    public CapResult<string> ReadChildLink(SafeDirHandle parent, ReadOnlySpan<char> name)
    {
        using HandleLease lease = parent.Lease();
        if (!lease.IsValid)
        {
            return CapResult<string>.Fail(HandleLease.ClosedError);
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
                    // The call's only reason to refuse its arguments, given a positive length,
                    // is that the name holds something other than a link.
                    return CapResult<string>.Fail(errno == PosixErrno.EINVAL
                        ? CapError.Create(CapErrorCategory.NotALink, CapErrorSource.Errno, errno)
                        : LinuxErrno.ToError(errno));
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
            return HandleLease.ClosedError;
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
            return HandleLease.ClosedError;
        }

        // An empty name with AT_EMPTY_PATH asks about the descriptor itself, which is the
        // only way to do it that does not involve naming the object again -- and naming it
        // again would ask about whatever holds that name now, not about what was opened.
        ReadOnlySpan<byte> empty = [0];
        return StatInto(lease.Descriptor, empty, LinuxConstants.AT_EMPTY_PATH, out info);
    }

    /// <inheritdoc/>
    public CapError DescribeChild(SafeDirHandle parent, ReadOnlySpan<char> name, out CapNodeStat stat)
    {
        stat = default;

        using HandleLease lease = parent.Lease();
        if (!lease.IsValid)
        {
            return HandleLease.ClosedError;
        }

        Span<byte> scratch = stackalloc byte[PathScratchBytes];
        using UnixPathBuffer encoded = UnixPathBuffer.Create(name, scratch);
        if (!encoded.IsValid)
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        int flags = LinuxConstants.AT_SYMLINK_NOFOLLOW | LinuxConstants.AT_NO_AUTOMOUNT;
        return DescribeInto(lease.Descriptor, encoded.Bytes, flags, out stat);
    }

    /// <inheritdoc/>
    public CapError DescribeHandle(SafeHandle handle, out CapNodeStat stat)
    {
        stat = default;

        using HandleLease lease = new(handle);
        if (!lease.IsValid)
        {
            return HandleLease.ClosedError;
        }

        ReadOnlySpan<byte> empty = [0];
        return DescribeInto(lease.Descriptor, empty, LinuxConstants.AT_EMPTY_PATH, out stat);
    }

    /// <summary>
    /// Fills a caller-facing snapshot from one <c>statx</c> call.
    /// </summary>
    /// <remarks>
    /// Asked with the synchronising flag rather than the cached one, which is the difference
    /// from <see cref="StatInto"/>: the fields a caller wants here are the length and the
    /// times, and those are exactly the fields a network filesystem's cached answer is wrong
    /// about.
    /// </remarks>
    private static CapError DescribeInto(int directoryFd, ReadOnlySpan<byte> path, int flags, out CapNodeStat stat)
    {
        stat = default;

        const uint Wanted =
            LinuxConstants.STATX_TYPE | LinuxConstants.STATX_MODE | LinuxConstants.STATX_INO |
            LinuxConstants.STATX_SIZE | LinuxConstants.STATX_ATIME | LinuxConstants.STATX_MTIME |
            LinuxConstants.STATX_UID | LinuxConstants.STATX_BTIME;

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
                    flags | LinuxConstants.AT_STATX_SYNC_AS_STAT,
                    Wanted,
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

        // Everything but the creation time is required, for the same reason the resolution
        // stat requires its own fields: a value the kernel did not fill in reads as zero,
        // and a zero length or a zero inode is a plausible-looking answer rather than an
        // obviously missing one. The creation time is the exception because it is genuinely
        // optional on this platform, and it is reported as absent rather than as the epoch.
        const uint Required = Wanted & ~LinuxConstants.STATX_BTIME;
        if ((buffer.Mask & Required) != Required)
        {
            return CapError.Create(CapErrorCategory.NotSupported, CapErrorSource.Errno, LinuxErrno.EOPNOTSUPP);
        }

        DateTimeOffset? created = (buffer.Mask & LinuxConstants.STATX_BTIME) != 0
            ? UnixTimestamps.FromParts(buffer.BirthTime.Seconds, buffer.BirthTime.Nanoseconds)
            : null;

        stat = new CapNodeStat(
            UnixFileTypes.FromMode(buffer.Mode),
            buffer.VolumeId,
            buffer.Inode,
            (long)Math.Min(buffer.Size, long.MaxValue),
            UnixTimestamps.FromParts(buffer.AccessTime.Seconds, buffer.AccessTime.Nanoseconds),
            UnixTimestamps.FromParts(buffer.ModifyTime.Seconds, buffer.ModifyTime.Nanoseconds),
            created,
            UnixFileTypes.PermissionsFromMode(buffer.Mode),
            windowsAttributes: null,
            buffer.UserId);

        return CapError.Success;
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
            return CapResult<string>.Fail(HandleLease.ClosedError);
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
    /// <remarks>
    /// One call, and no retry. A commit that reports a failure has left the kernel's record
    /// of what went wrong in a state the next attempt cannot be trusted to report again, so
    /// the answer is passed on rather than second-guessed.
    /// </remarks>
    public CapError SyncDirectory(SafeDirHandle directory)
    {
        using HandleLease lease = directory.Lease();
        if (!lease.IsValid)
        {
            return HandleLease.ClosedError;
        }

        return LinuxNative.FSync(lease.Descriptor) < 0
            ? LinuxErrno.ToError(Marshal.GetLastPInvokeError())
            : CapError.Success;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A value carrying Windows attributes and no mode describes permissions this system does
    /// not have, and is refused rather than approximated. The mode is masked to the bits that
    /// say who may do what, so nothing here can restate what kind of object the handle refers
    /// to.
    /// </remarks>
    public CapError SetHandlePermissions(
        SafeHandle handle,
        UnixFileMode? unixMode,
        FileAttributes? windowsAttributes)
    {
        if (unixMode is not { } mode)
        {
            return CapError.FromCategory(CapErrorCategory.NotSupported);
        }

        using HandleLease lease = new(handle);
        if (!lease.IsValid)
        {
            return HandleLease.ClosedError;
        }

        return LinuxNative.FChmod(lease.Descriptor, UnixFileTypes.ModeFromPermissions(mode)) < 0
            ? LinuxErrno.ToError(Marshal.GetLastPInvokeError())
            : CapError.Success;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// A file handle is set directly. A directory handle may be one opened for traversal
    /// alone, which the kernel does not accept for a call on the descriptor itself, so a
    /// directory is set by naming it through its own handle with an empty name, which reaches
    /// the object the handle holds and nothing else.
    /// </para>
    /// <para>
    /// A kernel that predates empty names for this call rejects that as an invalid argument.
    /// The directory is then opened again through its own handle, as <c>.</c>, and set through
    /// that. This needs read permission on the directory, which the owner normally has.
    /// </para>
    /// </remarks>
    public unsafe CapError SetHandleTimes(SafeHandle handle, CapFileTime lastAccess, CapFileTime lastWrite)
    {
        UnixTimespec* times = stackalloc UnixTimespec[2];
        times[0] = UnixTimestamps.ToTimespec(lastAccess, LinuxConstants.UTIME_NOW, LinuxConstants.UTIME_OMIT);
        times[1] = UnixTimestamps.ToTimespec(lastWrite, LinuxConstants.UTIME_NOW, LinuxConstants.UTIME_OMIT);

        using HandleLease lease = new(handle);
        if (!lease.IsValid)
        {
            return HandleLease.ClosedError;
        }

        if (handle is not SafeDirHandle)
        {
            return LinuxNative.FUtimens(lease.Descriptor, times) < 0
                ? LinuxErrno.ToError(Marshal.GetLastPInvokeError())
                : CapError.Success;
        }

        byte empty = 0;
        if (LinuxNative.UtimensAt(lease.Descriptor, &empty, times, LinuxConstants.AT_EMPTY_PATH) == 0)
        {
            return CapError.Success;
        }

        int errno = Marshal.GetLastPInvokeError();
        if (errno != PosixErrno.EINVAL)
        {
            return LinuxErrno.ToError(errno);
        }

        byte* self = stackalloc byte[] { (byte)'.', 0 };
        int fd = LinuxNative.OpenAt(
            lease.Descriptor,
            self,
            LinuxConstants.O_RDONLY | LinuxConstants.O_DIRECTORY | LinuxConstants.O_CLOEXEC);
        if (fd < 0)
        {
            return LinuxErrno.ToError(Marshal.GetLastPInvokeError());
        }

        using SafeFileHandle reopened = new(fd, ownsHandle: true);
        return LinuxNative.FUtimens(fd, times) < 0
            ? LinuxErrno.ToError(Marshal.GetLastPInvokeError())
            : CapError.Success;
    }

    /// <inheritdoc/>
    public unsafe CapError SetChildTimes(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        CapFileTime lastAccess,
        CapFileTime lastWrite)
    {
        using HandleLease lease = parent.Lease();
        if (!lease.IsValid)
        {
            return HandleLease.ClosedError;
        }

        Span<byte> scratch = stackalloc byte[PathScratchBytes];
        using UnixPathBuffer encoded = UnixPathBuffer.Create(name, scratch);
        if (!encoded.IsValid)
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        UnixTimespec* times = stackalloc UnixTimespec[2];
        times[0] = UnixTimestamps.ToTimespec(lastAccess, LinuxConstants.UTIME_NOW, LinuxConstants.UTIME_OMIT);
        times[1] = UnixTimestamps.ToTimespec(lastWrite, LinuxConstants.UTIME_NOW, LinuxConstants.UTIME_OMIT);

        fixed (byte* path = encoded.Bytes)
        {
            return LinuxNative.UtimensAt(lease.Descriptor, path, times, LinuxConstants.AT_SYMLINK_NOFOLLOW) < 0
                ? LinuxErrno.ToError(Marshal.GetLastPInvokeError())
                : CapError.Success;
        }
    }

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> DuplicateDirectory(SafeDirHandle handle)
    {
        using HandleLease lease = handle.Lease();
        if (!lease.IsValid)
        {
            return CapResult<SafeDirHandle>.Fail(HandleLease.ClosedError);
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
            return CapResult<SafeFileHandle>.Fail(HandleLease.ClosedError);
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
    /// <remarks>
    /// An ordinary copy. Appending is a flag on the open file here, and a copy shares the
    /// open file.
    /// </remarks>
    public CapResult<SafeFileHandle> DuplicateAppendingFile(SafeFileHandle handle) => DuplicateFile(handle);

    /// <inheritdoc/>
    public CapError SetFileAppending(SafeFileHandle handle, bool appending)
    {
        using HandleLease lease = new(handle);
        if (!lease.IsValid)
        {
            return HandleLease.ClosedError;
        }

        int flags = LinuxNative.Fcntl(lease.Descriptor, LinuxConstants.F_GETFL, 0);
        if (flags < 0)
        {
            return LinuxErrno.ToError(Marshal.GetLastPInvokeError());
        }

        int wanted = appending ? flags | LinuxConstants.O_APPEND : flags & ~LinuxConstants.O_APPEND;
        if (wanted == flags)
        {
            return CapError.Success;
        }

        return LinuxNative.Fcntl(lease.Descriptor, LinuxConstants.F_SETFL, wanted) < 0
            ? LinuxErrno.ToError(Marshal.GetLastPInvokeError())
            : CapError.Success;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A positioned write, which this kernel puts at the end of the file whatever offset it
    /// is given while the descriptor appends. Using it rather than a write at the
    /// descriptor's position means a write that races appending being turned off lands at
    /// the offset the caller named, not at a position the caller never chose.
    /// </remarks>
    public unsafe CapError WriteAppending(SafeFileHandle handle, ReadOnlySpan<byte> buffer, long fileOffset)
    {
        using HandleLease lease = new(handle);
        if (!lease.IsValid)
        {
            return HandleLease.ClosedError;
        }

        fixed (byte* start = buffer)
        {
            int done = 0;
            while (done < buffer.Length)
            {
                nint written = LinuxNative.PWrite(
                    lease.Descriptor, start + done, (nuint)(buffer.Length - done), fileOffset + done);

                if (written < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno == PosixErrno.EINTR)
                    {
                        continue;
                    }

                    return LinuxErrno.ToError(errno);
                }

                done += (int)written;
            }
        }

        return CapError.Success;
    }

    /// <inheritdoc/>
    public CapError CreateChildDirectory(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        CreationVisibility visibility)
    {
        using HandleLease lease = parent.Lease();
        if (!lease.IsValid)
        {
            return HandleLease.ClosedError;
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
                result = LinuxNative.MkdirAt(lease.Descriptor, path, CreateMode(visibility));
                if (result < 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }

        return result < 0 ? LinuxErrno.ToError(errno) : CapError.Success;
    }

    /// <summary>
    /// The permissions a directory creation asks the kernel for.
    /// </summary>
    /// <remarks>
    /// The umask narrows whichever of these is chosen, and never widens it, so the owner-only
    /// request stays owner-only in a process configured to create nothing group-readable.
    /// </remarks>
    private static uint CreateMode(CreationVisibility visibility) =>
        visibility == CreationVisibility.OwnerOnly
            ? LinuxConstants.OwnerOnlyDirectoryCreateMode
            : LinuxConstants.DirectoryCreateMode;

    /// <inheritdoc/>
    /// <remarks>
    /// There is no such flag on this platform. Whether a name can be removed is decided by
    /// the permissions on the directory holding it, so a removal that failed here failed for
    /// a reason clearing something on the object would not address, and saying so is better
    /// than succeeding at nothing and letting the caller retry a removal that will fail the
    /// same way.
    /// </remarks>
    public CapError ClearChildRemovalBlock(SafeDirHandle parent, ReadOnlySpan<char> name) =>
        CapError.FromCategory(CapErrorCategory.NotSupported);

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
            return HandleLease.ClosedError;
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
            return HandleLease.ClosedError;
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

        if (result >= 0)
        {
            return CapError.Success;
        }

        // For this call the kernel's permission code has one meaning: the filesystem cannot
        // hold a symbolic link at all. Passed on as a permission failure it would send the
        // caller looking at modes and ownership, which have nothing to do with it.
        return errno == PosixErrno.EPERM
            ? CapError.Create(CapErrorCategory.NotSupported, CapErrorSource.Errno, errno)
            : LinuxErrno.ToError(errno);
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
            return HandleLease.ClosedError;
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

        if (result >= 0)
        {
            return CapError.Success;
        }

        // The kernel's permission code covers several refusals here: a filesystem with no hard
        // links, a directory, a file marked immutable or append-only, and the hardening that
        // stops a second name being made for a file the caller does not own. Only the first is
        // not about permission, and it is the only one that is a property of the volume, so
        // the volume is asked. Both names are
        // on it, or the refusal would have been the cross-device one.
        return errno == PosixErrno.EPERM && HasNoHardLinks(toLease.Descriptor)
            ? CapError.Create(CapErrorCategory.NotSupported, CapErrorSource.Errno, errno)
            : LinuxErrno.ToError(errno);
    }

    /// <summary>
    /// Whether a descriptor is on a filesystem the kernel cannot give a second name on.
    /// </summary>
    /// <remarks>
    /// A list rather than a probe, because the only probe is to try making a link, which is
    /// what just failed. False whenever the question cannot be answered, so that an unknown
    /// volume keeps the kernel's own reading of the failure.
    /// </remarks>
    internal static bool HasNoHardLinks(int fd) =>
        TryGetFilesystemType(fd, out long type) &&
        type is LinuxConstants.MSDOS_SUPER_MAGIC or LinuxConstants.EXFAT_SUPER_MAGIC;

    /// <summary>Reads the type number of the filesystem a descriptor is on.</summary>
    internal static unsafe bool TryGetFilesystemType(int fd, out long type)
    {
        byte* buffer = stackalloc byte[LinuxConstants.StatfsBufferBytes];
        if (LinuxNative.Fstatfs(fd, buffer) < 0)
        {
            type = 0;
            return false;
        }

        type = *(nint*)buffer;
        return true;
    }

    /// <summary>Removes one name beneath a directory descriptor.</summary>
    private static CapError Unlink(SafeDirHandle parent, ReadOnlySpan<char> name, int flags)
    {
        using HandleLease lease = parent.Lease();
        if (!lease.IsValid)
        {
            return HandleLease.ClosedError;
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
                flags |= LinuxConstants.O_CREAT;
                break;
            default:
                break;
        }

        if (request.Appends)
        {
            flags |= LinuxConstants.O_APPEND;
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
        CapError error = IsDirectory(fd, out bool isDirectory);
        if (error.IsFailure)
        {
            return error;
        }

        return isDirectory
            ? CapError.FromCategory(CapErrorCategory.IsADirectory)
            : CapError.Success;
    }

    /// <summary>Asks an open descriptor whether it refers to a directory.</summary>
    private static CapError IsDirectory(int fd, out bool isDirectory)
    {
        ReadOnlySpan<byte> empty = [0];
        CapError error = StatInto(fd, empty, LinuxConstants.AT_EMPTY_PATH, out CapNodeInfo info);
        isDirectory = error.IsSuccess && info.Type == CapNodeType.Directory;
        return error;
    }

    /// <summary>
    /// Turns a descriptor from an open that took whatever the name held into a handle of the
    /// kind it turned out to be.
    /// </summary>
    /// <remarks>
    /// Asked of the descriptor rather than of the name, so the kind is that of the object
    /// opened, however the name has been reassigned since. A directory is handed back with
    /// the authority a directory opened for reading carries, which is what the read-only
    /// descriptor already is.
    /// </remarks>
    private static CapResult<OpenedNode> FinishNodeOpen(int fd)
    {
        ClearNonBlocking(fd);

        CapError error = IsDirectory(fd, out bool isDirectory);
        if (error.IsFailure)
        {
            new SafeFileHandle(fd, ownsHandle: true).Dispose();
            return CapResult<OpenedNode>.Fail(error);
        }

        return CapResult<OpenedNode>.Ok(isDirectory
            ? new OpenedNode(new SafeDirHandle(fd, ownsHandle: true, CapAccess.Read))
            : new OpenedNode(new SafeFileHandle(fd, ownsHandle: true)));
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
    /// One code is ambiguous here and has to be resolved, because the walk reacts to its two
    /// meanings in opposite ways: a directory open that refuses links reports "not a
    /// directory" for a symbolic link and for a plain file alike — the link is not a
    /// directory, which is true and useless.
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
        // An open of one name that refuses to follow links fails with the loop code for one
        // reason only: the name was a link. Asking again whether it still is could only find
        // that it has since changed, and the walk learns that for itself when it reads the
        // link -- reporting a loop here instead would call a lost race a chain too long.
        if (noFollow && errno == LinuxErrno.ELOOP)
        {
            return CapError.Create(CapErrorCategory.SymbolicLink, CapErrorSource.Errno, errno);
        }

        if (noFollow && errno == PosixErrno.ENOTDIR)
        {
            int flags = LinuxConstants.AT_SYMLINK_NOFOLLOW | LinuxConstants.AT_NO_AUTOMOUNT;
            // A directory now, though the open found something that was not one: the name
            // was swapped between the two calls, most often from a link. Reported as the link it
            // most likely was, so that the walk goes on to read it, finds it is not one, and
            // looks at the name again -- a lost race, not an answer about the caller's path.
            if (StatInto(directoryFd, name, flags, out CapNodeInfo info).IsSuccess &&
                info.Type is CapNodeType.SymbolicLink or CapNodeType.Directory)
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
            return HandleLease.ClosedError;
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
