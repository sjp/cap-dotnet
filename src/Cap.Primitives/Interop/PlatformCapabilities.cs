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
    /// <summary>Creates a capability description.</summary>
    public PlatformCapabilities(ResolutionBackend backend, bool overlappedFileHandles)
    {
        Backend = backend;
        SupportsOverlappedFileHandles = overlappedFileHandles;
    }

    /// <summary>The backend resolution will use.</summary>
    public ResolutionBackend Backend { get; }

    /// <summary>
    /// True when the platform can resolve a whole multi-component path in one confined,
    /// kernel-atomic operation, and the confined-open members of
    /// <see cref="IPlatformOps"/> may be called.
    /// </summary>
    public bool SupportsConfinedOpen => Backend == ResolutionBackend.ConfinedOpen;

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
