namespace Cap.Primitives.Interop;

/// <summary>
/// What the running platform can actually do, as answered once and then cached.
/// </summary>
/// <remarks>
/// Probing is not free and must not be repeated per call: on a kernel that lacks the
/// confined open, or one where a sandbox filter has removed it, asking again on every
/// resolution would be a syscall storm in exactly the deployments least able to absorb one.
/// The answer is therefore settled at first use and does not change for the life of the
/// process.
/// </remarks>
internal readonly struct PlatformCapabilities
{
    /// <summary>Creates a capability description for a backend over the host.</summary>
    /// <remarks>
    /// The confined open is offered exactly when it is the backend's name for itself, and
    /// paths are read under the running platform's rules, because a backend over the host
    /// resolves them through the host's kernel.
    /// </remarks>
    public PlatformCapabilities(ResolutionBackend backend, bool overlappedFileHandles)
        : this(backend, overlappedFileHandles, backend == ResolutionBackend.ConfinedOpen, CapPath.HostSyntax)
    {
    }

    /// <summary>Creates a capability description for a backend that says everything itself.</summary>
    /// <param name="backend">What the backend reports itself as.</param>
    /// <param name="overlappedFileHandles">Whether its file handles can be overlapped.</param>
    /// <param name="confinedOpen">Whether its confined-open members may be called.</param>
    /// <param name="pathSyntax">The rules a path beneath one of its handles is read under.</param>
    /// <remarks>
    /// For a filesystem that is not the host's, which chooses the answers the host would
    /// otherwise impose. An in-memory filesystem reports itself as in memory whichever way it
    /// resolves, and can be told to read paths as Windows does on a machine that is not
    /// Windows, so that code bound for one platform can be tested on another.
    /// </remarks>
    public PlatformCapabilities(
        ResolutionBackend backend,
        bool overlappedFileHandles,
        bool confinedOpen,
        CapPathSyntax pathSyntax)
    {
        Backend = backend;
        SupportsOverlappedFileHandles = overlappedFileHandles;
        SupportsConfinedOpen = confinedOpen;
        PathSyntax = pathSyntax;
    }

    /// <summary>The backend resolution will use.</summary>
    public ResolutionBackend Backend { get; }

    /// <summary>
    /// True when the platform can resolve a whole multi-component path in one confined,
    /// kernel-atomic operation, and the confined-open members of
    /// <see cref="IPlatformOps"/> may be called.
    /// </summary>
    public bool SupportsConfinedOpen { get; }

    /// <summary>
    /// The rules a path handed to a handle from this backend is read under: which characters
    /// separate components, and which names are refused.
    /// </summary>
    /// <remarks>
    /// The running platform's for every backend over the host. A path is parsed before any
    /// backend sees it, so this has to be settled by the backend rather than by the parser:
    /// a name that one platform's rules accept and another's refuse would otherwise be
    /// accepted by the parser and then mean something different to the filesystem.
    /// </remarks>
    public CapPathSyntax PathSyntax { get; }

    /// <summary>
    /// True when a file handle can be opened so that the operating system itself completes
    /// reads and writes without a thread waiting on them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows alone, where whether a handle works that way is decided when it is opened and
    /// cannot be changed afterwards — which is why it is asked for at the open and carried
    /// with the handle rather than chosen per call. Asking for the wrong one is not an error
    /// anybody notices: the operations still work, and every one of them blocks a thread that
    /// was supposed to be doing something else.
    /// </para>
    /// <para>
    /// Everywhere else this is false, and asking for such a handle changes nothing rather
    /// than failing. A read of a file on those systems is a call that returns when the data
    /// is there; what an asynchronous file API can offer is to make some other thread wait,
    /// which is what the framework already does and is not a property of the handle.
    /// </para>
    /// </remarks>
    public bool SupportsOverlappedFileHandles { get; }
}
