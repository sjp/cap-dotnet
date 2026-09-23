using System.Runtime.InteropServices;
using Cap.Primitives.Interop;
using Microsoft.Win32.SafeHandles;

namespace Cap.Tests.Fakes;

/// <summary>
/// A platform implementation backed by <see cref="FakeFileSystem"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every handle it produces is marked as not owning its value, because the value is an index
/// into this object's own table rather than anything the operating system issued. Closing
/// one through the OS would close whatever real file happened to be using that number —
/// which, in a test suite whose entire subject is handle confusion, would be a memorable way
/// to spend an afternoon.
/// </para>
/// <para>
/// The confined open is modelled as well as the single steps, so that resolution logic
/// written for the kernel-atomic backend can be driven on a machine that has no such kernel.
/// It resolves the whole path in one call, follows links within a budget, and refuses any
/// resolution that would leave the subtree it started from — the same contract the real one
/// offers, minus the atomicity, which nothing in a single-threaded simulation can observe.
/// </para>
/// </remarks>
internal sealed class FakePlatformOps : IPlatformOps
{
    /// <summary>Handle values start well above any plausible real descriptor, to be obvious in a dump.</summary>
    private const int FirstHandleValue = 0x7000;

    private readonly FakeFileSystem _fileSystem;
    private readonly Dictionary<nint, FakeNode> _open = [];
    private readonly List<SafeHandle> _issued = [];
    private nint _nextHandle = FirstHandleValue;
    private long _confinedOpenAttempts;

    /// <summary>How many times a link may be followed before resolution gives up.</summary>
    private const int LinkBudget = 8;

    public FakePlatformOps(FakeFileSystem fileSystem) => _fileSystem = fileSystem;

    /// <inheritdoc/>
    public PlatformCapabilities Capabilities => new(
        _fileSystem.SupportsConfinedOpen ? ResolutionBackend.ConfinedOpen : ResolutionBackend.PortableWalk,
        overlappedFileHandles: false);

    /// <inheritdoc/>
    public long ConfinedOpenAttempts => _confinedOpenAttempts;

    /// <summary>
    /// How many of the handles this instance has produced are still open.
    /// </summary>
    /// <remarks>
    /// A walk holds a handle for every directory it has descended through, so that upward
    /// movement can step back through one rather than ask the kernel to resolve a parent.
    /// Every exit from it therefore has handles to close, including the error exits — which
    /// are the ones a hostile input is trying to take. Descriptors are a process-wide
    /// resource, so a leak there is not a slow leak inside the walk; it is a way to make
    /// unrelated opens elsewhere in the program start failing. Counting is how a test can
    /// insist the walk unwound rather than assume it.
    /// </remarks>
    public int OpenHandleCount
    {
        get
        {
            int live = 0;
            foreach (SafeHandle handle in _issued)
            {
                if (!handle.IsClosed)
                {
                    live++;
                }
            }

            return live;
        }
    }

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> OpenAmbientDirectory(string path, CapAccess access)
    {
        if (!IsDirectoryAccess(access))
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        FakeNode? node = _fileSystem.Find(path);
        if (node is null)
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.NotFound));
        }

        return node.Type == CapNodeType.Directory
            ? CapResult<SafeDirHandle>.Ok(Register(node, access))
            : CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.NotADirectory));
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

        CapError error = ResolveChild(parent, name, out FakeNode? node);
        if (error.IsFailure)
        {
            return CapResult<SafeDirHandle>.Fail(error);
        }

        if (node!.Type == CapNodeType.SymbolicLink)
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.SymbolicLink));
        }

        if (node.Type == CapNodeType.UnknownReparsePoint)
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.Reparse));
        }

        return node.Type == CapNodeType.Directory
            ? CapResult<SafeDirHandle>.Ok(Register(node, access))
            : CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.NotADirectory));
    }

    /// <inheritdoc/>
    public CapResult<SafeFileHandle> OpenChildFile(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        in FileOpenRequest request)
    {
        CapError error = ResolveChild(parent, name, out FakeNode? node);

        if (error.Category == CapErrorCategory.NotFound && request.Creates)
        {
            return CreateChildFile(parent, name);
        }

        if (error.IsFailure)
        {
            return CapResult<SafeFileHandle>.Fail(error);
        }

        // A link is reported rather than followed even when the request would have created
        // the name. The name being taken by a link is the same fact whichever way the open
        // was asked for, and what to do about it is the caller's decision to make under the
        // caller's policy.
        if (node!.Type == CapNodeType.SymbolicLink)
        {
            return CapResult<SafeFileHandle>.Fail(CapError.FromCategory(CapErrorCategory.SymbolicLink));
        }

        if (node.Type == CapNodeType.Directory)
        {
            return CapResult<SafeFileHandle>.Fail(CapError.FromCategory(CapErrorCategory.IsADirectory));
        }

        return request.Mode == FileMode.CreateNew
            ? CapResult<SafeFileHandle>.Fail(CapError.FromCategory(CapErrorCategory.AlreadyExists))
            : CapResult<SafeFileHandle>.Ok(RegisterFile(node));
    }

    /// <summary>Adds a file to the simulation and hands back a handle on it.</summary>
    private CapResult<SafeFileHandle> CreateChildFile(SafeDirHandle parent, ReadOnlySpan<char> name)
    {
        CapError error = ResolveDirectory(parent, out FakeNode? directory);
        if (error.IsFailure)
        {
            return CapResult<SafeFileHandle>.Fail(error);
        }

        FakeNode created = new()
        {
            Type = CapNodeType.File,
            VolumeId = directory!.VolumeId,
            NodeId = _fileSystem.NextNodeId(),
        };

        directory.Entries[name.ToString()] = created;
        return CapResult<SafeFileHandle>.Ok(RegisterFile(created));
    }

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> OpenConfinedDirectory(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        CapAccess access,
        ConfinedResolveOptions options)
    {
        if (!IsDirectoryAccess(access))
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        CapError error = ResolveConfined(root, path, options, out FakeNode? node);
        if (error.IsFailure)
        {
            return CapResult<SafeDirHandle>.Fail(error);
        }

        return node!.Type == CapNodeType.Directory
            ? CapResult<SafeDirHandle>.Ok(Register(node, access))
            : CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.NotADirectory));
    }

    /// <inheritdoc/>
    public CapResult<SafeFileHandle> OpenConfinedFile(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        in FileOpenRequest request,
        ConfinedResolveOptions options)
    {
        CapError error = ResolveConfined(root, path, options, out FakeNode? node);
        if (error.IsFailure)
        {
            return CapResult<SafeFileHandle>.Fail(error);
        }

        return node!.Type == CapNodeType.Directory
            ? CapResult<SafeFileHandle>.Fail(CapError.FromCategory(CapErrorCategory.IsADirectory))
            : CapResult<SafeFileHandle>.Ok(RegisterFile(node));
    }

    /// <inheritdoc/>
    public CapResult<DirectoryReader> OpenDirectoryReader(SafeDirHandle directory)
    {
        if ((directory.Access & CapAccess.Read) == 0)
        {
            return CapResult<DirectoryReader>.Fail(CapError.FromCategory(CapErrorCategory.PermissionDenied));
        }

        CapError error = ResolveDirectory(directory, out FakeNode? node);
        return error.IsFailure
            ? CapResult<DirectoryReader>.Fail(error)
            : CapResult<DirectoryReader>.Ok(new FakeDirectoryReader(node!));
    }

    /// <inheritdoc/>
    public CapResult<string> ReadChildLink(SafeDirHandle parent, ReadOnlySpan<char> name)
    {
        CapError error = ResolveChild(parent, name, out FakeNode? node);
        if (error.IsFailure)
        {
            return CapResult<string>.Fail(error);
        }

        return node!.LinkTarget is { } target
            ? CapResult<string>.Ok(target)
            : CapResult<string>.Fail(CapError.FromCategory(CapErrorCategory.NotALink));
    }

    /// <inheritdoc/>
    public CapError StatChild(SafeDirHandle parent, ReadOnlySpan<char> name, out CapNodeInfo info)
    {
        info = default;
        CapError error = ResolveChild(parent, name, out FakeNode? node);
        if (error.IsFailure)
        {
            return error;
        }

        info = node!.Info;
        return CapError.Success;
    }

    /// <inheritdoc/>
    public CapError StatHandle(SafeDirHandle handle, out CapNodeInfo info)
    {
        info = default;
        if (!TryResolveHandle(handle, out FakeNode? node))
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        info = node!.Info;
        return CapError.Success;
    }

    /// <inheritdoc/>
    public CapError DescribeChild(SafeDirHandle parent, ReadOnlySpan<char> name, out CapNodeStat stat)
    {
        stat = default;
        CapError error = ResolveChild(parent, name, out FakeNode? node);
        if (error.IsFailure)
        {
            return error;
        }

        stat = node!.Stat;
        return CapError.Success;
    }

    /// <inheritdoc/>
    public CapError DescribeHandle(SafeHandle handle, out CapNodeStat stat)
    {
        stat = default;
        if (!TryResolveHandle(handle, out FakeNode? node))
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        stat = node!.Stat;
        return CapError.Success;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Refused, because the simulation has no ambient namespace for an answer to be a name
    /// in. Its nodes exist only relative to the root it was built with, so any path it
    /// produced would be a path in a filesystem that does not exist, and code that logged it
    /// would be logging fiction. Callers must cope with having no answer in any case: the
    /// question has no answer on a real host either when the process filesystem is missing.
    /// </remarks>
    public CapResult<string> GetHandlePath(SafeDirHandle handle)
    {
        if (!TryResolveHandle(handle, out _))
        {
            return CapResult<string>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        return CapResult<string>.Fail(CapError.FromCategory(CapErrorCategory.NotSupported));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Counted rather than performed. There is no storage behind the simulation for anything
    /// to be committed to, and what a test needs to know is whether the code under test asked
    /// — a durability promise is kept or broken by whether the call is made, so the count is
    /// the observable the assertions are written against.
    /// </remarks>
    public CapError SyncDirectory(SafeDirHandle directory)
    {
        if (!TryResolveHandle(directory, out _))
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        DirectorySyncs++;
        return CapError.Success;
    }

    /// <summary>How many times a directory has been asked to commit what it holds.</summary>
    public int DirectorySyncs { get; private set; }

    /// <inheritdoc/>
    /// <remarks>
    /// Applies whichever half of the value this simulated platform records, matching the rule
    /// the real implementations follow: a node created with a Unix mode takes a mode, and one
    /// created with attributes takes attributes. A value carrying the other platform's is
    /// refused here exactly as it would be there.
    /// </remarks>
    public CapError SetHandlePermissions(
        SafeHandle handle,
        UnixFileMode? unixMode,
        FileAttributes? windowsAttributes)
    {
        FakeNode? node;
        if (handle is SafeDirHandle directory)
        {
            if (!TryResolveHandle(directory, out node))
            {
                return CapError.FromCategory(CapErrorCategory.InvalidArgument);
            }
        }
        else if (handle.IsInvalid || handle.IsClosed ||
                 !_open.TryGetValue(handle.DangerousGetHandle(), out node))
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        if (node!.UnixMode is not null)
        {
            if (unixMode is not { } mode)
            {
                return CapError.FromCategory(CapErrorCategory.NotSupported);
            }

            node.UnixMode = mode;
            return CapError.Success;
        }

        if (windowsAttributes is not { } attributes)
        {
            return CapError.FromCategory(CapErrorCategory.NotSupported);
        }

        node.WindowsAttributes = attributes;
        return CapError.Success;
    }

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> DuplicateDirectory(SafeDirHandle handle)
    {
        if (!TryResolveHandle(handle, out FakeNode? node))
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        return CapResult<SafeDirHandle>.Ok(Register(node!, handle.Access));
    }

    /// <inheritdoc/>
    public CapResult<SafeFileHandle> DuplicateFile(SafeFileHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed || !_open.TryGetValue(handle.DangerousGetHandle(), out FakeNode? node))
        {
            return CapResult<SafeFileHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        return CapResult<SafeFileHandle>.Ok(RegisterFile(node));
    }

    /// <inheritdoc/>
    public CapError CreateChildDirectory(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        CreationVisibility visibility)
    {
        CapError error = ResolveDirectory(parent, out FakeNode? directory);
        if (error.IsFailure)
        {
            return error;
        }

        string entry = name.ToString();
        if (_fileSystem.Lookup(directory!, entry) is not null)
        {
            return CapError.FromCategory(CapErrorCategory.AlreadyExists);
        }

        directory!.Entries[entry] = new FakeNode
        {
            Type = CapNodeType.Directory,
            VolumeId = directory.VolumeId,
            NodeId = _fileSystem.NextNodeId(),
            UnixMode = visibility == CreationVisibility.OwnerOnly ? OwnerOnlyMode : SharedMode,
        };

        return CapError.Success;
    }

    /// <summary>What the simulation records for a directory only its owner may reach.</summary>
    private const UnixFileMode OwnerOnlyMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>What it records for one created with the usual permissions.</summary>
    private const UnixFileMode SharedMode =
        OwnerOnlyMode |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <inheritdoc/>
    public CapResult<string> GetSystemTemporaryDirectory() =>
        _fileSystem.TemporaryDirectory is { } location
            ? CapResult<string>.Ok(location)
            : CapResult<string>.Fail(CapError.FromCategory(CapErrorCategory.NotFound));

    /// <inheritdoc/>
    /// <remarks>
    /// Simulated only where a test asks for it, so that both answers — a platform that
    /// produces nameless files and one that does not — can be exercised on whichever machine
    /// the suite happens to be running on. The file is created with no entry in any
    /// directory, which is the property under test.
    /// </remarks>
    public CapResult<SafeFileHandle> OpenAnonymousChildFile(SafeDirHandle parent, FileAccess access)
    {
        if (!_fileSystem.SupportsAnonymousFiles)
        {
            return CapResult<SafeFileHandle>.Fail(CapError.FromCategory(CapErrorCategory.NotSupported));
        }

        if (access == FileAccess.Read)
        {
            return CapResult<SafeFileHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        CapError error = ResolveDirectory(parent, out FakeNode? directory);
        if (error.IsFailure)
        {
            return CapResult<SafeFileHandle>.Fail(error);
        }

        FakeNode created = new()
        {
            Type = CapNodeType.File,
            VolumeId = directory!.VolumeId,
            NodeId = _fileSystem.NextNodeId(),
        };

        return CapResult<SafeFileHandle>.Ok(RegisterFile(created));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Models the platform where a flag on the object can refuse its own removal. A node the
    /// test has not marked that way has nothing to clear, and the answer is the one the
    /// platforms without such a flag give.
    /// </remarks>
    public CapError ClearChildRemovalBlock(SafeDirHandle parent, ReadOnlySpan<char> name)
    {
        CapError error = ResolveChild(parent, name, out FakeNode? node);
        if (error.IsFailure)
        {
            return error;
        }

        if (!node!.RefusesRemoval)
        {
            return CapError.FromCategory(CapErrorCategory.NotSupported);
        }

        // An object the platform will not let anything change at all keeps its refusal, which
        // is how a test produces a name that genuinely cannot be removed.
        if (node.Unreadable)
        {
            return CapError.FromCategory(CapErrorCategory.PermissionDenied);
        }

        node.RefusesRemoval = false;
        return CapError.Success;
    }

    /// <inheritdoc/>
    public CapError RemoveChildFile(SafeDirHandle parent, ReadOnlySpan<char> name)
    {
        CapError error = ResolveChild(parent, name, out FakeNode? node);
        if (error.IsFailure)
        {
            return error;
        }

        if (node!.Type == CapNodeType.Directory)
        {
            return CapError.FromCategory(CapErrorCategory.IsADirectory);
        }

        if (node.RefusesRemoval)
        {
            return CapError.FromCategory(CapErrorCategory.PermissionDenied);
        }

        _ = Parent(parent).Entries.Remove(name.ToString());
        return CapError.Success;
    }

    /// <inheritdoc/>
    public CapError RemoveChildDirectory(SafeDirHandle parent, ReadOnlySpan<char> name)
    {
        CapError error = ResolveChild(parent, name, out FakeNode? node);
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

        if (node.RefusesRemoval)
        {
            return CapError.FromCategory(CapErrorCategory.PermissionDenied);
        }

        _ = Parent(parent).Entries.Remove(name.ToString());
        return CapError.Success;
    }

    /// <inheritdoc/>
    public CapError RenameChild(
        SafeDirHandle fromParent,
        ReadOnlySpan<char> fromName,
        SafeDirHandle toParent,
        ReadOnlySpan<char> toName,
        bool replaceExisting)
    {
        CapError error = ResolveChild(fromParent, fromName, out FakeNode? node);
        if (error.IsFailure)
        {
            return error;
        }

        error = ResolveDirectory(toParent, out FakeNode? destination);
        if (error.IsFailure)
        {
            return error;
        }

        if (destination!.VolumeId != node!.VolumeId)
        {
            return CapError.FromCategory(CapErrorCategory.CrossDevice);
        }

        string target = toName.ToString();
        if (!replaceExisting && _fileSystem.Lookup(destination, target) is not null)
        {
            return CapError.FromCategory(CapErrorCategory.AlreadyExists);
        }

        _ = Parent(fromParent).Entries.Remove(fromName.ToString());
        destination.Entries[target] = node;
        return CapError.Success;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The simulation records no kind for a link, as the Unix platforms do not, so the
    /// request for a directory link and the request for a file link produce the same node.
    /// </remarks>
    public CapError CreateChildSymbolicLink(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        ReadOnlySpan<char> target,
        bool targetIsDirectory)
    {
        CapError error = ResolveDirectory(parent, out FakeNode? directory);
        if (error.IsFailure)
        {
            return error;
        }

        string entry = name.ToString();
        if (_fileSystem.Lookup(directory!, entry) is not null)
        {
            return CapError.FromCategory(CapErrorCategory.AlreadyExists);
        }

        directory!.Entries[entry] = new FakeNode
        {
            Type = CapNodeType.SymbolicLink,
            VolumeId = directory.VolumeId,
            NodeId = _fileSystem.NextNodeId(),
            LinkTarget = target.ToString(),
        };

        return CapError.Success;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The destination entry is made to refer to the very same node, which is what a hard
    /// link is: one object with two names. A test can therefore tell a link from a copy by
    /// comparing identities, exactly as it would against a real filesystem.
    /// </remarks>
    public CapError CreateChildHardLink(
        SafeDirHandle parent,
        ReadOnlySpan<char> name,
        SafeDirHandle toParent,
        ReadOnlySpan<char> toName)
    {
        CapError error = ResolveChild(parent, name, out FakeNode? node);
        if (error.IsFailure)
        {
            return error;
        }

        error = ResolveDirectory(toParent, out FakeNode? destination);
        if (error.IsFailure)
        {
            return error;
        }

        string target = toName.ToString();
        if (_fileSystem.Lookup(destination!, target) is not null)
        {
            return CapError.FromCategory(CapErrorCategory.AlreadyExists);
        }

        destination!.Entries[target] = node!;
        return CapError.Success;
    }

    /// <summary>Resolves a handle to the directory it refers to.</summary>
    private CapError ResolveDirectory(SafeDirHandle handle, out FakeNode? directory)
    {
        directory = null;
        if (!TryResolveHandle(handle, out FakeNode? node))
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        if (node!.Type != CapNodeType.Directory)
        {
            return CapError.FromCategory(CapErrorCategory.NotADirectory);
        }

        if (node.Unreadable)
        {
            return CapError.FromCategory(CapErrorCategory.PermissionDenied);
        }

        directory = node;
        return CapError.Success;
    }

    /// <summary>
    /// The directory a handle refers to, for a caller that has already established it is one.
    /// </summary>
    private FakeNode Parent(SafeDirHandle handle) =>
        TryResolveHandle(handle, out FakeNode? node)
            ? node!
            : throw new InvalidOperationException("The handle was resolved a moment ago and is not now.");

    /// <summary>
    /// Whether the authority asked of a directory is one a directory can carry.
    /// </summary>
    /// <remarks>
    /// The simulation does not model permission bits, so the two it accepts behave alike
    /// here. It still refuses the third, because a caller asking to write a directory has
    /// made a mistake that every real implementation reports, and a simulation that accepted
    /// it would let that mistake pass unnoticed in exactly the tests written to catch it.
    /// </remarks>
    private static bool IsDirectoryAccess(CapAccess access) =>
        access is CapAccess.None or CapAccess.Read;

    private SafeDirHandle Register(FakeNode node, CapAccess access) =>
        Track(new SafeDirHandle(NextHandle(node), ownsHandle: false, access));

    private SafeFileHandle RegisterFile(FakeNode node) =>
        Track(new SafeFileHandle(NextHandle(node), ownsHandle: false));

    /// <summary>Remembers a handle so that its eventual closing can be observed.</summary>
    private T Track<T>(T handle)
        where T : SafeHandle
    {
        _issued.Add(handle);
        return handle;
    }

    private nint NextHandle(FakeNode node)
    {
        nint value = _nextHandle++;
        _open[value] = node;
        return value;
    }

    private bool TryResolveHandle(SafeHandle handle, out FakeNode? node)
    {
        node = null;
        if (handle.IsInvalid || handle.IsClosed)
        {
            return false;
        }

        return _open.TryGetValue(handle.DangerousGetHandle(), out node);
    }

    /// <summary>Resolves one name in the directory a handle refers to, without following links.</summary>
    private CapError ResolveChild(SafeDirHandle parent, ReadOnlySpan<char> name, out FakeNode? node)
    {
        node = null;
        if (!TryResolveHandle(parent, out FakeNode? directory))
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        if (directory!.Type != CapNodeType.Directory)
        {
            return CapError.FromCategory(CapErrorCategory.NotADirectory);
        }

        if (directory.Unreadable)
        {
            return CapError.FromCategory(CapErrorCategory.PermissionDenied);
        }

        node = _fileSystem.Lookup(directory, name.ToString());
        return node is null ? CapError.FromCategory(CapErrorCategory.NotFound) : CapError.Success;
    }

    /// <summary>
    /// Resolves a whole path, following links and refusing to leave the starting subtree.
    /// </summary>
    /// <remarks>
    /// Upward movement is modelled the way the kernel confines it: a step above the starting
    /// directory is refused outright rather than clamped, so a path that tries to escape is
    /// reported as an escape and not quietly turned into one that stays inside.
    /// </remarks>
    private CapError ResolveConfined(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        ConfinedResolveOptions options,
        out FakeNode? node)
    {
        node = null;
        _confinedOpenAttempts++;

        if (!_fileSystem.SupportsConfinedOpen)
        {
            return CapError.FromCategory(CapErrorCategory.NotSupported);
        }

        if (!TryResolveHandle(root, out FakeNode? start))
        {
            return CapError.FromCategory(CapErrorCategory.InvalidArgument);
        }

        return Descend(start!, path.ToString(), options, out node);
    }

    /// <summary>
    /// Resolves a path from a starting directory, following links and refusing to leave the
    /// subtree that directory roots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written as one loop over a queue of components rather than as a recursion, because a
    /// link's target has to be resolved in the same walk as the path that reached it. A
    /// recursion that started a fresh walk at the link would lose how far above the starting
    /// point resolution already was, and would report a link such as <c>../sibling</c> — from
    /// a subdirectory, and entirely inside the subtree — as an escape.
    /// </para>
    /// <para>
    /// A step above the starting directory is refused rather than clamped, so a path that
    /// tries to leave is reported as having tried, and not quietly rewritten into one that
    /// stays.
    /// </para>
    /// </remarks>
    private CapError Descend(
        FakeNode start,
        string path,
        ConfinedResolveOptions options,
        out FakeNode? node)
    {
        node = null;

        List<FakeNode> stack = [start];
        Queue<string> pending = new(path.Split('/', StringSplitOptions.RemoveEmptyEntries));
        int budget = LinkBudget;

        while (pending.Count > 0)
        {
            string component = pending.Dequeue();
            if (component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                if (stack.Count == 1)
                {
                    return CapError.FromCategory(CapErrorCategory.Escaped);
                }

                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            FakeNode current = stack[^1];
            if (current.Type != CapNodeType.Directory)
            {
                return CapError.FromCategory(CapErrorCategory.NotADirectory);
            }

            FakeNode? next = _fileSystem.Lookup(current, component);
            if (next is null)
            {
                return CapError.FromCategory(CapErrorCategory.NotFound);
            }

            if (next.Type == CapNodeType.UnknownReparsePoint)
            {
                return CapError.FromCategory(CapErrorCategory.Reparse);
            }

            if (next.Type == CapNodeType.SymbolicLink)
            {
                if ((options & ConfinedResolveOptions.RefuseSymlinks) != 0)
                {
                    return CapError.FromCategory(CapErrorCategory.SymbolicLinkLoop);
                }

                if (--budget < 0)
                {
                    return CapError.FromCategory(CapErrorCategory.SymbolicLinkLoop);
                }

                string target = next.LinkTarget ?? string.Empty;
                if (target.StartsWith('/'))
                {
                    return CapError.FromCategory(CapErrorCategory.Escaped);
                }

                // The target's components are resolved before whatever was left of the
                // original path, from the directory the link lives in.
                string[] remainder = [.. pending];
                pending.Clear();
                foreach (string part in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    pending.Enqueue(part);
                }

                foreach (string part in remainder)
                {
                    pending.Enqueue(part);
                }

                continue;
            }

            if ((options & ConfinedResolveOptions.RefuseMountCrossing) != 0 &&
                next.VolumeId != current.VolumeId)
            {
                return CapError.FromCategory(CapErrorCategory.CrossDevice);
            }

            stack.Add(next);
        }

        node = stack[^1];
        return CapError.Success;
    }
}
