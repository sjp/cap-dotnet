using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Windows;
using Microsoft.Win32.SafeHandles;

namespace Cap.Std.Testing;

/// <summary>
/// The backend beneath every handle on an <see cref="InMemoryFileSystem"/>.
/// </summary>
/// <remarks>
/// <para>
/// Answers each question the way the Linux backend does, since that is the backend whose
/// answers the rest of the library was first written against: the same error for the same
/// refusal, in the same order where two refusals apply at once. The confined open follows
/// <c>openat2</c> under <c>RESOLVE_BENEATH</c>. The differences that are Windows' own are taken
/// only where the filesystem follows Windows rules, and only the ones listed on
/// <see cref="InMemoryFileSystem"/>.
/// </para>
/// <para>
/// A directory handle's value is a key into this object's table and is removed when the
/// handle is closed. A file handle is the framework's own type, which would close its value
/// through the operating system, so it is created not owning its value, and what it was opened
/// for is kept against the handle object itself, weakly, so that a handle nobody closed is
/// forgotten once it is collected. Neither value is ever a real descriptor, which is why raw
/// handles are refused for this backend by the layer above.
/// </para>
/// <para>
/// Every member takes the filesystem's lock for its whole length, which makes each one atomic
/// with respect to the others, as a single system call is. The one exception is closing a
/// directory handle, which can run on the finalizer thread and so touches only a table that is
/// safe to change without the lock.
/// </para>
/// </remarks>
internal sealed class InMemoryPlatformOps : IPlatformOps
{
    /// <summary>The first handle value issued, well above any real descriptor, to be obvious in a dump.</summary>
    private const long FirstHandleValue = 0x6d000000;

    /// <summary>The code a full disk carries on Unix, where the framework reports the raw errno.</summary>
    private const int NoSpaceErrno = 28;

    /// <summary>
    /// Linux's limit, in bytes and counting the terminating NUL, on a path handed to it in one
    /// piece, which also bounds what a symbolic link can store.
    /// </summary>
    private const int LinuxPathMax = 4096;

    /// <summary>As many links as one ambient open follows, as Linux allows in one lookup.</summary>
    private const int MaxAmbientLinks = 40;

    /// <summary>The code a full disk carries on Windows: ERROR_DISK_FULL as an HRESULT.</summary>
    private const int DiskFullHResult = unchecked((int)0x80070070);

    /// <summary>The earliest instant a Windows file time can express.</summary>
    private static readonly DateTimeOffset WindowsEpoch = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly InMemoryFileSystem _fs;
    private readonly ConcurrentDictionary<nint, MemoryNode> _directories = new();
    private readonly ConditionalWeakTable<SafeFileHandle, OpenFile> _files = new();
    private readonly Func<MemoryNode, string, MemoryNode?> _lookup;
    private long _nextHandle = FirstHandleValue;
    private long _confinedOpenAttempts;
    private long _componentOpens;

    public InMemoryPlatformOps(InMemoryFileSystem fileSystem)
    {
        _fs = fileSystem;
        _lookup = static (directory, name) => directory.Entries.GetValueOrDefault(name);
        Capabilities = new PlatformCapabilities(
            ResolutionBackend.InMemory,
            overlappedFileHandles: false,
            confinedOpen: fileSystem.Resolution == ResolutionBackend.ConfinedOpen,
            fileSystem.PathSyntax);
    }

    /// <inheritdoc/>
    public PlatformCapabilities Capabilities { get; }

    /// <inheritdoc/>
    public long ConfinedOpenAttempts => Interlocked.Read(ref _confinedOpenAttempts);

    /// <inheritdoc/>
    /// <remarks>Always zero: nothing changes the tree while a resolution holds the lock.</remarks>
    public long ConfinedOpenRaceRetries => 0;

    /// <inheritdoc/>
    public long ComponentOpens => Interlocked.Read(ref _componentOpens);

    /// <inheritdoc/>
    public bool IssuesKernelHandles => false;

    /// <summary>Issues a handle on a directory, for a root the filesystem opens itself.</summary>
    internal SafeDirHandle OpenDirectoryHandle(MemoryNode directory, CapAccess access)
    {
        nint value = NextHandle();
        _directories[value] = directory;
        return new SafeDirHandle(value, this, access);
    }

    /// <inheritdoc/>
    public bool CloseDirectory(nint handle) => _directories.TryRemove(handle, out _);

    /// <inheritdoc/>
    /// <remarks>
    /// The path is a build path, as <see cref="InMemoryFileSystem"/> describes, or the name
    /// <see cref="GetSystemTemporaryDirectory"/> gave for the scratch directory. Under Windows
    /// rules <c>\</c> separates components too and a leading drive letter is ignored, so that a
    /// Windows path to a directory in the tree opens it. Symbolic links on the way, the last
    /// component included, are followed as the host follows them in a path it is handed:
    /// a relative target from the directory holding the link, and a rooted one from the top of
    /// the tree. This is how a path in the host's own syntax behaves when this filesystem is
    /// standing in for the host.
    /// </remarks>
    public CapResult<SafeDirHandle> OpenAmbientDirectory(string path, CapAccess access)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!IsDirectoryAccess(access))
        {
            return Fail<SafeDirHandle>(CapErrorCategory.InvalidArgument);
        }

        lock (_fs.Gate)
        {
            MemoryNode? node = path == InMemoryFileSystem.ScratchPath ? _fs.Scratch : FindAmbientPath(path);
            if (node is null)
            {
                return Fail<SafeDirHandle>(CapErrorCategory.NotFound);
            }

            if (node.Type != CapNodeType.Directory)
            {
                return Fail<SafeDirHandle>(CapErrorCategory.NotADirectory);
            }

            return node.Unreadable
                ? Fail<SafeDirHandle>(CapErrorCategory.PermissionDenied)
                : CapResult<SafeDirHandle>.Ok(OpenDirectoryHandle(node, access));
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A directory kept apart from the tree a test builds, so that scratch files made through
    /// the backend's own idea of where they go do not appear among a test's entries.
    /// </remarks>
    public CapResult<string> GetSystemTemporaryDirectory() => CapResult<string>.Ok(InMemoryFileSystem.ScratchPath);

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> OpenChildDirectory(SafeDirHandle parent, ReadOnlySpan<char> name, CapAccess access)
    {
        if (!IsDirectoryAccess(access))
        {
            return Fail<SafeDirHandle>(CapErrorCategory.InvalidArgument);
        }

        lock (_fs.Gate)
        {
            Interlocked.Increment(ref _componentOpens);
            CapError error = Child(parent, name, out _, out MemoryNode? node);
            if (error.IsFailure)
            {
                return CapResult<SafeDirHandle>.Fail(error);
            }

            return node!.Type switch
            {
                CapNodeType.SymbolicLink => Fail<SafeDirHandle>(CapErrorCategory.SymbolicLink),
                CapNodeType.UnknownReparsePoint => Fail<SafeDirHandle>(CapErrorCategory.Reparse),
                not CapNodeType.Directory => Fail<SafeDirHandle>(CapErrorCategory.NotADirectory),
                _ when node.Unreadable => Fail<SafeDirHandle>(CapErrorCategory.PermissionDenied),
                _ => CapResult<SafeDirHandle>.Ok(OpenDirectoryHandle(node, access)),
            };
        }
    }

    /// <inheritdoc/>
    public CapResult<SafeFileHandle> OpenChildFile(SafeDirHandle parent, ReadOnlySpan<char> name, in FileOpenRequest request)
    {
        if (Unsupported(in request))
        {
            return Fail<SafeFileHandle>(CapErrorCategory.NotSupported);
        }

        lock (_fs.Gate)
        {
            Interlocked.Increment(ref _componentOpens);
            CapError error = Child(parent, name, out MemoryNode? directory, out MemoryNode? node);
            if (error.Category == CapErrorCategory.NotFound && directory is not null)
            {
                return request.Creates
                    ? CreateFile(directory, name.ToString(), in request)
                    : CapResult<SafeFileHandle>.Fail(error);
            }

            if (error.IsFailure)
            {
                return CapResult<SafeFileHandle>.Fail(error);
            }

            // An exclusive creation is refused by whatever holds the name, a link included,
            // before the link itself is looked at: O_EXCL takes precedence over O_NOFOLLOW.
            if (request.Mode == FileMode.CreateNew)
            {
                return Fail<SafeFileHandle>(CapErrorCategory.AlreadyExists);
            }

            return node!.Type switch
            {
                CapNodeType.SymbolicLink => Fail<SafeFileHandle>(CapErrorCategory.SymbolicLink),
                _ => OpenExisting(node, in request),
            };
        }
    }

    /// <inheritdoc/>
    public CapResult<OpenedNode> OpenChildNode(SafeDirHandle parent, ReadOnlySpan<char> name, in FileOpenRequest request)
    {
        if (Unsupported(in request))
        {
            return Fail<OpenedNode>(CapErrorCategory.NotSupported);
        }

        lock (_fs.Gate)
        {
            Interlocked.Increment(ref _componentOpens);
            CapError error = Child(parent, name, out _, out MemoryNode? node);
            if (error.IsFailure)
            {
                return CapResult<OpenedNode>.Fail(error);
            }

            return node!.Type == CapNodeType.SymbolicLink
                ? Fail<OpenedNode>(CapErrorCategory.SymbolicLink)
                : OpenAny(node, in request);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Supported, as it is on Linux. The file has no name anywhere, so it never counts against
    /// the filesystem's capacity.
    /// </remarks>
    public CapResult<SafeFileHandle> OpenAnonymousChildFile(SafeDirHandle parent, FileAccess access)
    {
        if (access is not (FileAccess.Write or FileAccess.ReadWrite))
        {
            return Fail<SafeFileHandle>(CapErrorCategory.InvalidArgument);
        }

        lock (_fs.Gate)
        {
            CapError error = Directory(parent, out MemoryNode? directory);
            if (error.IsFailure)
            {
                return CapResult<SafeFileHandle>.Fail(error);
            }

            if (directory!.Detached)
            {
                return Fail<SafeFileHandle>(CapErrorCategory.NotFound);
            }

            MemoryNode created = _fs.NewNode(CapNodeType.File);
            created.LinkCount = 0;
            created.Detached = true;
            if (_fs.WindowsRules)
            {
                created.WindowsAttributes = FileAttributes.Archive;
            }
            else
            {
                created.UnixMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            return CapResult<SafeFileHandle>.Ok(IssueFile(created, access, appendOnly: false));
        }
    }

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> OpenConfinedDirectory(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        CapAccess access,
        ConfinedResolveOptions options,
        bool followFinalLink = true)
    {
        if (!IsDirectoryAccess(access))
        {
            return Fail<SafeDirHandle>(CapErrorCategory.InvalidArgument);
        }

        lock (_fs.Gate)
        {
            CapError error = Confined(root, path, options, followFinalLink, out MemoryWalkResult found);
            if (error.IsFailure)
            {
                return CapResult<SafeDirHandle>.Fail(error);
            }

            MemoryNode node = found.Node!;
            if (node.Type != CapNodeType.Directory)
            {
                return Fail<SafeDirHandle>(CapErrorCategory.NotADirectory);
            }

            return node.Unreadable
                ? Fail<SafeDirHandle>(CapErrorCategory.PermissionDenied)
                : CapResult<SafeDirHandle>.Ok(OpenDirectoryHandle(node, access));
        }
    }

    /// <inheritdoc/>
    public CapResult<SafeFileHandle> OpenConfinedFile(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        in FileOpenRequest request,
        ConfinedResolveOptions options)
    {
        if (Unsupported(in request))
        {
            return Fail<SafeFileHandle>(CapErrorCategory.NotSupported);
        }

        lock (_fs.Gate)
        {
            CapError error = Confined(root, path, options, request.FollowsFinalLink, out MemoryWalkResult found);

            if (error.Category == CapErrorCategory.NotFound && found.FinalName is { } name && request.Creates)
            {
                // A name that ends in a separator can only be a directory, and a file open
                // does not make directories.
                return found.RequiresDirectory
                    ? Fail<SafeFileHandle>(CapErrorCategory.IsADirectory)
                    : CreateFile(found.Parent!, name, in request);
            }

            // A final link the open refused still holds the name, which an exclusive creation
            // is refused for first.
            if (error.Category == CapErrorCategory.SymbolicLinkLoop && found.Node is not null &&
                request.Mode == FileMode.CreateNew)
            {
                return Fail<SafeFileHandle>(CapErrorCategory.AlreadyExists);
            }

            if (error.IsFailure)
            {
                return CapResult<SafeFileHandle>.Fail(error);
            }

            if (request.Mode == FileMode.CreateNew)
            {
                return Fail<SafeFileHandle>(CapErrorCategory.AlreadyExists);
            }

            return OpenExisting(found.Node!, in request);
        }
    }

    /// <inheritdoc/>
    public CapResult<OpenedNode> OpenConfinedNode(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        in FileOpenRequest request,
        ConfinedResolveOptions options)
    {
        if (Unsupported(in request))
        {
            return Fail<OpenedNode>(CapErrorCategory.NotSupported);
        }

        lock (_fs.Gate)
        {
            CapError error = Confined(root, path, options, request.FollowsFinalLink, out MemoryWalkResult found);
            return error.IsFailure
                ? CapResult<OpenedNode>.Fail(error)
                : OpenAny(found.Node!, in request);
        }
    }

    /// <inheritdoc/>
    public CapResult<DirectoryReader> OpenDirectoryReader(SafeDirHandle directory)
    {
        if ((directory.Access & CapAccess.Read) == 0)
        {
            return Fail<DirectoryReader>(CapErrorCategory.PermissionDenied);
        }

        lock (_fs.Gate)
        {
            CapError error = Directory(directory, out MemoryNode? node);
            if (error.IsFailure)
            {
                return CapResult<DirectoryReader>.Fail(error);
            }

            if (node!.Detached)
            {
                return Fail<DirectoryReader>(CapErrorCategory.NotFound);
            }

            return node.Unreadable
                ? Fail<DirectoryReader>(CapErrorCategory.PermissionDenied)
                : CapResult<DirectoryReader>.Ok(new MemoryDirectoryReader(node, _fs.Gate));
        }
    }

    /// <inheritdoc/>
    public CapResult<string> ReadChildLink(SafeDirHandle parent, ReadOnlySpan<char> name)
    {
        lock (_fs.Gate)
        {
            CapError error = Child(parent, name, out _, out MemoryNode? node);
            if (error.IsFailure)
            {
                return CapResult<string>.Fail(error);
            }

            return node!.Type == CapNodeType.SymbolicLink
                ? CapResult<string>.Ok(node.LinkTarget ?? string.Empty)
                : Fail<string>(CapErrorCategory.NotALink);
        }
    }

    /// <inheritdoc/>
    public CapError StatChild(SafeDirHandle parent, ReadOnlySpan<char> name, out CapNodeInfo info)
    {
        info = default;
        lock (_fs.Gate)
        {
            CapError error = Child(parent, name, out _, out MemoryNode? node);
            if (error.IsSuccess)
            {
                info = node!.Info;
            }

            return error;
        }
    }

    /// <inheritdoc/>
    public CapError StatHandle(SafeDirHandle handle, out CapNodeInfo info)
    {
        info = default;
        lock (_fs.Gate)
        {
            CapError error = Directory(handle, out MemoryNode? node);
            if (error.IsSuccess)
            {
                info = node!.Info;
            }

            return error;
        }
    }

    /// <inheritdoc/>
    public CapError DescribeChild(SafeDirHandle parent, ReadOnlySpan<char> name, out CapNodeStat stat)
    {
        stat = default;
        lock (_fs.Gate)
        {
            CapError error = Child(parent, name, out _, out MemoryNode? node);
            if (error.IsSuccess)
            {
                stat = Describe(node!);
            }

            return error;
        }
    }

    /// <inheritdoc/>
    public CapError DescribeHandle(SafeHandle handle, out CapNodeStat stat)
    {
        stat = default;
        lock (_fs.Gate)
        {
            CapError error = Node(handle, out MemoryNode? node);
            if (error.IsSuccess)
            {
                stat = Describe(node!);
            }

            return error;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Refused: a directory in memory has no path in any namespace outside the filesystem it
    /// belongs to, so any answer would name something that does not exist.
    /// </remarks>
    public CapResult<string> GetHandlePath(SafeDirHandle handle)
    {
        lock (_fs.Gate)
        {
            CapError error = Directory(handle, out _);
            return error.IsFailure
                ? CapResult<string>.Fail(error)
                : Fail<string>(CapErrorCategory.NotSupported);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Under Unix rules this succeeds at once: nothing in memory is less durable than anything
    /// else. Under Windows rules it reports the request unsupported, as Windows does, since
    /// that system has no way to commit a directory's entries on their own.
    /// </remarks>
    public CapError SyncDirectory(SafeDirHandle directory)
    {
        lock (_fs.Gate)
        {
            CapError error = Directory(directory, out _);
            return error.IsSuccess && _fs.WindowsRules
                ? CapError.FromCategory(CapErrorCategory.NotSupported)
                : error;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Takes whichever half of the value this filesystem records, and refuses a value carrying
    /// only the other half, as the host backends do.
    /// </remarks>
    public CapError SetHandlePermissions(SafeHandle handle, UnixFileMode? unixMode, FileAttributes? windowsAttributes)
    {
        lock (_fs.Gate)
        {
            CapError error = Node(handle, out MemoryNode? node);
            if (error.IsFailure)
            {
                return error;
            }

            if (node!.Unreadable)
            {
                return CapError.FromCategory(CapErrorCategory.PermissionDenied);
            }

            if (_fs.WindowsRules)
            {
                if (windowsAttributes is not { } attributes)
                {
                    return CapError.FromCategory(CapErrorCategory.NotSupported);
                }

                InMemoryFileSystem.ApplyAttributes(node, attributes);
            }
            else
            {
                if (unixMode is not { } mode)
                {
                    return CapError.FromCategory(CapErrorCategory.NotSupported);
                }

                node.UnixMode = mode;
            }

            node.ChangeTime = _fs.Now();
            return CapError.Success;
        }
    }

    /// <inheritdoc/>
    public CapError SetHandleTimes(SafeHandle handle, CapFileTime lastAccess, CapFileTime lastWrite)
    {
        lock (_fs.Gate)
        {
            CapError error = Node(handle, out MemoryNode? node);
            if (error.IsFailure)
            {
                return error;
            }

            return ApplyTimes(node!, lastAccess, lastWrite);
        }
    }

    /// <inheritdoc/>
    public CapError SetChildTimes(SafeDirHandle parent, ReadOnlySpan<char> name, CapFileTime lastAccess, CapFileTime lastWrite)
    {
        lock (_fs.Gate)
        {
            CapError error = Child(parent, name, out _, out MemoryNode? node);
            if (error.IsFailure)
            {
                return error;
            }

            return ApplyTimes(node!, lastAccess, lastWrite);
        }
    }

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> DuplicateDirectory(SafeDirHandle handle)
    {
        lock (_fs.Gate)
        {
            CapError error = Directory(handle, out MemoryNode? node);
            return error.IsFailure
                ? CapResult<SafeDirHandle>.Fail(error)
                : CapResult<SafeDirHandle>.Ok(OpenDirectoryHandle(node!, handle.Access));
        }
    }

    /// <inheritdoc/>
    public CapResult<SafeFileHandle> DuplicateFile(SafeFileHandle handle) => Duplicate(handle, appendOnly: false);

    /// <inheritdoc/>
    /// <remarks>
    /// The copy can only append, as on Windows, so a stream given it puts every write at the
    /// end whatever its position says.
    /// </remarks>
    public CapResult<SafeFileHandle> DuplicateAppendingFile(SafeFileHandle handle) => Duplicate(handle, appendOnly: true);

    private CapResult<SafeFileHandle> Duplicate(SafeFileHandle handle, bool appendOnly)
    {
        lock (_fs.Gate)
        {
            CapError error = File(handle, out OpenFile? file);
            return error.IsFailure
                ? CapResult<SafeFileHandle>.Fail(error)
                : CapResult<SafeFileHandle>.Ok(IssueFile(file!.Node, file.Access, appendOnly || file.AppendOnly, file.Description));
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Under Unix rules the setting is kept on the open file, as Linux keeps its append flag,
    /// so it reaches every copy made by duplication and every positioned write through any of
    /// them, a stream's included. Under Windows rules nothing is kept, as on Windows, and
    /// appending is applied by <see cref="WriteAppending"/> alone.
    /// </remarks>
    public CapError SetFileAppending(SafeFileHandle handle, bool appending)
    {
        lock (_fs.Gate)
        {
            CapError error = File(handle, out OpenFile? file);
            if (error.IsSuccess && !_fs.WindowsRules)
            {
                file!.Description.Appending = appending;
            }

            return error;
        }
    }

    /// <inheritdoc/>
    public CapError WriteAppending(SafeFileHandle handle, ReadOnlySpan<byte> buffer, long fileOffset)
    {
        lock (_fs.Gate)
        {
            CapError error = File(handle, out OpenFile? file);
            if (error.IsFailure)
            {
                return error;
            }

            MemoryNode node = file!.Node;
            if ((file.Access & FileAccess.Write) == 0 || node.Unreadable)
            {
                return CapError.FromCategory(CapErrorCategory.PermissionDenied);
            }

            if (_fs.TakeWriteFault(out CapErrorKind kind))
            {
                return CapError.FromCategory(CategoryOf(kind));
            }

            if (!_fs.HasRoomFor(node, buffer.Length))
            {
                return CapError.Create(CapErrorCategory.Unknown, CapErrorSource.Errno, NoSpaceErrno);
            }

            long before = node.Length;
            node.Append(buffer);
            _fs.Account(node, before);
            Written(node);
            return CapError.Success;
        }
    }

    /// <inheritdoc/>
    public int ReadFile(SafeFileHandle handle, Span<byte> buffer, long fileOffset)
    {
        lock (_fs.Gate)
        {
            return Demand(handle, FileAccess.Read, out _).ReadAt(buffer, fileOffset);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A handle that can only append writes at the end, as the system puts a positioned write
    /// through such a handle on Windows. So does one whose open file has appending turned on,
    /// as Linux puts a positioned write to a file opened for appending.
    /// </remarks>
    public void WriteFile(SafeFileHandle handle, ReadOnlySpan<byte> buffer, long fileOffset)
    {
        lock (_fs.Gate)
        {
            MemoryNode node = Demand(handle, FileAccess.Write, out OpenFile file);
            ThrowIfWriteFails();

            bool append = file.AppendOnly || file.Description.Appending;
            long before = node.Length;
            long end = append ? before + buffer.Length : fileOffset + buffer.Length;
            ThrowIfNoRoom(node, end - before);

            if (append)
            {
                node.Append(buffer);
            }
            else
            {
                node.WriteAt(buffer, fileOffset);
            }

            _fs.Account(node, before);
            Written(node);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Completes before it returns: there is nothing in memory to wait for.</remarks>
    public ValueTask<int> ReadFileAsync(SafeFileHandle handle, Memory<byte> buffer, long fileOffset, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(cancellationToken);
        }

        try
        {
            return ValueTask.FromResult(ReadFile(handle, buffer.Span, fileOffset));
        }
        catch (Exception thrown)
        {
            return ValueTask.FromException<int>(thrown);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Completes before it returns: there is nothing in memory to wait for.</remarks>
    public ValueTask WriteFileAsync(SafeFileHandle handle, ReadOnlyMemory<byte> buffer, long fileOffset, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        try
        {
            WriteFile(handle, buffer.Span, fileOffset);
            return ValueTask.CompletedTask;
        }
        catch (Exception thrown)
        {
            return ValueTask.FromException(thrown);
        }
    }

    /// <inheritdoc/>
    public long GetFileLength(SafeFileHandle handle)
    {
        lock (_fs.Gate)
        {
            return Demand(handle, 0, out _).Length;
        }
    }

    /// <inheritdoc/>
    public void SetFileLength(SafeFileHandle handle, long length)
    {
        lock (_fs.Gate)
        {
            MemoryNode node = Demand(handle, FileAccess.Write, out _);
            ThrowIfWriteFails();

            long before = node.Length;
            ThrowIfNoRoom(node, length - before);
            node.SetLength(length);
            _fs.Account(node, before);
            Written(node);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Returns at once: nothing in memory is less durable than anything else.</remarks>
    public void FlushFileToDisk(SafeFileHandle handle)
    {
        lock (_fs.Gate)
        {
            _ = Demand(handle, 0, out _);
        }
    }

    /// <inheritdoc/>
    public Stream OpenFileStream(SafeFileHandle handle, FileAccess access, int bufferSize, bool isAsync)
    {
        lock (_fs.Gate)
        {
            _ = Demand(handle, 0, out _);
        }

        return new PositionedFileStream(this, handle, access);
    }

    /// <inheritdoc/>
    public CapError CreateChildDirectory(SafeDirHandle parent, ReadOnlySpan<char> name, CreationVisibility visibility)
    {
        lock (_fs.Gate)
        {
            CapError error = Child(parent, name, out MemoryNode? directory, out _);
            if (error.IsSuccess)
            {
                return CapError.FromCategory(CapErrorCategory.AlreadyExists);
            }

            if (error.Category != CapErrorCategory.NotFound || directory is null)
            {
                return error;
            }

            MemoryNode created = _fs.NewNode(CapNodeType.Directory);
            if (!_fs.WindowsRules && visibility == CreationVisibility.OwnerOnly)
            {
                created.UnixMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            }

            _fs.Attach(directory, name.ToString(), created);
            return CapError.Success;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Under Windows rules, clears the read-only attribute. Under Unix rules there is no such
    /// flag, and the answer is the one the Unix backends give.
    /// </remarks>
    public CapError ClearChildRemovalBlock(SafeDirHandle parent, ReadOnlySpan<char> name)
    {
        lock (_fs.Gate)
        {
            CapError error = Child(parent, name, out _, out MemoryNode? node);
            if (error.IsFailure)
            {
                return error;
            }

            if (!_fs.WindowsRules)
            {
                return CapError.FromCategory(CapErrorCategory.NotSupported);
            }

            if (node!.Unreadable)
            {
                return CapError.FromCategory(CapErrorCategory.PermissionDenied);
            }

            InMemoryFileSystem.ApplyAttributes(node, (node.WindowsAttributes ?? 0) & ~FileAttributes.ReadOnly);
            node.ChangeTime = _fs.Now();
            return CapError.Success;
        }
    }

    /// <inheritdoc/>
    public CapError RemoveChildFile(SafeDirHandle parent, ReadOnlySpan<char> name)
    {
        lock (_fs.Gate)
        {
            CapError error = Child(parent, name, out MemoryNode? directory, out MemoryNode? node);
            if (error.IsFailure)
            {
                return error;
            }

            if (node!.Type == CapNodeType.Directory)
            {
                return CapError.FromCategory(CapErrorCategory.IsADirectory);
            }

            if (RefusesRemoval(node))
            {
                return CapError.FromCategory(CapErrorCategory.PermissionDenied);
            }

            Detach(directory!, name.ToString(), node);
            return CapError.Success;
        }
    }

    /// <inheritdoc/>
    public CapError RemoveChildDirectory(SafeDirHandle parent, ReadOnlySpan<char> name)
    {
        lock (_fs.Gate)
        {
            CapError error = Child(parent, name, out MemoryNode? directory, out MemoryNode? node);
            if (error.IsFailure)
            {
                return error;
            }

            if (node!.Type != CapNodeType.Directory)
            {
                return CapError.FromCategory(CapErrorCategory.NotADirectory);
            }

            if (node.Entries.Count > 0)
            {
                return CapError.FromCategory(CapErrorCategory.NotEmpty);
            }

            if (RefusesRemoval(node))
            {
                return CapError.FromCategory(CapErrorCategory.PermissionDenied);
            }

            Detach(directory!, name.ToString(), node);
            return CapError.Success;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Follows <c>rename(2)</c>: replacing a name that refers to the same object does nothing,
    /// a directory replaces only an empty directory, a file replaces only a non-directory, and a
    /// directory cannot be moved beneath itself. Under Windows rules a directory is never
    /// replaced, and a name the read-only attribute protects is not replaced either.
    /// </remarks>
    public CapError RenameChild(
        SafeDirHandle fromParent,
        ReadOnlySpan<char> fromName,
        SafeDirHandle toParent,
        ReadOnlySpan<char> toName,
        bool replaceExisting)
    {
        lock (_fs.Gate)
        {
            CapError error = Child(fromParent, fromName, out MemoryNode? source, out MemoryNode? node);
            if (error.IsFailure)
            {
                return error;
            }

            error = Child(toParent, toName, out MemoryNode? destination, out MemoryNode? existing);
            if (error.IsFailure && (error.Category != CapErrorCategory.NotFound || destination is null))
            {
                return error;
            }

            if (destination!.Detached)
            {
                return CapError.FromCategory(CapErrorCategory.NotFound);
            }

            if (destination.VolumeId != node!.VolumeId)
            {
                return CapError.FromCategory(CapErrorCategory.CrossDevice);
            }

            if (node.Type == CapNodeType.Directory && (ReferenceEquals(node, destination) || Contains(node, destination)))
            {
                return CapError.FromCategory(CapErrorCategory.InvalidArgument);
            }

            string from = fromName.ToString();
            string to = toName.ToString();

            // Refused before anything else is touched, so that a refused move cannot have
            // removed what it was going to replace.
            if (node.Undeletable)
            {
                return CapError.FromCategory(CapErrorCategory.PermissionDenied);
            }

            if (existing is not null)
            {
                if (ReferenceEquals(existing, node))
                {
                    // The same object under both names. On a filesystem that ignores case the
                    // two may be one entry spelled two ways, and then the rename changes only
                    // the spelling it is kept under.
                    if (ReferenceEquals(source, destination) && _fs.Names.Equals(from, to) && !string.Equals(from, to, StringComparison.Ordinal))
                    {
                        _ = source!.Entries.Remove(from);
                        _fs.Attach(destination, to, node);
                        node.ChangeTime = _fs.Now();
                    }

                    return CapError.Success;
                }

                if (!replaceExisting)
                {
                    return CapError.FromCategory(CapErrorCategory.AlreadyExists);
                }

                error = CanReplace(node, existing);
                if (error.IsFailure)
                {
                    return error;
                }

                Detach(destination, to, existing);
            }

            _ = source!.Entries.Remove(from);
            DateTimeOffset now = _fs.Now();
            source.LastWriteTime = now;
            source.ChangeTime = now;
            _fs.Attach(destination, to, node);
            node.ChangeTime = now;
            return CapError.Success;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A target longer than the system would store is refused as too long: under Unix rules
    /// one that does not fit in Linux's path limit, and under Windows rules one whose reparse
    /// data does not fit in the most a reparse point holds.
    /// </remarks>
    public CapError CreateChildSymbolicLink(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        ReadOnlySpan<char> target,
        bool targetIsDirectory)
    {
        bool tooLong = _fs.WindowsRules
            ? ReparseData.SymbolicLinkSize(target, CapPath.IsRooted(target, CapPathSyntax.Windows)) > ReparseData.MaximumBufferSize
            : PathEncoding.GetByteCount(target) >= LinuxPathMax;

        lock (_fs.Gate)
        {
            CapError error = Child(parent, name, out MemoryNode? directory, out _);
            if (error.IsSuccess)
            {
                return CapError.FromCategory(CapErrorCategory.AlreadyExists);
            }

            if (error.Category != CapErrorCategory.NotFound || directory is null)
            {
                return error;
            }

            if (tooLong)
            {
                return CapError.FromCategory(CapErrorCategory.NameTooLong);
            }

            MemoryNode link = _fs.NewNode(CapNodeType.SymbolicLink);
            link.LinkTarget = target.ToString();
            link.LinkIsDirectory = targetIsDirectory;
            if (_fs.WindowsRules && targetIsDirectory)
            {
                link.WindowsAttributes |= FileAttributes.Directory;
            }

            _fs.Attach(directory, name.ToString(), link);
            return CapError.Success;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The new entry refers to the very same node, which is what a hard link is: one object
    /// with two names, telling apart from a copy by its identity. A directory cannot be given a
    /// second name, and the refusal is the one Linux gives. Under Windows rules that reaches a
    /// link made as the directory kind too, since such a link is a directory entry there; under
    /// Unix rules links are untyped and the link itself gets the name.
    /// </remarks>
    public CapError CreateChildHardLink(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        SafeDirHandle toParent,
        ReadOnlySpan<char> toName)
    {
        lock (_fs.Gate)
        {
            CapError error = Child(parent, name, out _, out MemoryNode? node);
            if (error.IsFailure)
            {
                return error;
            }

            error = Child(toParent, toName, out MemoryNode? destination, out _);
            if (error.IsSuccess)
            {
                return CapError.FromCategory(CapErrorCategory.AlreadyExists);
            }

            if (error.Category != CapErrorCategory.NotFound || destination is null)
            {
                return error;
            }

            if (node!.Type == CapNodeType.Directory)
            {
                return CapError.FromCategory(CapErrorCategory.PermissionDenied);
            }

            if (_fs.WindowsRules && node.LinkIsDirectory)
            {
                return CapError.FromCategory(CapErrorCategory.IsADirectory);
            }

            if (destination.VolumeId != node.VolumeId)
            {
                return CapError.FromCategory(CapErrorCategory.CrossDevice);
            }

            _fs.Attach(destination, toName.ToString(), node);
            node.LinkCount++;
            node.ChangeTime = _fs.Now();
            return CapError.Success;
        }
    }

    /// <summary>Resolves a whole path beneath a handle, as the confined open does.</summary>
    private CapError Confined(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        ConfinedResolveOptions options,
        bool followFinalLink,
        out MemoryWalkResult found)
    {
        found = default;
        Interlocked.Increment(ref _confinedOpenAttempts);

        if (!Capabilities.SupportsConfinedOpen)
        {
            return CapError.FromCategory(CapErrorCategory.NotSupported);
        }

        // The kernel takes the whole path in one piece, and refuses one longer than its limit
        // before looking anything up. The limit belongs to the call being modelled rather than
        // to the path rules a test chose, so it applies under either, measured as that syntax
        // measures a name: bytes where the kernel counts bytes, characters where it counts
        // UTF-16 units. A walk has no such limit, because it never hands over more than one
        // name at a time -- which is the difference the corpus records between the two.
        int length = _fs.WindowsRules ? path.Length : PathEncoding.GetByteCount(path);
        if (length >= LinuxPathMax)
        {
            return CapError.FromCategory(CapErrorCategory.NameTooLong);
        }

        CapError error = Directory(root, out MemoryNode? start);
        if (error.IsFailure)
        {
            return error;
        }

        error = MemoryPathWalk.Resolve(start!, path, _fs.PathSyntax, options, followFinalLink, _lookup, out found);

        // A name found missing in a directory that has itself been removed cannot be created.
        if (error.Category == CapErrorCategory.NotFound && found.Parent is { Detached: true })
        {
            found = found with { FinalName = null };
        }

        return error;
    }

    /// <summary>
    /// Looks <paramref name="name"/> up in the directory a handle refers to, without following
    /// it. On <see cref="CapErrorCategory.NotFound"/>, <paramref name="directory"/> is set when
    /// the name could be created there.
    /// </summary>
    private CapError Child(SafeDirHandle parent, ReadOnlySpan<char> name, out MemoryNode? directory, out MemoryNode? node)
    {
        node = null;
        CapError error = Directory(parent, out directory);
        if (error.IsFailure)
        {
            directory = null;
            return error;
        }

        if (directory!.Unreadable)
        {
            directory = null;
            return CapError.FromCategory(CapErrorCategory.PermissionDenied);
        }

        node = directory.Entries.GetValueOrDefault(name.ToString());
        if (node is not null)
        {
            return CapError.Success;
        }

        if (directory.Detached)
        {
            directory = null;
        }

        return CapError.FromCategory(CapErrorCategory.NotFound);
    }

    /// <summary>The directory a handle refers to.</summary>
    private CapError Directory(SafeDirHandle handle, out MemoryNode? directory)
    {
        directory = null;
        if (!ReferenceEquals(handle.Backend, this))
        {
            throw new InvalidOperationException(
                "A directory handle from another backend was passed to an in-memory one. Its value " +
                "is not an entry in this table, and must never be looked up as one.");
        }

        if (handle.IsClosed || handle.IsInvalid || !_directories.TryGetValue(handle.DangerousGetHandle(), out directory))
        {
            return HandleLease.ClosedError;
        }

        return directory.Type == CapNodeType.Directory
            ? CapError.Success
            : CapError.FromCategory(CapErrorCategory.NotADirectory);
    }

    /// <summary>The open file a handle refers to.</summary>
    private CapError File(SafeFileHandle handle, out OpenFile? file)
    {
        file = null;
        if (handle.IsClosed || handle.IsInvalid)
        {
            return HandleLease.ClosedError;
        }

        return _files.TryGetValue(handle, out file)
            ? CapError.Success
            : CapError.FromCategory(CapErrorCategory.InvalidArgument);
    }

    /// <summary>The object a directory or file handle refers to.</summary>
    private CapError Node(SafeHandle handle, out MemoryNode? node)
    {
        node = null;
        switch (handle)
        {
            case SafeDirHandle directory:
                return Directory(directory, out node);
            case SafeFileHandle fileHandle:
                CapError error = File(fileHandle, out OpenFile? file);
                node = file?.Node;
                return error;
            default:
                return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }
    }

    /// <summary>
    /// Finds the file a content operation's handle refers to, refusing it as the framework
    /// refuses a real one.
    /// </summary>
    private MemoryNode Demand(SafeFileHandle handle, FileAccess needed, out OpenFile file)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, handle);

        if (!_files.TryGetValue(handle, out OpenFile? found))
        {
            throw new ArgumentException("The handle is not one this in-memory filesystem issued for a file.", nameof(handle));
        }

        file = found;
        if ((file.Access & needed) != needed || file.Node.Unreadable)
        {
            throw new UnauthorizedAccessException(
                needed == FileAccess.Write
                    ? "The file was not opened for writing, or refuses it."
                    : "The file was not opened for reading, or refuses it.");
        }

        return file.Node;
    }

    /// <summary>Makes a new file under a name that is free, and opens it.</summary>
    private CapResult<SafeFileHandle> CreateFile(MemoryNode directory, string name, in FileOpenRequest request)
    {
        if (directory.Detached)
        {
            return Fail<SafeFileHandle>(CapErrorCategory.NotFound);
        }

        MemoryNode created = _fs.NewNode(CapNodeType.File);
        if (!_fs.HasRoomFor(created, request.PreallocationSize) && request.PreallocationSize > 0)
        {
            return CapResult<SafeFileHandle>.Fail(CapError.Create(CapErrorCategory.Unknown, CapErrorSource.Errno, NoSpaceErrno));
        }

        _fs.Attach(directory, name, created);
        return CapResult<SafeFileHandle>.Ok(IssueFile(created, request.Access, appendOnly: false));
    }

    /// <summary>Opens something a name held, as a file or as a directory, whichever it is.</summary>
    private CapResult<OpenedNode> OpenAny(MemoryNode node, in FileOpenRequest request)
    {
        switch (node.Type)
        {
            case CapNodeType.UnknownReparsePoint:
                return Fail<OpenedNode>(CapErrorCategory.Reparse);
            case CapNodeType.Directory:
                return node.Unreadable
                    ? Fail<OpenedNode>(CapErrorCategory.PermissionDenied)
                    : CapResult<OpenedNode>.Ok(new OpenedNode(OpenDirectoryHandle(node, CapAccess.Read)));
            default:
                CapResult<SafeFileHandle> file = OpenExisting(node, in request);
                return file.IsSuccess
                    ? CapResult<OpenedNode>.Ok(new OpenedNode(file.Value, this))
                    : CapResult<OpenedNode>.Fail(file.Error);
        }
    }

    /// <summary>Opens a file that exists, emptying it first if the open says to.</summary>
    private CapResult<SafeFileHandle> OpenExisting(MemoryNode node, in FileOpenRequest request)
    {
        switch (node.Type)
        {
            case CapNodeType.Directory:
                return Fail<SafeFileHandle>(CapErrorCategory.IsADirectory);
            case CapNodeType.UnknownReparsePoint:
                return Fail<SafeFileHandle>(CapErrorCategory.Reparse);
            case CapNodeType.SymbolicLink:
                return Fail<SafeFileHandle>(CapErrorCategory.SymbolicLink);
        }

        if (node.Unreadable)
        {
            return Fail<SafeFileHandle>(CapErrorCategory.PermissionDenied);
        }

        bool writes = (request.Access & FileAccess.Write) != 0 || request.Truncates;
        if (writes && _fs.WindowsRules && node.RefusesRemoval)
        {
            return Fail<SafeFileHandle>(CapErrorCategory.PermissionDenied);
        }

        if (request.Truncates && node.Length > 0)
        {
            long before = node.Length;
            node.SetLength(0);
            _fs.Account(node, before);
            Written(node);
        }

        if (request.Truncates && request.PreallocationSize > 0 && !_fs.HasRoomFor(node, request.PreallocationSize))
        {
            return CapResult<SafeFileHandle>.Fail(CapError.Create(CapErrorCategory.Unknown, CapErrorSource.Errno, NoSpaceErrno));
        }

        return CapResult<SafeFileHandle>.Ok(IssueFile(node, request.Access, appendOnly: false));
    }

    /// <summary>Issues a file handle and records what it was opened for.</summary>
    private SafeFileHandle IssueFile(MemoryNode node, FileAccess access, bool appendOnly, OpenFileDescription? shared = null)
    {
        SafeFileHandle handle = new(NextHandle(), ownsHandle: false);
        _files.Add(handle, new OpenFile(node, access, appendOnly, shared ?? new OpenFileDescription()));
        return handle;
    }

    /// <summary>Removes a name, and records that the object it named has one fewer.</summary>
    private void Detach(MemoryNode directory, string name, MemoryNode node)
    {
        _ = directory.Entries.Remove(name);
        DateTimeOffset now = _fs.Now();
        directory.LastWriteTime = now;
        directory.ChangeTime = now;
        _fs.Unlinked(node);
    }

    /// <summary>Whether something stops a name being removed or replaced.</summary>
    /// <remarks>
    /// Whether the object can be read plays no part: removing a name changes the directory
    /// holding it, not the object it names.
    /// </remarks>
    private bool RefusesRemoval(MemoryNode node) =>
        node.Undeletable || (_fs.WindowsRules && node.RefusesRemoval);

    /// <summary>Whether <paramref name="node"/> may take the place of <paramref name="existing"/>.</summary>
    private CapError CanReplace(MemoryNode node, MemoryNode existing)
    {
        bool movingDirectory = node.Type == CapNodeType.Directory;
        bool replacingDirectory = existing.Type == CapNodeType.Directory;

        // Windows never replaces a directory by a rename, and the host backend reports the
        // refusal by what stands at the destination rather than as the access denial the
        // filesystem gives, so the same category is given here.
        if (replacingDirectory && _fs.WindowsRules)
        {
            return CapError.FromCategory(CapErrorCategory.IsADirectory);
        }

        if (movingDirectory && !replacingDirectory)
        {
            return CapError.FromCategory(CapErrorCategory.NotADirectory);
        }

        if (!movingDirectory && replacingDirectory)
        {
            return CapError.FromCategory(CapErrorCategory.IsADirectory);
        }

        if (replacingDirectory && existing.Entries.Count > 0)
        {
            return CapError.FromCategory(CapErrorCategory.NotEmpty);
        }

        return RefusesRemoval(existing)
            ? CapError.FromCategory(CapErrorCategory.PermissionDenied)
            : CapError.Success;
    }

    /// <summary>Whether <paramref name="descendant"/> is somewhere beneath <paramref name="directory"/>.</summary>
    private static bool Contains(MemoryNode directory, MemoryNode descendant)
    {
        Stack<MemoryNode> pending = new();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            foreach (MemoryNode child in pending.Pop().Entries.Values)
            {
                if (ReferenceEquals(child, descendant))
                {
                    return true;
                }

                if (child.Type == CapNodeType.Directory)
                {
                    pending.Push(child);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Describes an object, with the link count the platform being imitated reports for it.
    /// </summary>
    /// <remarks>
    /// A Unix directory counts its own entry, its <c>.</c>, and the <c>..</c> of each
    /// directory in it. Windows counts only names.
    /// </remarks>
    private CapNodeStat Describe(MemoryNode node)
    {
        if (node.Type != CapNodeType.Directory)
        {
            return node.Stat;
        }

        if (_fs.WindowsRules)
        {
            return node.Describe(1);
        }

        long subdirectories = 0;
        foreach (MemoryNode child in node.Entries.Values)
        {
            if (child.Type == CapNodeType.Directory)
            {
                subdirectories++;
            }
        }

        return node.Describe((node.Detached ? 0 : 2) + subdirectories);
    }

    /// <remarks>
    /// Under Windows rules an instant before the start of 1601 is refused, as Windows refuses
    /// it: its file times count from then and cannot express anything earlier. The refusal
    /// comes before either time is changed, so a request that cannot be stored changes nothing.
    /// </remarks>
    private CapError ApplyTimes(MemoryNode node, CapFileTime lastAccess, CapFileTime lastWrite)
    {
        if (_fs.WindowsRules && (BeforeWindowsEpoch(lastAccess) || BeforeWindowsEpoch(lastWrite)))
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        DateTimeOffset now = _fs.Now();
        node.LastAccessTime = Resolve(lastAccess, node.LastAccessTime);
        node.LastWriteTime = Resolve(lastWrite, node.LastWriteTime);
        node.ChangeTime = now;

        return CapError.Success;

        DateTimeOffset Resolve(CapFileTime time, DateTimeOffset current) =>
            time.IsNow ? now : time.TryGetValue(out DateTimeOffset value) ? value : current;
    }

    private static bool BeforeWindowsEpoch(CapFileTime time) =>
        time.TryGetValue(out DateTimeOffset value) && value < WindowsEpoch;

    /// <summary>Stamps a change to a file's contents.</summary>
    private void Written(MemoryNode node)
    {
        DateTimeOffset now = _fs.Now();
        node.LastWriteTime = now;
        node.ChangeTime = now;
    }

    /// <summary>Throws the fault a test asked the next write to fail with, if one is due.</summary>
    private void ThrowIfWriteFails()
    {
        if (!_fs.TakeWriteFault(out CapErrorKind kind))
        {
            return;
        }

        const string Message = "The in-memory filesystem was told to fail this write.";
        throw kind switch
        {
            CapErrorKind.Other => new IOException(Message),
            CapErrorKind.NotFound => new FileNotFoundException(Message),
            CapErrorKind.PermissionDenied => new UnauthorizedAccessException(Message),
            CapErrorKind.NameTooLong => new PathTooLongException(Message),
            _ => new CapIOException(kind, Message),
        };
    }

    /// <summary>Throws as a full disk does when a file cannot grow by <paramref name="growth"/> bytes.</summary>
    private void ThrowIfNoRoom(MemoryNode node, long growth)
    {
        if (!_fs.HasRoomFor(node, growth))
        {
            throw new IOException(
                "There is not enough space in the in-memory filesystem.",
                _fs.WindowsRules ? DiskFullHResult : NoSpaceErrno);
        }
    }

    /// <summary>The platform-layer category a public kind is reported through.</summary>
    private static CapErrorCategory CategoryOf(CapErrorKind kind) => kind switch
    {
        CapErrorKind.NotFound => CapErrorCategory.NotFound,
        CapErrorKind.PermissionDenied => CapErrorCategory.PermissionDenied,
        CapErrorKind.AlreadyExists => CapErrorCategory.AlreadyExists,
        CapErrorKind.NotADirectory => CapErrorCategory.NotADirectory,
        CapErrorKind.IsADirectory => CapErrorCategory.IsADirectory,
        CapErrorKind.NotEmpty => CapErrorCategory.NotEmpty,
        CapErrorKind.SymbolicLink => CapErrorCategory.SymbolicLink,
        CapErrorKind.LinkNotFollowed => CapErrorCategory.SymbolicLinkLoop,
        CapErrorKind.NotALink => CapErrorCategory.NotALink,
        CapErrorKind.CrossDevice => CapErrorCategory.CrossDevice,
        CapErrorKind.ReadOnlyFilesystem => CapErrorCategory.ReadOnlyFilesystem,
        CapErrorKind.InvalidArgument => CapErrorCategory.InvalidArgument,
        CapErrorKind.NotSupported => CapErrorCategory.NotSupported,
        CapErrorKind.NameTooLong => CapErrorCategory.NameTooLong,
        CapErrorKind.PathTooDeep => CapErrorCategory.PathTooDeep,
        CapErrorKind.OutOfHandles => CapErrorCategory.OutOfHandles,
        CapErrorKind.ConcurrentChange => CapErrorCategory.Raced,
        CapErrorKind.AliasedName => CapErrorCategory.AliasedName,
        CapErrorKind.Escaped => CapErrorCategory.Escaped,
        _ => CapErrorCategory.Unknown,
    };

    /// <summary>
    /// Whether a request asks for something no file here can do, which Linux refuses too:
    /// removal when the last handle closes, and encryption at rest.
    /// </summary>
    private static bool Unsupported(in FileOpenRequest request) =>
        (request.Options & (FileOptions.DeleteOnClose | FileOptions.Encrypted)) != 0;

    /// <summary>
    /// Whether the authority asked of a directory is one a directory can carry. Asking to
    /// write one is a mistake every real backend reports.
    /// </summary>
    private static bool IsDirectoryAccess(CapAccess access) => access is CapAccess.None or CapAccess.Read;

    /// <summary>
    /// Looks an ambient path up from the top of the tree, following links on the way as the
    /// host's own resolution would. Null when nothing is there, or links lead round in a loop.
    /// </summary>
    private MemoryNode? FindAmbientPath(string path)
    {
        Stack<string> pending = new();
        PushComponents(pending, path);

        List<MemoryNode> trail = [_fs.Root];
        int links = 0;
        while (pending.TryPop(out string? component))
        {
            if (component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                if (trail.Count > 1)
                {
                    trail.RemoveAt(trail.Count - 1);
                }

                continue;
            }

            MemoryNode current = trail[^1];
            if (current.Type != CapNodeType.Directory || !current.Entries.TryGetValue(component, out MemoryNode? next))
            {
                return null;
            }

            if (next.Type != CapNodeType.SymbolicLink)
            {
                trail.Add(next);
                continue;
            }

            if (++links > MaxAmbientLinks)
            {
                return null;
            }

            string target = next.LinkTarget!;
            if (target.StartsWith('/') || (_fs.WindowsRules && CapPath.IsRooted(target, CapPathSyntax.Windows)))
            {
                trail.RemoveRange(1, trail.Count - 1);
            }

            PushComponents(pending, target);
        }

        return trail[^1];
    }

    /// <summary>
    /// Queues a path's components to be taken first, in order: split at <c>/</c>, and under
    /// Windows rules at <c>\</c> too with any drive letter dropped, since there is one volume.
    /// </summary>
    private void PushComponents(Stack<string> pending, string path)
    {
        string[] components;
        if (_fs.WindowsRules)
        {
            bool drive = path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]);
            components = path[(drive ? 2 : 0)..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        }
        else
        {
            components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        }

        for (int i = components.Length - 1; i >= 0; i--)
        {
            pending.Push(components[i]);
        }
    }

    private nint NextHandle() => (nint)Interlocked.Increment(ref _nextHandle);

    private static CapResult<T> Fail<T>(CapErrorCategory category)
        where T : class =>
        CapResult<T>.Fail(CapError.FromCategory(category));

    /// <summary>What an open file handle refers to and was opened for.</summary>
    /// <param name="Node">The file.</param>
    /// <param name="Access">What it may do with the contents.</param>
    /// <param name="AppendOnly">Whether every write through it goes to the end.</param>
    /// <param name="Description">
    /// What it shares with every copy made from it by duplication, as copies of a descriptor
    /// share one open file description on Unix.
    /// </param>
    private sealed record OpenFile(MemoryNode Node, FileAccess Access, bool AppendOnly, OpenFileDescription Description);

    /// <summary>The state an open file shares with its duplicates.</summary>
    private sealed class OpenFileDescription
    {
        /// <summary>Whether appending has been turned on, under Unix rules.</summary>
        public bool Appending { get; set; }
    }
}
