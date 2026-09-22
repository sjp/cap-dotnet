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
    public PlatformCapabilities Capabilities => new(ResolutionBackend.WindowsRelativeOpen);

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
    public CapResult<SafeFileHandle> OpenChildFile(SafeDirHandle parent, ReadOnlySpan<char> name, CapAccess access)
    {
        CapError error = OpenRelative(
            parent,
            name,
            FileAccessMask(access),
            NtConstants.FILE_NON_DIRECTORY_FILE | NtConstants.FILE_SYNCHRONOUS_IO_NONALERT |
            NtConstants.FILE_OPEN_REPARSE_POINT,
            out nint raw);

        if (error.IsFailure)
        {
            return CapResult<SafeFileHandle>.Fail(error);
        }

        SafeFileHandle handle = new(raw, ownsHandle: true);
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
        CapAccess access,
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

    private static uint FileAccessMask(CapAccess access)
    {
        uint mask = NtConstants.FILE_READ_ATTRIBUTES | NtConstants.SYNCHRONIZE;

        if ((access & CapAccess.Read) != 0)
        {
            mask |= NtConstants.FILE_READ_DATA;
        }

        if ((access & CapAccess.Write) != 0)
        {
            mask |= NtConstants.FILE_WRITE_DATA | NtConstants.FILE_WRITE_ATTRIBUTES;
        }

        return mask;
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
    private static CapError RefuseIfReparsePoint(SafeHandle handle)
    {
        CapError error = QueryAttributeTag(handle, out FileAttributeTagInformation tagInfo);
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

    private static unsafe CapError QueryAttributeTag(SafeHandle handle, out FileAttributeTagInformation result)
    {
        result = default;

        using HandleLease lease = new(handle);
        if (!lease.IsValid)
        {
            return CapError.Create(
                CapErrorCategory.InvalidArgument, CapErrorSource.NtStatus, NtStatusCodes.STATUS_INVALID_HANDLE);
        }

        IoStatusBlock status = default;
        FileAttributeTagInformation value = default;
        int nt = NtNative.NtQueryInformationFile(
            lease.Raw,
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
