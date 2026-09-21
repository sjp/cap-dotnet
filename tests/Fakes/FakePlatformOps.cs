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
    private nint _nextHandle = FirstHandleValue;
    private long _confinedOpenAttempts;

    /// <summary>How many times a link may be followed before resolution gives up.</summary>
    private const int LinkBudget = 8;

    public FakePlatformOps(FakeFileSystem fileSystem) => _fileSystem = fileSystem;

    /// <inheritdoc/>
    public PlatformCapabilities Capabilities => new(
        _fileSystem.SupportsConfinedOpen ? ResolutionBackend.ConfinedOpen : ResolutionBackend.PortableWalk);

    /// <inheritdoc/>
    public long ConfinedOpenAttempts => _confinedOpenAttempts;

    /// <summary>How many handles this instance has produced and not seen released.</summary>
    public int OpenHandleCount => _open.Count;

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> OpenAmbientDirectory(string path)
    {
        FakeNode? node = _fileSystem.Find(path);
        if (node is null)
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.NotFound));
        }

        return node.Type == CapNodeType.Directory
            ? CapResult<SafeDirHandle>.Ok(Register(node))
            : CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.NotADirectory));
    }

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> OpenChildDirectory(SafeDirHandle parent, ReadOnlySpan<char> name)
    {
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
            ? CapResult<SafeDirHandle>.Ok(Register(node))
            : CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.NotADirectory));
    }

    /// <inheritdoc/>
    public CapResult<SafeFileHandle> OpenChildFile(SafeDirHandle parent, ReadOnlySpan<char> name, CapAccess access)
    {
        CapError error = ResolveChild(parent, name, out FakeNode? node);
        if (error.IsFailure)
        {
            return CapResult<SafeFileHandle>.Fail(error);
        }

        if (node!.Type == CapNodeType.SymbolicLink)
        {
            return CapResult<SafeFileHandle>.Fail(CapError.FromCategory(CapErrorCategory.SymbolicLink));
        }

        if (node.Type == CapNodeType.Directory)
        {
            return CapResult<SafeFileHandle>.Fail(CapError.FromCategory(CapErrorCategory.IsADirectory));
        }

        return CapResult<SafeFileHandle>.Ok(new SafeFileHandle(NextHandle(node), ownsHandle: false));
    }

    /// <inheritdoc/>
    public CapResult<SafeDirHandle> OpenConfinedDirectory(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        ConfinedResolveOptions options)
    {
        CapError error = ResolveConfined(root, path, options, out FakeNode? node);
        if (error.IsFailure)
        {
            return CapResult<SafeDirHandle>.Fail(error);
        }

        return node!.Type == CapNodeType.Directory
            ? CapResult<SafeDirHandle>.Ok(Register(node))
            : CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.NotADirectory));
    }

    /// <inheritdoc/>
    public CapResult<SafeFileHandle> OpenConfinedFile(
        SafeDirHandle root,
        ReadOnlySpan<char> path,
        CapAccess access,
        ConfinedResolveOptions options)
    {
        CapError error = ResolveConfined(root, path, options, out FakeNode? node);
        if (error.IsFailure)
        {
            return CapResult<SafeFileHandle>.Fail(error);
        }

        return node!.Type == CapNodeType.Directory
            ? CapResult<SafeFileHandle>.Fail(CapError.FromCategory(CapErrorCategory.IsADirectory))
            : CapResult<SafeFileHandle>.Ok(new SafeFileHandle(NextHandle(node), ownsHandle: false));
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
            : CapResult<string>.Fail(CapError.FromCategory(CapErrorCategory.NotSupported));
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
    public CapResult<SafeDirHandle> DuplicateDirectory(SafeDirHandle handle)
    {
        if (!TryResolveHandle(handle, out FakeNode? node))
        {
            return CapResult<SafeDirHandle>.Fail(CapError.FromCategory(CapErrorCategory.InvalidArgument));
        }

        return CapResult<SafeDirHandle>.Ok(Register(node!));
    }

    private SafeDirHandle Register(FakeNode node) => new(NextHandle(node), ownsHandle: false);

    private nint NextHandle(FakeNode node)
    {
        nint value = _nextHandle++;
        _open[value] = node;
        return value;
    }

    private bool TryResolveHandle(SafeDirHandle handle, out FakeNode? node)
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
