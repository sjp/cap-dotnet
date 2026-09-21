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
    public PlatformCapabilities(ResolutionBackend backend)
    {
        Backend = backend;
    }

    /// <summary>The backend resolution will use.</summary>
    public ResolutionBackend Backend { get; }

    /// <summary>
    /// True when the platform can resolve a whole multi-component path in one confined,
    /// kernel-atomic operation, and the confined-open members of
    /// <see cref="IPlatformOps"/> may be called.
    /// </summary>
    public bool SupportsConfinedOpen => Backend == ResolutionBackend.ConfinedOpen;
}
