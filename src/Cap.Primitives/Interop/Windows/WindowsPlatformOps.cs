using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Cap.Primitives.Interop.Windows;

/// <summary>
/// The Windows filesystem backend.
/// </summary>
/// <remarks>
/// <para>
/// Windows has no equivalent of the Linux confined open, so resolution here is a walk: one
/// step per component, each taken through the native API with the previous step's handle as
/// the directory to resolve against. That is the Windows form of the same property the Unix
/// walk relies on — a step can only reach an entry of a directory already held — and it
/// carries the same residual race between steps.
/// </para>
/// <para>
/// Two things are done differently here from how Windows code usually does them, and both
/// are load-bearing. Names go to the filesystem as counted strings through the native API,
/// never as paths through the Win32 layer, because that layer rewrites what it is given:
/// trailing dots and spaces disappear, and a reserved device name is recognised wherever it
/// occurs, including after an extension. A validated name is not the name Win32 would open.
/// And every open asks for the reparse point itself rather than its target, so that whether
/// to follow a link is decided here rather than by the object manager — a junction's target
/// is always an absolute path, and following one would leave the sandbox before this code
/// learned a link existed.
/// </para>
/// <para>
/// A name that is not a filesystem link is refused rather than interpreted. Reparse points
/// are a general extension mechanism and most tags have nothing to do with paths; reading an
/// unrecognised one as a link would mean treating a structure of unknown shape as a
/// destination.
/// </para>
/// <para>
/// <strong>Names are matched without regard to case</strong>, which is what the rest of the
/// system does and therefore the only choice under which a name reaches the same file here as
/// it does everywhere else. Asking for case-sensitive matching would not reliably get it —
/// the kernel has a setting that overrides the request — and would mean this library
/// disagreeing with every other program about which file a name refers to. The consequence is
/// stated rather than worked around: <em>containment on this platform never rests on
/// comparing names as strings</em>, because two names that differ only in case are one file.
/// Every decision about whether a step is allowed is made from an open handle instead.
/// </para>
/// <para>
/// <strong>A name that is an alias for a different name is refused.</strong> A filesystem
/// that generates short names gives entries a second, mangled spelling, and an open by that
/// spelling reaches the same object — so a rule a caller states about one spelling can be
/// defeated with the other. Every generated short name contains a tilde, so a component
/// containing one is opened and then asked what it is actually called; the open is handed
/// back if the two disagree. The check costs a query only on the rare component that could
/// be an alias, and it does not refuse a file genuinely named with a tilde, whose own name
/// is what it answers with.
/// </para>
/// <para>
/// <strong>One call here hands the Win32 layer a path,</strong> the one that opens the very
/// first directory by an ordinary path string. That is the ambient step, before any
/// capability exists, and it needs exactly the drive-letter and working-directory handling
/// the rest of this type avoids. It is also the one open that can name something which is not
/// a directory on a filesystem at all, which is why its result is checked against what a user
/// asked for and not merely against what a name may contain. Other Win32 calls appear here
/// where they take a handle instead of a path; those carry no string for the layer to
/// rewrite.
/// </para>
/// <para>
/// <strong>Every handle produced here is asked whether it is on a filesystem</strong> and
/// dropped if it is not. Refusing the reserved device names as strings is the first defence
/// against them and it is a blocklist, which ages badly: the reserved set belongs to Windows
/// and has grown before. A blocklist that has fallen behind fails open, and what it fails open
/// on is a handle to the console or a serial port. So the object that was opened is asked what
/// it is, one kind is accepted and everything else — including a kind this code has never
/// heard of — is refused. Reaching that check means the names missed something, which is why
/// it exists and why it should never fire.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPlatformOps : IPlatformOps
{
    /// <summary>Access enough to traverse a directory and to read what it contains.</summary>
    private const uint DirectoryAccess =
        NtConstants.FILE_LIST_DIRECTORY | NtConstants.FILE_TRAVERSE |
        NtConstants.FILE_READ_ATTRIBUTES | NtConstants.SYNCHRONIZE;

    /// <summary>
    /// Access enough to resolve names beneath a directory, and to ask what it is, but not to
    /// list it.
    /// </summary>
    /// <remarks>
    /// This platform draws the same line the kernel path walk draws everywhere: traversing a
    /// directory and listing its contents are separate rights, granted separately, and a
    /// handle held only in order to resolve further names needs the first and not the second.
    /// Asking for only what is needed keeps an anchor handle from being usable as a
    /// directory listing if it is passed somewhere it should not have been.
    /// </remarks>
    private const uint TraverseAccess =
        NtConstants.FILE_TRAVERSE | NtConstants.FILE_READ_ATTRIBUTES | NtConstants.SYNCHRONIZE;

    /// <summary>Access enough to ask what something is, and nothing more.</summary>
    private const uint QueryAccess = NtConstants.FILE_READ_ATTRIBUTES | NtConstants.SYNCHRONIZE;

    /// <summary>
    /// The largest reply this layer will accept when it asks an object for its own name.
    /// </summary>
    /// <remarks>
    /// A ceiling on a length the filesystem states, so that a wrong or hostile one cannot
    /// turn a question about a name into a large allocation. Set above the longest path the
    /// platform will store, so nothing reachable is refused by it.
    /// </remarks>
    private const int NameReplyLimit = 64 * 1024;

    /// <inheritdoc/>
    public PlatformCapabilities Capabilities =>
        new(ResolutionBackend.WindowsRelativeOpen, overlappedFileHandles: true);

    /// <inheritdoc/>
    /// <remarks>Always zero: there is no confined open on this platform to attempt.</remarks>
    public long ConfinedOpenAttempts => 0;

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> OpenAmbientDirectory(string path, CapAccess access)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!TryDirectoryAccessMask(access, out uint mask))
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        // The Win32 call, deliberately, and only here. It understands drive letters, the
        // per-process working directory and the rest of the user-facing path syntax, which
        // is exactly what a caller naming a root expects and exactly what must never be
        // reachable once a capability exists.
        nint raw = NtNative.CreateFile(
            path,
            mask,
            NtConstants.FILE_SHARE_ALL,
            securityAttributes: 0,
            NtConstants.OPEN_EXISTING,
            NtConstants.FILE_FLAG_BACKUP_SEMANTICS,
            templateFile: 0);

        if (raw == NtConstants.INVALID_HANDLE_VALUE)
        {
            return CapResult<SafeDirHandle>.Fail(Win32Errors.ToError(Marshal.GetLastWin32Error()));
        }

        SafeDirHandle root = new(raw, ownsHandle: true, access);
        CapError check = RefuseUnlessFilesystemDirectory(root);
        if (check.IsFailure)
        {
            root.Dispose();
            return CapResult<SafeDirHandle>.Fail(check);
        }

        return CapResult<SafeDirHandle>.Ok(root);
    }

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> OpenChildDirectory(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        CapAccess access)
    {
        if (!TryDirectoryAccessMask(access, out uint mask))
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        CapError error = OpenRelative(
            parent,
            name,
            mask,
            NtConstants.FILE_DIRECTORY_FILE | NtConstants.FILE_SYNCHRONOUS_IO_NONALERT |
            NtConstants.FILE_OPEN_REPARSE_POINT,
            out nint raw);

        if (error.IsFailure)
        {
            return CapResult<SafeDirHandle>.Fail(error);
        }

        SafeDirHandle handle = new(raw, ownsHandle: true, access);
        CapError linkCheck = RefuseIfReparsePoint(handle);
        if (linkCheck.IsFailure)
        {
            handle.Dispose();
            return CapResult<SafeDirHandle>.Fail(linkCheck);
        }

        return CapResult<SafeDirHandle>.Ok(handle);
    }

    /// <inheritdoc/>
    public CapResult<SafeFileHandle> OpenChildFile(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        in FileOpenRequest request)
    {
        CapError error = OpenFileRelative(parent, name, in request, out nint raw);
        if (error.IsFailure)
        {
            return CapResult<SafeFileHandle>.Fail(error);
        }

        SafeFileHandle handle = new(raw, ownsHandle: true);

        // Asked after the open, and of the object rather than of the name. A reparse point
        // opened as itself is a link this library will not hand back as a file, whatever the
        // name said and whatever appeared at that name in the meantime.
        CapError linkCheck = RefuseIfReparsePoint(handle);
        if (linkCheck.IsFailure)
        {
            handle.Dispose();
            return CapResult<SafeFileHandle>.Fail(linkCheck);
        }

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
        in FileOpenRequest request,
        ConfinedResolveOptions options) =>
        CapResult<SafeFileHandle>.Fail(ConfinedOpenUnavailable);

    /// <inheritdoc/>
    public CapResult<string> ReadChildLink(SafeDirHandle parent, ReadOnlySpan<char> name)
    {
        CapError error = OpenRelative(
            parent,
            name,
            QueryAccess,
            NtConstants.FILE_SYNCHRONOUS_IO_NONALERT | NtConstants.FILE_OPEN_REPARSE_POINT,
            out nint raw);

        if (error.IsFailure)
        {
            return CapResult<string>.Fail(error);
        }

        using SafeDirHandle handle = new(raw, ownsHandle: true, CapAccess.None);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReparseData.MaximumBufferSize);
        try
        {
            bool ok;
            uint returned;
            int win32 = 0;
            unsafe
            {
                fixed (byte* output = buffer)
                {
                    ok = NtNative.DeviceIoControl(
                        handle,
                        NtConstants.FSCTL_GET_REPARSE_POINT,
                        inBuffer: null,
                        inBufferSize: 0,
                        output,
                        (uint)ReparseData.MaximumBufferSize,
                        out returned,
                        overlapped: 0);
                    if (!ok)
                    {
                        win32 = Marshal.GetLastWin32Error();
                    }
                }
            }

            if (!ok)
            {
                return CapResult<string>.Fail(Win32Errors.ToError(win32));
            }

            if (!ReparseData.TryReadTarget(
                    buffer.AsSpan(0, (int)returned), out string target, out bool isRelative))
            {
                // Either the tag is not one that names a path, or the structure did not
                // describe itself consistently. Both mean the same thing here: there is no
                // link target that can be trusted, so none is produced.
                return CapResult<string>.Fail(CapError.Create(
                    CapErrorCategory.Reparse,
                    CapErrorSource.NtStatus,
                    NtStatusCodes.STATUS_IO_REPARSE_TAG_NOT_HANDLED));
            }

            if (!isRelative)
            {
                // The link says of itself that its target starts from a filesystem root, and
                // that is the reading the filesystem will act on. A target anchored at a root
                // cannot be beneath a directory handle whatever it spells, so it is refused
                // here rather than handed back to be re-resolved.
                //
                // Taken from the structure's flag and not from the spelling of the stored
                // name, because the two can disagree and only one of them decides. A name
                // stored as an ordinary relative path but flagged as rooted would otherwise
                // be walked as though it were relative — the one reading the filesystem
                // itself would never give it. Both readings therefore fail closed: this
                // refuses what declares itself rooted, and the path parser refuses every
                // rooted spelling of what declares itself relative.
                return CapResult<string>.Fail(CapError.Create(
                    CapErrorCategory.Escaped,
                    CapErrorSource.NtStatus,
                    NtStatusCodes.STATUS_REPARSE_POINT_ENCOUNTERED));
            }

            return CapResult<string>.Ok(target);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc/>
    public CapError StatChild(SafeDirHandle parent, ReadOnlySpan<char> name, out CapNodeInfo info)
    {
        info = default;

        CapError error = OpenRelative(
            parent,
            name,
            QueryAccess,
            NtConstants.FILE_SYNCHRONOUS_IO_NONALERT | NtConstants.FILE_OPEN_REPARSE_POINT,
            out nint raw);

        if (error.IsFailure)
        {
            return error;
        }

        using SafeDirHandle handle = new(raw, ownsHandle: true, CapAccess.None);
        return Describe(handle, out info);
    }

    /// <inheritdoc/>
    public CapError StatHandle(SafeDirHandle handle, out CapNodeInfo info) => Describe(handle, out info);

    /// <inheritdoc/>
    /// <remarks>
    /// The reply is normally spelled with the extended-length prefix the system uses
    /// internally, and it is handed back that way rather than tidied: this is a diagnostic,
    /// and a prefix that tells the reader which layer answered is more use than one that has
    /// been quietly removed. A directory reached through a mount point with no drive letter
    /// is named by its volume identifier instead, which is the same answer any other tool
    /// would give for it.
    /// </remarks>
    public CapResult<string> GetHandlePath(SafeDirHandle handle)
    {
        using HandleLease lease = handle.Lease();
        if (!lease.IsValid)
        {
            return CapResult<string>.Fail(CapError.Create(
                CapErrorCategory.InvalidArgument, CapErrorSource.NtStatus, NtStatusCodes.STATUS_INVALID_HANDLE));
        }

        // The call reports the room it needs when the buffer is too small, so the loop runs
        // at most twice -- and is a loop rather than two calls because a rename between them
        // can make the second answer longer than the first said it would be.
        int capacity = 512;
        while (true)
        {
            char[] buffer = ArrayPool<char>.Shared.Rent(capacity);
            try
            {
                uint written;
                int error = 0;
                unsafe
                {
                    fixed (char* target = buffer)
                    {
                        written = NtNative.GetFinalPathNameByHandle(
                            lease.Raw,
                            target,
                            (uint)buffer.Length,
                            NtConstants.FILE_NAME_NORMALIZED_VOLUME_NAME_DOS);
                        if (written == 0)
                        {
                            error = Marshal.GetLastWin32Error();
                        }
                    }
                }

                if (written == 0)
                {
                    return CapResult<string>.Fail(Win32Errors.ToError(error));
                }

                if (written < buffer.Length)
                {
                    return CapResult<string>.Ok(new string(buffer, 0, (int)written));
                }

                // Did not fit: the value is the room required, terminator included.
                capacity = (int)written;
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }

            if (capacity > NameReplyLimit)
            {
                return CapResult<string>.Fail(CapError.Create(
                    CapErrorCategory.NameTooLong, CapErrorSource.Win32, Win32Errors.ERROR_FILENAME_EXCED_RANGE));
            }
        }
    }

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> DuplicateDirectory(SafeDirHandle handle)
    {
        using HandleLease lease = handle.Lease();
        if (!lease.IsValid)
        {
            return CapResult<SafeDirHandle>.Fail(CapError.Create(
                CapErrorCategory.InvalidArgument, CapErrorSource.NtStatus, NtStatusCodes.STATUS_INVALID_HANDLE));
        }

        // The empty name with the handle as the resolution root re-opens the object the
        // handle already refers to. Nothing is named a second time, so nothing a concurrent
        // rename could do changes what comes back.
        //
        // Re-opening is what gives the copy an independent position for enumeration, and it
        // is also why the access has to be asked for again explicitly. The rights a re-open
        // is granted come from the directory's own permissions, not from the handle it
        // started at, so copying a handle opened for traversal alone without restating that
        // would hand back a handle that could list the directory. A copy must carry the
        // authority of its original and not the authority its original could have had.
        if (!TryDirectoryAccessMask(handle.Access, out uint mask))
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        CapError error = OpenRelative(
            handle,
            ReadOnlySpan<char>.Empty,
            mask,
            NtConstants.FILE_DIRECTORY_FILE | NtConstants.FILE_SYNCHRONOUS_IO_NONALERT |
            NtConstants.FILE_OPEN_REPARSE_POINT,
            out nint raw);

        return error.IsFailure
            ? CapResult<SafeDirHandle>.Fail(error)
            : CapResult<SafeDirHandle>.Ok(new SafeDirHandle(raw, ownsHandle: true, handle.Access));
    }

    /// <inheritdoc/>
    public unsafe CapResult<SafeFileHandle> DuplicateFile(SafeFileHandle handle)
    {
        using HandleLease lease = new(handle);
        if (!lease.IsValid)
        {
            return CapResult<SafeFileHandle>.Fail(CapError.Create(
                CapErrorCategory.InvalidArgument, CapErrorSource.NtStatus, NtStatusCodes.STATUS_INVALID_HANDLE));
        }

        // Duplicated rather than re-opened by the empty name, which is how a directory copy
        // is made here. A re-open is granted the rights the object's own permissions allow
        // now, and would also re-run the checks that decide whether the object may be handed
        // back at all; neither is wanted for a copy of a handle whose access has already been
        // granted and whose object has already been vetted. A duplicate carries exactly the
        // access the original was given.
        //
        // Not inheritable: a handle a child process receives is authority the child was never
        // granted, which is the leak the whole design exists to prevent.
        nint copy = 0;
        nint process = NtNative.GetCurrentProcess();
        bool duplicated = NtNative.DuplicateHandle(
            process, lease.Raw, process, &copy, 0, inheritHandle: false, DuplicateSameAccess);

        return duplicated
            ? CapResult<SafeFileHandle>.Ok(new SafeFileHandle(copy, ownsHandle: true))
            : CapResult<SafeFileHandle>.Fail(Win32Errors.ToError(Marshal.GetLastPInvokeError()));
    }

    /// <inheritdoc/>
    public CapError CreateChildDirectory(SafeDirHandle parent, ReadOnlySpan<char> name)
    {
        CapError error = CreateRelative(
            parent,
            name,
            QueryAccess,
            NtConstants.FILE_ATTRIBUTE_DIRECTORY,
            NtConstants.FILE_DIRECTORY_FILE | NtConstants.FILE_SYNCHRONOUS_IO_NONALERT,
            out nint raw);

        if (error.IsFailure)
        {
            return error;
        }

        // The directory is made by the create itself; the handle was only ever the create's
        // result and nothing here wants to keep it.
        _ = NtNative.NtClose(raw);
        return CapError.Success;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The open constrains nothing about what kind of object the name holds, and then the
    /// answer decides. A plain directory is refused, because removing one is the other
    /// operation; everything else — a file, a symbolic link of either kind, a junction — is
    /// a name this removes, which is the same set the Unix call removes and for the same
    /// reason. A link is unlinked as itself: the open asks for the reparse point rather than
    /// what it points at, so nothing about the target is reached or even read.
    /// </remarks>
    public CapError RemoveChildFile(SafeDirHandle parent, ReadOnlySpan<char> name)
    {
        CapError error = OpenRelative(
            parent,
            name,
            NtConstants.DELETE | NtConstants.FILE_READ_ATTRIBUTES | NtConstants.SYNCHRONIZE,
            NtConstants.FILE_SYNCHRONOUS_IO_NONALERT | NtConstants.FILE_OPEN_REPARSE_POINT,
            out nint raw);

        if (error.IsFailure)
        {
            return error;
        }

        try
        {
            CapError kind = QueryAttributeTag(raw, out FileAttributeTagInformation info);
            if (kind.IsFailure)
            {
                return kind;
            }

            bool isReparsePoint = (info.FileAttributes & NtConstants.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
            bool isDirectory = (info.FileAttributes & NtConstants.FILE_ATTRIBUTE_DIRECTORY) != 0;

            return isDirectory && !isReparsePoint
                ? CapError.Create(
                    CapErrorCategory.IsADirectory, CapErrorSource.NtStatus, NtStatusCodes.STATUS_FILE_IS_A_DIRECTORY)
                : MarkForRemoval(raw);
        }
        finally
        {
            _ = NtNative.NtClose(raw);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A reparse point is refused here even when it is a directory one, which is the same
    /// answer the Unix call gives: a link that happens to point at a directory is still a
    /// link, and removing it is removing a name rather than removing a directory. Emptiness
    /// is left to the filesystem to enforce, because only the filesystem can decide it
    /// without a window in which something is added.
    /// </remarks>
    public CapError RemoveChildDirectory(SafeDirHandle parent, ReadOnlySpan<char> name)
    {
        CapError error = OpenRelative(
            parent,
            name,
            NtConstants.DELETE | NtConstants.FILE_READ_ATTRIBUTES | NtConstants.SYNCHRONIZE,
            NtConstants.FILE_DIRECTORY_FILE | NtConstants.FILE_SYNCHRONOUS_IO_NONALERT |
            NtConstants.FILE_OPEN_REPARSE_POINT,
            out nint raw);

        if (error.IsFailure)
        {
            return error;
        }

        try
        {
            CapError kind = QueryAttributeTag(raw, out FileAttributeTagInformation info);
            if (kind.IsFailure)
            {
                return kind;
            }

            return (info.FileAttributes & NtConstants.FILE_ATTRIBUTE_REPARSE_POINT) != 0
                ? CapError.Create(
                    CapErrorCategory.NotADirectory, CapErrorSource.NtStatus, NtStatusCodes.STATUS_NOT_A_DIRECTORY)
                : MarkForRemoval(raw);
        }
        finally
        {
            _ = NtNative.NtClose(raw);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The destination is named by a handle and a single name rather than by a path, which
    /// is what keeps the far end of the move as confined as the near end. Refusing an
    /// existing destination is the filesystem's own behaviour when the replace flag is
    /// absent, so it costs no extra call and leaves no window.
    /// </remarks>
    public CapError RenameChild(
        SafeDirHandle fromParent,
        ReadOnlySpan<char> fromName,
        SafeDirHandle toParent,
        ReadOnlySpan<char> toName,
        bool replaceExisting)
    {
        CapError error = OpenRelative(
            fromParent,
            fromName,
            NtConstants.DELETE | NtConstants.FILE_READ_ATTRIBUTES | NtConstants.SYNCHRONIZE,
            NtConstants.FILE_SYNCHRONOUS_IO_NONALERT | NtConstants.FILE_OPEN_REPARSE_POINT,
            out nint raw);

        if (error.IsFailure)
        {
            return error;
        }

        try
        {
            uint extended = replaceExisting
                ? NtConstants.FILE_RENAME_REPLACE_IF_EXISTS | NtConstants.FILE_RENAME_POSIX_SEMANTICS
                : 0;

            CapError renamed = SetDestinationName(
                raw, toParent, toName, NtConstants.FileRenameInformationExClass, extended);

            // An unimplemented information class is reported two ways depending on how old
            // the system is, and an invalid-argument report is one of them. Falling back on
            // it is safe even when the argument was genuinely wrong: the older form then
            // fails the same way and its failure is what gets reported.
            if (renamed.Category is not (CapErrorCategory.NotSupported or CapErrorCategory.InvalidArgument))
            {
                return renamed;
            }

            // The older form carries a plain flag in the first byte rather than a flag word,
            // so the value is recomputed rather than reused: the newer form's second flag has
            // the numeric value the older form reads as "replace", and passing it through
            // would turn a refusal into exactly the replacement it was asked to prevent.
            return SetDestinationName(
                raw,
                toParent,
                toName,
                NtConstants.FileRenameInformationClass,
                replaceExisting ? 1u : 0u);
        }
        finally
        {
            _ = NtNative.NtClose(raw);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Made in two steps rather than one, because the call this platform offers for making a
    /// link in one step takes two paths and resolves both with the process's own authority,
    /// which is precisely what a directory handle exists to avoid. So an empty object is
    /// created as an entry of the confined directory, and the link is written into it
    /// through its handle.
    /// </para>
    /// <para>
    /// The stub is removed again if the second step fails, so a failure leaves no empty file
    /// or directory behind wearing the name the caller asked for. It cannot be made not to
    /// exist in between: for an instant the name is a zero-length object rather than a link.
    /// </para>
    /// <para>
    /// The link records which kind it is, and the wrong kind cannot be traversed, which is
    /// why the kind is asked for rather than guessed from what the target happens to be
    /// today. It also records separately whether its target is rooted, and the filesystem
    /// acts on that flag rather than on the spelling — so the flag is set from the same
    /// reading of the text that resolution uses, and a rooted target is additionally stored
    /// in the syntax the object manager resolves, which is what the system's own call
    /// stores. The name shown to a reader keeps the caller's spelling either way.
    /// </para>
    /// </remarks>
    public CapError CreateChildSymbolicLink(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        ReadOnlySpan<char> target,
        bool targetIsDirectory)
    {
        if (target.IsEmpty || target.Contains('\0'))
        {
            return CapError.Create(
                CapErrorCategory.InvalidArgument, CapErrorSource.NtStatus, NtStatusCodes.STATUS_OBJECT_NAME_INVALID);
        }

        CapError error = CreateRelative(
            parent,
            name,
            NtConstants.FILE_WRITE_DATA | NtConstants.FILE_WRITE_ATTRIBUTES |
            NtConstants.FILE_READ_ATTRIBUTES | NtConstants.DELETE | NtConstants.SYNCHRONIZE,
            targetIsDirectory ? NtConstants.FILE_ATTRIBUTE_DIRECTORY : NtConstants.FILE_ATTRIBUTE_NORMAL,
            (targetIsDirectory ? NtConstants.FILE_DIRECTORY_FILE : NtConstants.FILE_NON_DIRECTORY_FILE) |
            NtConstants.FILE_SYNCHRONOUS_IO_NONALERT,
            out nint raw);

        if (error.IsFailure)
        {
            return error;
        }

        using SafeFileHandle stub = new(raw, ownsHandle: true);
        CapError written = WriteSymbolicLinkData(stub, target);
        if (written.IsSuccess)
        {
            return CapError.Success;
        }

        // Best effort, and deliberately not allowed to replace the failure that matters: the
        // caller needs to know why the link could not be made, not why the tidying up of a
        // stub they never asked for did not work either.
        _ = MarkForRemoval(raw);
        return written;
    }

    /// <inheritdoc/>
    public CapError CreateChildHardLink(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        SafeDirHandle toParent,
        ReadOnlySpan<char> toName)
    {
        CapError error = OpenRelative(
            parent,
            name,
            NtConstants.FILE_READ_ATTRIBUTES | NtConstants.SYNCHRONIZE,
            NtConstants.FILE_SYNCHRONOUS_IO_NONALERT | NtConstants.FILE_OPEN_REPARSE_POINT,
            out nint raw);

        if (error.IsFailure)
        {
            return error;
        }

        try
        {
            // Never replaces: a second name for an object is always a new name, so the flag
            // that would overwrite the destination is not offered and not passed.
            return SetDestinationName(
                raw, toParent, toName, NtConstants.FileLinkInformationClass, flags: 0);
        }
        finally
        {
            _ = NtNative.NtClose(raw);
        }
    }

    /// <summary>
    /// Creates a name as an entry of an already-open directory.
    /// </summary>
    /// <remarks>
    /// The creating twin of <see cref="OpenRelative"/>, with the same counted name and the
    /// same directory handle as the resolution root, and the same refusal of anything that
    /// is not a filesystem object. It does not ask whether the name reached its object
    /// through an alias, because there was no object: a create either takes a free name or
    /// fails.
    /// </remarks>
    private static unsafe CapError CreateRelative(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        uint desiredAccess,
        uint fileAttributes,
        uint createOptions,
        out nint handle)
    {
        handle = 0;

        if (name.Length > short.MaxValue)
        {
            return CapError.Create(
                CapErrorCategory.NameTooLong, CapErrorSource.NtStatus, NtStatusCodes.STATUS_NAME_TOO_LONG);
        }

        if (name.Contains('\\') || name.Contains('/') || name.Contains('\0'))
        {
            return CapError.Create(
                CapErrorCategory.InvalidArgument, CapErrorSource.NtStatus, NtStatusCodes.STATUS_OBJECT_NAME_INVALID);
        }

        using HandleLease lease = parent.Lease();
        if (!lease.IsValid)
        {
            return CapError.Create(
                CapErrorCategory.InvalidArgument, CapErrorSource.NtStatus, NtStatusCodes.STATUS_INVALID_HANDLE);
        }

        fixed (char* characters = name)
        {
            UnicodeString objectName = new()
            {
                Length = (ushort)(name.Length * sizeof(char)),
                MaximumLength = (ushort)(name.Length * sizeof(char)),
                Buffer = (nint)characters,
            };

            ObjectAttributes attributes = new()
            {
                Length = (uint)ObjectAttributes.StructSize,
                RootDirectory = lease.Raw,
                ObjectName = (nint)(&objectName),
                Attributes = (uint)ObjectAttributeFlags.CaseInsensitive,
                SecurityDescriptor = 0,
                SecurityQualityOfService = 0,
            };

            IoStatusBlock status = default;
            nint created = 0;
            int result = NtNative.NtCreateFile(
                &created,
                desiredAccess,
                &attributes,
                &status,
                allocationSize: null,
                fileAttributes,
                NtConstants.FILE_SHARE_ALL,
                NtConstants.FILE_CREATE,
                createOptions,
                eaBuffer: null,
                eaLength: 0);

            if (NtStatusCodes.IsFailure(result))
            {
                return NtStatusCodes.ToError(result);
            }

            CapError kind = RefuseUnlessFilesystemObject(created);
            if (kind.IsFailure)
            {
                _ = NtNative.NtClose(created);
                return kind;
            }

            handle = created;
            return CapError.Success;
        }
    }

    /// <summary>
    /// Marks an open object for removal.
    /// </summary>
    /// <remarks>
    /// The newer form is asked for first because it can request that the name disappear at
    /// once. This platform's own convention is the opposite — a removed name lingers,
    /// unusable, until the last handle to the object is closed — and a library whose removal
    /// meant something different here from everywhere else would be a trap rather than a
    /// portability layer. Where the filesystem has no such form, the older one is used and
    /// the local convention applies; that is a difference worth having rather than a failure
    /// to report.
    /// </remarks>
    private static unsafe CapError MarkForRemoval(nint handle)
    {
        IoStatusBlock status = default;
        uint flags = NtConstants.FILE_DISPOSITION_DELETE | NtConstants.FILE_DISPOSITION_POSIX_SEMANTICS;
        int nt = NtNative.NtSetInformationFile(
            handle, &status, &flags, sizeof(uint), NtConstants.FileDispositionInformationExClass);

        if (!NtStatusCodes.IsFailure(nt))
        {
            return CapError.Success;
        }

        if (NtStatusCodes.Classify(nt) != CapErrorCategory.NotSupported &&
            nt != NtStatusCodes.STATUS_INVALID_PARAMETER)
        {
            return NtStatusCodes.ToError(nt);
        }

        // The older structure is a single byte that means "delete", padded to the alignment
        // the call expects.
        byte remove = 1;
        status = default;
        nt = NtNative.NtSetInformationFile(
            handle, &status, &remove, sizeof(byte), NtConstants.FileDispositionInformationClass);

        return NtStatusCodes.IsFailure(nt) ? NtStatusCodes.ToError(nt) : CapError.Success;
    }

    /// <summary>
    /// Gives an open object a name beneath another directory handle: a move, or a second
    /// name for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Renaming and hard linking take the same structure and differ only in which class it
    /// is written as, so they are built here once. The layout is a flag word, the directory
    /// handle the name is relative to, the length of the name in bytes, and the characters —
    /// with the handle at its natural alignment, which is why the offsets are computed from
    /// the pointer size rather than written down.
    /// </para>
    /// <para>
    /// The destination handle is what confines the far end. Naming it as a path from the
    /// object being moved would resolve a string all over again, with none of the work
    /// resolution has already done and with this platform's rewriting in front of it.
    /// </para>
    /// </remarks>
    private static unsafe CapError SetDestinationName(
        nint handle,
        SafeDirHandle destinationParent,
        ReadOnlySpan<char> destinationName,
        uint informationClass,
        uint flags)
    {
        if (destinationName.Length > short.MaxValue)
        {
            return CapError.Create(
                CapErrorCategory.NameTooLong, CapErrorSource.NtStatus, NtStatusCodes.STATUS_NAME_TOO_LONG);
        }

        if (destinationName.Contains('\\') || destinationName.Contains('/') ||
            destinationName.Contains('\0'))
        {
            return CapError.Create(
                CapErrorCategory.InvalidArgument, CapErrorSource.NtStatus, NtStatusCodes.STATUS_OBJECT_NAME_INVALID);
        }

        using HandleLease lease = destinationParent.Lease();
        if (!lease.IsValid)
        {
            return CapError.Create(
                CapErrorCategory.InvalidArgument, CapErrorSource.NtStatus, NtStatusCodes.STATUS_INVALID_HANDLE);
        }

        int rootOffset = sizeof(nint);
        int lengthOffset = rootOffset + sizeof(nint);
        int nameOffset = lengthOffset + sizeof(uint);
        int nameBytes = destinationName.Length * sizeof(char);
        int total = nameOffset + nameBytes;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            Span<byte> structure = buffer.AsSpan(0, total);
            structure.Clear();

            BinaryPrimitives.WriteUInt32LittleEndian(structure, flags);
            nint destinationRoot = lease.Raw;
            MemoryMarshal.Write(structure[rootOffset..], in destinationRoot);
            BinaryPrimitives.WriteUInt32LittleEndian(structure[lengthOffset..], (uint)nameBytes);
            MemoryMarshal.AsBytes(destinationName).CopyTo(structure[nameOffset..]);

            IoStatusBlock status = default;
            int nt;
            fixed (byte* raw = structure)
            {
                nt = NtNative.NtSetInformationFile(handle, &status, raw, (uint)total, informationClass);
            }

            return NtStatusCodes.IsFailure(nt) ? NtStatusCodes.ToError(nt) : CapError.Success;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Writes a symbolic link's data into the empty object that will hold it.
    /// </summary>
    /// <remarks>
    /// The structure itself is built beside the code that reads one back, so that the two
    /// cannot come to disagree about where the names live. Whether the target is rooted is
    /// decided here, by the same parser resolution uses, because the filesystem acts on that
    /// flag rather than on how the target is spelled.
    /// </remarks>
    private static unsafe CapError WriteSymbolicLinkData(SafeFileHandle handle, ReadOnlySpan<char> target)
    {
        bool rooted = CapPath.IsRooted(target, CapPathSyntax.Windows);
        int size = ReparseData.SymbolicLinkSize(target, rooted);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            if (!ReparseData.TryBuildSymbolicLink(target, rooted, buffer, out int written))
            {
                return CapError.Create(
                    CapErrorCategory.NameTooLong, CapErrorSource.NtStatus, NtStatusCodes.STATUS_NAME_TOO_LONG);
            }

            bool sent;
            fixed (byte* raw = buffer)
            {
                sent = NtNative.DeviceIoControl(
                    handle,
                    NtConstants.FSCTL_SET_REPARSE_POINT,
                    raw,
                    (uint)written,
                    outBuffer: null,
                    outBufferSize: 0,
                    out _,
                    overlapped: 0);
            }

            return sent ? CapError.Success : Win32Errors.ToError(Marshal.GetLastWin32Error());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static CapError ConfinedOpenUnavailable =>
        CapError.Create(CapErrorCategory.NotSupported, CapErrorSource.NtStatus, NtStatusCodes.STATUS_NOT_SUPPORTED);

    /// <summary>
    /// Translates the authority asked of a directory into the rights the open requests.
    /// </summary>
    /// <returns>
    /// False when no directory open can mean what was asked, which is the case for any
    /// request to write one.
    /// </returns>
    private static bool TryDirectoryAccessMask(CapAccess access, out uint mask)
    {
        switch (access)
        {
            case CapAccess.None:
                mask = TraverseAccess;
                return true;

            case CapAccess.Read:
                mask = DirectoryAccess;
                return true;

            default:
                mask = 0;
                return false;
        }
    }
    /// <summary>
    /// The access this process is asking to keep, when it copies a handle it already holds.
    /// </summary>
    private const uint DuplicateSameAccess = 0x00000002;

    /// <summary>
    /// Opens or creates a file as an entry of an already-open directory, as the request
    /// describes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The creating call rather than the opening one, for every disposition including the one
    /// that cannot create. It is the only call with a disposition at all, and expressing some
    /// modes through one entry point and some through another would mean two code paths whose
    /// share mode, options and attributes had to be kept in agreement by hand.
    /// </para>
    /// <para>
    /// The name is a counted string resolved against the directory handle, which is what
    /// confines it, and the reparse-point flag keeps the object manager from resolving a link
    /// on this library's behalf. Both are the same here as for an ordinary open.
    /// </para>
    /// <para>
    /// <strong>A mode that empties the file is not expressed as a disposition.</strong> The
    /// flag that opens a reparse point as itself, combined with a disposition that overwrites,
    /// overwrites the reparse point — the link is destroyed and replaced with an empty file
    /// before anything has had the chance to notice there was a link. Since the whole purpose
    /// of opening the reparse point as itself is to let this library decide what to do about
    /// it, a disposition that acts first defeats the decision it was supposed to inform. So
    /// emptying is done through the handle instead, by <see cref="TruncateThrough"/>, once the
    /// object has been opened and proved not to be a link.
    /// </para>
    /// </remarks>
    private static unsafe CapError OpenFileRelative(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        in FileOpenRequest request,
        out nint handle)
    {
        handle = 0;

        if (name.Length > short.MaxValue)
        {
            return CapError.Create(
                CapErrorCategory.NameTooLong, CapErrorSource.NtStatus, NtStatusCodes.STATUS_NAME_TOO_LONG);
        }

        if (name.Contains('\\') || name.Contains('/') || name.Contains('\0'))
        {
            return CapError.Create(
                CapErrorCategory.InvalidArgument, CapErrorSource.NtStatus, NtStatusCodes.STATUS_OBJECT_NAME_INVALID);
        }

        using HandleLease lease = parent.Lease();
        if (!lease.IsValid)
        {
            return CapError.Create(
                CapErrorCategory.InvalidArgument, CapErrorSource.NtStatus, NtStatusCodes.STATUS_INVALID_HANDLE);
        }

        // An emptying mode is taken in two halves, and the first half is the one that can be
        // atomic: claiming a free name. It either succeeds, and the file is new and therefore
        // already empty, or it reports the name as taken and the second half opens what is
        // there without touching it.
        if (request.Truncates && request.Creates)
        {
            CapError claimed = CreateFileRelative(lease.Raw, name, in request, NtConstants.FILE_CREATE, out handle);
            if (claimed.Category != CapErrorCategory.AlreadyExists)
            {
                return claimed;
            }
        }

        uint disposition = request.Truncates ? NtConstants.FILE_OPEN : FileRequestDisposition(request.Mode);

        CapError opened = CreateFileRelative(lease.Raw, name, in request, disposition, out handle);
        if (opened.IsFailure)
        {
            return opened;
        }

        if (!request.Truncates)
        {
            return CapError.Success;
        }

        // Only now, with the object open and known not to be a link, is it emptied. A file
        // something else substituted for this one between the two halves is emptied instead,
        // which is the same outcome the single atomic call would have produced.
        CapError kind = RefuseIfReparsePoint(handle);
        CapError emptied = kind.IsFailure ? kind : TruncateThrough(handle, request.PreallocationSize);
        if (emptied.IsFailure)
        {
            _ = NtNative.NtClose(handle);
            handle = 0;
            return emptied;
        }

        return CapError.Success;
    }

    /// <summary>
    /// One native open of a name beneath a directory handle, with the checks every handle
    /// this backend produces is subject to.
    /// </summary>
    private static unsafe CapError CreateFileRelative(
        nint parent,
        ReadOnlySpan<char> name,
        in FileOpenRequest request,
        uint disposition,
        out nint handle)
    {
        handle = 0;

        uint desiredAccess = FileRequestAccessMask(in request);
        uint createOptions = NtConstants.FILE_NON_DIRECTORY_FILE | NtConstants.FILE_OPEN_REPARSE_POINT |
                             FileRequestOptions(in request);

        uint attributes = (request.Options & FileOptions.Encrypted) != 0
            ? NtConstants.FILE_ATTRIBUTE_ENCRYPTED
            : NtConstants.FILE_ATTRIBUTE_NORMAL;

        long allocation = request.PreallocationSize > 0 && disposition != NtConstants.FILE_OPEN
            ? request.PreallocationSize
            : 0;

        fixed (char* characters = name)
        {
            UnicodeString objectName = new()
            {
                Length = (ushort)(name.Length * sizeof(char)),
                MaximumLength = (ushort)(name.Length * sizeof(char)),
                Buffer = (nint)characters,
            };

            ObjectAttributes objectAttributes = new()
            {
                Length = (uint)ObjectAttributes.StructSize,
                RootDirectory = parent,
                ObjectName = (nint)(&objectName),
                Attributes = (uint)ObjectAttributeFlags.CaseInsensitive,
                SecurityDescriptor = 0,
                SecurityQualityOfService = 0,
            };

            IoStatusBlock status = default;
            nint opened = 0;
            int result = NtNative.NtCreateFile(
                &opened,
                desiredAccess,
                &objectAttributes,
                &status,
                allocation > 0 ? &allocation : null,
                attributes,
                FileRequestShare(request.Share),
                disposition,
                createOptions,
                eaBuffer: null,
                eaLength: 0);

            if (NtStatusCodes.IsFailure(result))
            {
                return NtStatusCodes.ToError(result);
            }

            CapError kind = RefuseUnlessFilesystemObject(opened);
            if (kind.IsFailure)
            {
                _ = NtNative.NtClose(opened);
                return kind;
            }

            // Asked even of an open that created the file. A create takes a free name and so
            // has nothing to be aliased to, but the same call also opens names that were
            // already there, and a check that ran only for some dispositions would be a check
            // whose absence depended on a flag the caller chose.
            CapError alias = RefuseAliasedName(opened, name);
            if (alias.IsFailure)
            {
                _ = NtNative.NtClose(opened);
                return alias;
            }

            handle = opened;
            return CapError.Success;
        }
    }

    /// <summary>
    /// Empties an open file, and claims the room the request asked for.
    /// </summary>
    /// <remarks>
    /// Acts on the object rather than on the name, which is the whole reason the emptying is
    /// done here: nothing between the open and this call can redirect it at something else,
    /// however the name is reassigned in the meantime.
    /// </remarks>
    private static unsafe CapError TruncateThrough(nint handle, long preallocationSize)
    {
        IoStatusBlock status = default;

        long allocation = preallocationSize > 0 ? preallocationSize : 0;
        int nt = NtNative.NtSetInformationFile(
            handle, &status, &allocation, sizeof(long), NtConstants.FileAllocationInformationClass);

        if (NtStatusCodes.IsFailure(nt))
        {
            return NtStatusCodes.ToError(nt);
        }

        long end = 0;
        nt = NtNative.NtSetInformationFile(
            handle, &status, &end, sizeof(long), NtConstants.FileEndOfFileInformationClass);

        return NtStatusCodes.IsFailure(nt) ? NtStatusCodes.ToError(nt) : CapError.Success;
    }

    /// <summary>The disposition that expresses a file mode.</summary>
    /// <summary>
    /// The disposition that expresses a file mode, for the modes that do not empty the file.
    /// </summary>
    /// <remarks>
    /// The overwriting dispositions are deliberately absent. None of these destroys anything:
    /// each either takes a free name, opens what is there, or does whichever applies. A mode
    /// that empties the file is taken apart instead — see <see cref="OpenFileRelative"/> —
    /// because an overwriting disposition combined with the flag that opens a reparse point
    /// as itself destroys the reparse point, which is precisely the thing the flag exists to
    /// let this library look at first.
    /// </remarks>
    private static uint FileRequestDisposition(FileMode mode) => mode switch
    {
        FileMode.CreateNew => NtConstants.FILE_CREATE,
        FileMode.OpenOrCreate or FileMode.Append => NtConstants.FILE_OPEN_IF,
        _ => NtConstants.FILE_OPEN,
    };

    /// <summary>
    /// The rights a file open asks for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A handle opened to append is granted the appending right instead of the ordinary write
    /// right and not alongside it. Holding both would let a write land at an explicit offset,
    /// and a handle that can write anywhere is not a handle that appends — it is a handle
    /// that happens to be pointed at the end.
    /// </para>
    /// <para>
    /// The right to wait on the handle goes with the synchronous form and only with it: it is
    /// what makes a blocking read possible, and a handle opened for overlapped operations
    /// that also asked for it would be a handle whose kind could not be told from its access.
    /// </para>
    /// </remarks>
    private static uint FileRequestAccessMask(in FileOpenRequest request)
    {
        uint mask = NtConstants.FILE_READ_ATTRIBUTES;

        if (!request.IsAsynchronous)
        {
            mask |= NtConstants.SYNCHRONIZE;
        }

        if ((request.Access & FileAccess.Read) != 0)
        {
            mask |= NtConstants.FILE_READ_DATA;
        }

        if ((request.Access & FileAccess.Write) != 0)
        {
            mask |= NtConstants.FILE_WRITE_ATTRIBUTES;
            mask |= request.Mode == FileMode.Append
                ? NtConstants.FILE_APPEND_DATA
                : NtConstants.FILE_WRITE_DATA;
        }

        if ((request.Options & FileOptions.DeleteOnClose) != 0)
        {
            mask |= NtConstants.DELETE;
        }

        return mask;
    }

    /// <summary>The open options a request's flags and hints translate to.</summary>
    private static uint FileRequestOptions(in FileOpenRequest request)
    {
        uint options = request.IsAsynchronous ? 0 : NtConstants.FILE_SYNCHRONOUS_IO_NONALERT;

        if ((request.Options & FileOptions.WriteThrough) != 0)
        {
            options |= NtConstants.FILE_WRITE_THROUGH;
        }

        if ((request.Options & FileOptions.SequentialScan) != 0)
        {
            options |= NtConstants.FILE_SEQUENTIAL_ONLY;
        }

        if ((request.Options & FileOptions.RandomAccess) != 0)
        {
            options |= NtConstants.FILE_RANDOM_ACCESS;
        }

        if ((request.Options & FileOptions.DeleteOnClose) != 0)
        {
            options |= NtConstants.FILE_DELETE_ON_CLOSE;
        }

        return options;
    }

    /// <summary>
    /// The share mode a request asks for.
    /// </summary>
    /// <remarks>
    /// Passed through as the caller wrote it, rather than widened to the permissive mode
    /// resolution itself uses. A walk holds its handles for an instant and denying anybody
    /// else access for that instant would break ordinary concurrent use of a sandbox; a
    /// handle handed to a caller is theirs for as long as they keep it, and how they share it
    /// is their business.
    /// </remarks>
    private static uint FileRequestShare(FileShare share)
    {
        uint mode = 0;

        if ((share & FileShare.Read) != 0)
        {
            mode |= NtConstants.FILE_SHARE_READ;
        }

        if ((share & FileShare.Write) != 0)
        {
            mode |= NtConstants.FILE_SHARE_WRITE;
        }

        if ((share & FileShare.Delete) != 0)
        {
            mode |= NtConstants.FILE_SHARE_DELETE;
        }

        return mode;
    }

    /// <summary>
    /// Opens a name as an entry of an already-open directory.
    /// </summary>
    /// <remarks>
    /// The name is passed as a counted string with the directory handle as the resolution
    /// root, which is what confines the open to that directory. A name that begins with a
    /// separator would be read as a path from the object manager's own root instead; the
    /// path parser refuses those long before they arrive here, and this refuses them again
    /// because a single missed case would be an escape rather than a bug.
    /// </remarks>
    private static unsafe CapError OpenRelative(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        uint desiredAccess,
        uint openOptions,
        out nint handle)
    {
        handle = 0;

        if (name.Length > short.MaxValue)
        {
            return CapError.Create(
                CapErrorCategory.NameTooLong, CapErrorSource.NtStatus, NtStatusCodes.STATUS_NAME_TOO_LONG);
        }

        if (name.Contains('\\') || name.Contains('/') || name.Contains('\0'))
        {
            return CapError.Create(
                CapErrorCategory.InvalidArgument, CapErrorSource.NtStatus, NtStatusCodes.STATUS_OBJECT_NAME_INVALID);
        }

        using HandleLease lease = parent.Lease();
        if (!lease.IsValid)
        {
            return CapError.Create(
                CapErrorCategory.InvalidArgument, CapErrorSource.NtStatus, NtStatusCodes.STATUS_INVALID_HANDLE);
        }

        fixed (char* characters = name)
        {
            UnicodeString objectName = new()
            {
                Length = (ushort)(name.Length * sizeof(char)),
                MaximumLength = (ushort)(name.Length * sizeof(char)),
                Buffer = (nint)characters,
            };

            ObjectAttributes attributes = new()
            {
                Length = (uint)ObjectAttributes.StructSize,
                RootDirectory = lease.Raw,
                ObjectName = (nint)(&objectName),
                Attributes = (uint)ObjectAttributeFlags.CaseInsensitive,
                SecurityDescriptor = 0,
                SecurityQualityOfService = 0,
            };

            IoStatusBlock status = default;
            nint opened = 0;
            int result = NtNative.NtOpenFile(
                &opened,
                desiredAccess,
                &attributes,
                &status,
                NtConstants.FILE_SHARE_ALL,
                openOptions);

            if (NtStatusCodes.IsFailure(result))
            {
                return NtStatusCodes.ToError(result);
            }

            // Asked first, and of every handle this backend produces. It is the cheaper of
            // the two questions, and the one whose answer decides whether the other is even
            // meaningful: a device has no name of its own to compare against.
            CapError kind = RefuseUnlessFilesystemObject(opened);
            if (kind.IsFailure)
            {
                _ = NtNative.NtClose(opened);
                return kind;
            }

            CapError alias = RefuseAliasedName(opened, name);
            if (alias.IsFailure)
            {
                _ = NtNative.NtClose(opened);
                return alias;
            }

            handle = opened;
            return CapError.Success;
        }
    }

    /// <summary>
    /// Refuses a handle that was reached by a name that is not the object's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A filesystem that generates short names records two names for the same entry — the one
    /// it was created with and an eight-plus-three alias derived from it — and an open by
    /// either reaches the same object. Nothing about that leaves the directory the name was
    /// looked up in, so it is not an escape; what it defeats is any rule a caller states about
    /// names. A caller that refuses to serve <c>secret documents</c> is not refusing
    /// <c>SECRET~1</c>, and both are the same file.
    /// </para>
    /// <para>
    /// So the object is asked what it is called. Every generated alias contains a tilde, which
    /// makes the presence of one a cheap and complete trigger: a component without one cannot
    /// be a generated alias, and the query is skipped. A component genuinely named with a
    /// tilde answers with itself and is allowed through — which is why the test is a
    /// comparison and not a refusal of the character.
    /// </para>
    /// <para>
    /// The comparison ignores case because the filesystem does, and a name differing from the
    /// stored one only in case is the same name by the only definition that matters here.
    /// </para>
    /// </remarks>
    private static unsafe CapError RefuseAliasedName(nint handle, ReadOnlySpan<char> requested)
    {
        if (!requested.Contains('~'))
        {
            return CapError.Success;
        }

        // The reply is a path from the volume root, so its length is bounded by the depth of
        // the object rather than by the component asked about. Rented rather than stacked
        // for that reason, and affordable because this runs only for a name that could be an
        // alias.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                IoStatusBlock status = default;
                int nt;
                fixed (byte* raw = buffer)
                {
                    nt = NtNative.NtQueryInformationFile(
                        handle, &status, raw, (uint)buffer.Length, NtConstants.FileNameInformationClass);
                }

                if (nt == NtStatusCodes.STATUS_BUFFER_OVERFLOW && attempt == 0)
                {
                    // The length is written even when the characters did not fit, and it is
                    // the end of the name — the part being compared — that was lost, so the
                    // reply cannot be used as it stands. One retry at the stated size is
                    // enough; a second overflow would mean the filesystem is answering
                    // inconsistently, and guessing at a third size would be worse than
                    // refusing.
                    uint needed = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
                    if (needed > NameReplyLimit)
                    {
                        return CapError.Create(
                            CapErrorCategory.NameTooLong,
                            CapErrorSource.NtStatus,
                            NtStatusCodes.STATUS_NAME_TOO_LONG);
                    }

                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(sizeof(uint) + (int)needed);
                    continue;
                }

                if (NtStatusCodes.IsFailure(nt))
                {
                    return NtStatusCodes.ToError(nt);
                }

                // Parsing a reply, so its self-described length is checked against what was
                // actually written rather than trusted.
                uint nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
                if (nameBytes > buffer.Length - sizeof(uint) || (nameBytes & 1) != 0)
                {
                    return CapError.Create(
                        CapErrorCategory.Unknown, CapErrorSource.NtStatus, NtStatusCodes.STATUS_INVALID_PARAMETER);
                }

                ReadOnlySpan<char> full = MemoryMarshal.Cast<byte, char>(
                    buffer.AsSpan(sizeof(uint), (int)nameBytes));

                int separator = full.LastIndexOf('\\');
                ReadOnlySpan<char> stored = separator < 0 ? full : full[(separator + 1)..];

                return stored.Equals(requested, StringComparison.OrdinalIgnoreCase)
                    ? CapError.Success
                    : CapError.FromCategory(CapErrorCategory.AliasedName);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Refuses a handle that does not refer to an object on a filesystem.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the second of the two defences against Windows device names, and the only one
    /// that does not depend on having anticipated the name. The first is a rule about
    /// strings: a component spelling <c>CON</c>, <c>NUL</c>, <c>COM1</c> or any of their
    /// disguises is refused while it is still a string, before anything is opened. That rule
    /// is a blocklist of operating system behaviour, and the reserved set is the operating
    /// system's to change — it has grown before. A blocklist that falls behind fails open,
    /// which is the wrong direction for the one check standing between a caller and a handle
    /// to the console.
    /// </para>
    /// <para>
    /// So the object that was actually opened is asked what it is, and anything that is not a
    /// file or directory on a filesystem is dropped. That question is answered by the system
    /// from the handle, so it costs nothing to keep current and it does not care how the name
    /// was spelled. A name nobody anticipated still cannot be used.
    /// </para>
    /// <para>
    /// Anything other than a filesystem object is refused, rather than the known device kinds
    /// being listed and refused. The distinction matters when the system reports a kind this
    /// code has never heard of: refusing everything unrecognised means such a handle is
    /// dropped, where listing what to refuse would mean it is handed back.
    /// </para>
    /// </remarks>
    private static CapError RefuseUnlessFilesystemObject(nint handle) =>
        NtNative.GetFileType(handle) == NtConstants.FILE_TYPE_DISK
            ? CapError.Success
            : CapError.FromCategory(CapErrorCategory.DeviceObject);

    /// <inheritdoc cref="RefuseUnlessFilesystemObject(nint)"/>
    /// <remarks>
    /// Reachable from outside this type so that the defence can be aimed at a device handle
    /// directly. Every route into it through ordinary resolution has a name refused before
    /// an open is attempted, which is the point of the first defence and also means that
    /// exercising the second one that way is impossible — the assertion would only ever
    /// observe the parser.
    /// </remarks>
    internal static CapError RefuseUnlessFilesystemObject(SafeHandle handle)
    {
        using HandleLease lease = new(handle);
        return lease.IsValid
            ? RefuseUnlessFilesystemObject(lease.Raw)
            : CapError.Create(
                CapErrorCategory.InvalidArgument, CapErrorSource.NtStatus, NtStatusCodes.STATUS_INVALID_HANDLE);
    }

    /// <summary>
    /// Confirms that a handle opened by an ordinary path is a directory on a filesystem.
    /// </summary>
    /// <remarks>
    /// The one place a user-facing path string is resolved by the system, and therefore the
    /// one place a name can reach something that is not a file at all: the path syntax this
    /// platform accepts reaches serial ports, volumes and pipes as readily as directories, and
    /// several of the names that do so look like ordinary filenames. Confining names beneath
    /// this handle afterwards would be beside the point if the handle itself were a device. So
    /// the handle is interrogated rather than the path re-examined — what was opened is a fact,
    /// and what a path was going to open is a guess.
    /// </remarks>
    private static CapError RefuseUnlessFilesystemDirectory(SafeDirHandle handle)
    {
        CapError error = RefuseUnlessFilesystemObject(handle);
        if (error.IsFailure)
        {
            return error;
        }

        error = Describe(handle, out CapNodeInfo info);
        if (error.IsFailure)
        {
            return error;
        }

        return info.Type == CapNodeType.Directory
            ? CapError.Success
            : CapError.Create(
                CapErrorCategory.NotADirectory, CapErrorSource.NtStatus, NtStatusCodes.STATUS_NOT_A_DIRECTORY);
    }

    /// <summary>
    /// Refuses a handle that turned out to be a reparse point, and says which kind.
    /// </summary>
    /// <remarks>
    /// The open asked for the reparse point itself rather than its target, so it succeeds on
    /// a link. That is intended — it is what lets this code, rather than the object manager,
    /// decide what happens next — but it means every such open has to ask afterwards whether
    /// what it got was a link, and hand it back rather than treat it as the directory or file
    /// the caller asked for.
    /// </remarks>
    private static CapError RefuseIfReparsePoint(SafeHandle handle) =>
        Classify(QueryAttributeTag(handle, out FileAttributeTagInformation tagInfo), tagInfo);

    /// <summary>
    /// The same refusal asked of a raw handle, for the operations that hold one directly
    /// because they are about to close it themselves.
    /// </summary>
    private static CapError RefuseIfReparsePoint(nint handle) =>
        Classify(QueryAttributeTag(handle, out FileAttributeTagInformation tagInfo), tagInfo);

    private static CapError Classify(CapError error, in FileAttributeTagInformation tagInfo)
    {
        if (error.IsFailure)
        {
            return error;
        }

        if ((tagInfo.FileAttributes & NtConstants.FILE_ATTRIBUTE_REPARSE_POINT) == 0)
        {
            return CapError.Success;
        }

        // A tag that names a path is a link the caller can read and re-resolve. Any other tag
        // describes a structure this library does not know the shape of, so it is refused
        // outright rather than reported as something that could be followed.
        return ReparseTags.IsFilesystemLink(tagInfo.ReparseTag)
            ? CapError.Create(
                CapErrorCategory.SymbolicLink, CapErrorSource.NtStatus, NtStatusCodes.STATUS_REPARSE_POINT_ENCOUNTERED)
            : CapError.Create(
                CapErrorCategory.Reparse, CapErrorSource.NtStatus, NtStatusCodes.STATUS_IO_REPARSE_TAG_NOT_HANDLED);
    }

    private static CapError Describe(SafeHandle handle, out CapNodeInfo info)
    {
        info = default;

        CapError error = QueryAttributeTag(handle, out FileAttributeTagInformation tagInfo);
        if (error.IsFailure)
        {
            return error;
        }

        error = QueryId(handle, out FileIdInformation id);
        if (error.IsFailure)
        {
            return error;
        }

        CapNodeType type;
        uint tag = 0;
        if ((tagInfo.FileAttributes & NtConstants.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            tag = tagInfo.ReparseTag;
            type = ReparseTags.IsFilesystemLink(tag)
                ? CapNodeType.SymbolicLink
                : CapNodeType.UnknownReparsePoint;
        }
        else
        {
            type = (tagInfo.FileAttributes & NtConstants.FILE_ATTRIBUTE_DIRECTORY) != 0
                ? CapNodeType.Directory
                : CapNodeType.File;
        }

        info = new CapNodeInfo(type, id.VolumeSerialNumber, id.FileIdLow, tag);
        return CapError.Success;
    }

    private static CapError QueryAttributeTag(SafeHandle handle, out FileAttributeTagInformation result)
    {
        using HandleLease lease = new(handle);
        if (!lease.IsValid)
        {
            result = default;
            return CapError.Create(
                CapErrorCategory.InvalidArgument, CapErrorSource.NtStatus, NtStatusCodes.STATUS_INVALID_HANDLE);
        }

        return QueryAttributeTag(lease.Raw, out result);
    }

    /// <summary>
    /// The same question asked of a raw handle, for the operations that hold one directly
    /// rather than through a wrapper because they are about to close it themselves.
    /// </summary>
    private static unsafe CapError QueryAttributeTag(nint handle, out FileAttributeTagInformation result)
    {
        result = default;

        IoStatusBlock status = default;
        FileAttributeTagInformation value = default;
        int nt = NtNative.NtQueryInformationFile(
            handle,
            &status,
            &value,
            (uint)sizeof(FileAttributeTagInformation),
            NtConstants.FileAttributeTagInformationClass);

        if (NtStatusCodes.IsFailure(nt))
        {
            return NtStatusCodes.ToError(nt);
        }

        result = value;
        return CapError.Success;
    }

    private static unsafe CapError QueryId(SafeHandle handle, out FileIdInformation result)
    {
        result = default;

        using HandleLease lease = new(handle);
        if (!lease.IsValid)
        {
            return CapError.Create(
                CapErrorCategory.InvalidArgument, CapErrorSource.NtStatus, NtStatusCodes.STATUS_INVALID_HANDLE);
        }

        IoStatusBlock status = default;
        FileIdInformation value = default;
        int nt = NtNative.NtQueryInformationFile(
            lease.Raw,
            &status,
            &value,
            (uint)sizeof(FileIdInformation),
            NtConstants.FileIdInformationClass);

        if (NtStatusCodes.IsFailure(nt))
        {
            return NtStatusCodes.ToError(nt);
        }

        result = value;
        return CapError.Success;
    }
}
