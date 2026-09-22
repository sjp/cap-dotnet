using Cap.Primitives;
using Cap.Primitives.Interop.Unix;
using Cap.Std;

namespace Cap.Directories;

/// <summary>
/// An application's configuration, data, cache, state and runtime directories, found by
/// the platform's conventions and handed out as <see cref="Dir"/> capabilities.
/// </summary>
/// <remarks>
/// <para>
/// Most programs need a handful of directories whose locations are decided by the platform
/// rather than by the program, and the usual way to reach them is to build a path at start-up
/// and keep joining strings onto it for the rest of the run. This type is the capability
/// shaped version of that: the locations are worked out from the environment exactly once,
/// by <see cref="From"/>, under an explicit <see cref="AmbientAuthority"/> token, and from
/// then on each directory is reached through a handle. Nothing here returns a path, and
/// nothing after <see cref="From"/> resolves one.
/// </para>
/// <para>
/// <strong>Where the directories are.</strong> The layout matches the Rust
/// <c>directories</c> crate, so the same application finds the same directories whichever it
/// is written against:
/// </para>
/// <list type="table">
/// <listheader><term>Kind</term><description>Linux and other Unixes · macOS · Windows</description></listheader>
/// <item><term>Config</term><description>
/// <c>$XDG_CONFIG_HOME/app</c> or <c>~/.config/app</c> ·
/// <c>~/Library/Application Support/qualifier.Org.App</c> ·
/// <c>%APPDATA%\Org\App\config</c></description></item>
/// <item><term>Data</term><description>
/// <c>$XDG_DATA_HOME/app</c> or <c>~/.local/share/app</c> ·
/// <c>~/Library/Application Support/qualifier.Org.App</c> ·
/// <c>%APPDATA%\Org\App\data</c></description></item>
/// <item><term>Cache</term><description>
/// <c>$XDG_CACHE_HOME/app</c> or <c>~/.cache/app</c> ·
/// <c>~/Library/Caches/qualifier.Org.App</c> ·
/// <c>%LOCALAPPDATA%\Org\App\cache</c></description></item>
/// <item><term>State</term><description>
/// <c>$XDG_STATE_HOME/app</c> or <c>~/.local/state/app</c> · none · none</description></item>
/// <item><term>Runtime</term><description>
/// <c>$XDG_RUNTIME_DIR/app</c>, when that directory is set and private · none · none</description></item>
/// </list>
/// <para>
/// On the XDG side the application's directory is its name lowercased with the whitespace
/// removed, and an XDG variable that is unset, empty or not an absolute path is ignored in
/// favour of the default, as the specification requires. On macOS the qualifier,
/// organization and application are joined into a bundle identifier, with whitespace inside
/// each replaced by hyphens and empty parts left out.
/// </para>
/// <para>
/// <strong>Nothing is created until it is asked for.</strong> <see cref="From"/> opens, for
/// each kind, the nearest part of its location that already exists, and holds that handle.
/// The first request for a kind creates whatever is missing beneath it, one component at a
/// time through the handle, so an application that never asks for a cache never gets one.
/// Because the missing part is created beneath a handle rather than by path, a location that
/// is renamed or replaced after <see cref="From"/> returns does not redirect the creation.
/// </para>
/// <para>
/// <strong>Permissions.</strong> On Unix every directory this type creates — the
/// application's own and any missing parent such as <c>~/.config</c> — is created with mode
/// <c>0700</c>, as the XDG specification asks. A directory that already exists is used as it
/// is, whatever its mode: it was somebody's decision, and quietly changing it would be a
/// surprise rather than a defence.
/// </para>
/// <para>
/// <strong>The runtime directory is checked, not assumed.</strong> The session's
/// <c>XDG_RUNTIME_DIR</c> is meant to be owned by the user and closed to everybody else,
/// and what goes in it — sockets, locks — relies on that. It is used only if the directory
/// it names, as opened, is a directory owned by the current account with no permissions for
/// anybody else; otherwise, or when the variable is unset, there is no runtime directory and
/// <see cref="OpenRuntime"/> returns null. The check is made on the open handle, so the
/// directory checked is the directory used.
/// </para>
/// <para>
/// <strong>Thread safety.</strong> Instances are safe for concurrent use. Two threads
/// asking for the same kind for the first time create it once between them.
/// </para>
/// </remarks>
public sealed class ProjectDirs : IDisposable
{
    private readonly Slot _config;
    private readonly Slot _data;
    private readonly Slot _cache;
    private readonly Slot? _state;
    private readonly Slot? _runtime;

    private ProjectDirs(Slot config, Slot data, Slot cache, Slot? state, Slot? runtime)
    {
        _config = config;
        _data = data;
        _cache = cache;
        _state = state;
        _runtime = runtime;
    }

    /// <summary>
    /// Finds an application's directories on this host.
    /// </summary>
    /// <param name="qualifier">
    /// The top of the application's reverse-domain name, such as <c>com</c> or <c>org</c>.
    /// Used only on macOS. May be empty.
    /// </param>
    /// <param name="organization">
    /// The organization that makes the application. Used on macOS and Windows. May be empty.
    /// </param>
    /// <param name="application">The application's name. Must not be empty.</param>
    /// <param name="authority">
    /// Proof that taking authority from outside the capability graph is intended here. Must
    /// come from <see cref="AmbientAuthority.Acquire"/>; a default value is refused.
    /// </param>
    /// <param name="policy">
    /// What resolution beneath the returned handles does with a symbolic link it meets on the
    /// way to the thing a path names. Travels with every handle this hands out.
    /// </param>
    /// <returns>The directories, none of which has been created yet.</returns>
    /// <remarks>
    /// <para>
    /// This is the ambient step, and the only one. It reads the environment, and opens the
    /// deepest existing part of each location by an ordinary path, following links the way
    /// any program would — so a <c>~/.config</c> that is a link into a dotfiles checkout is
    /// followed, as the user intended. Everything after it is reached through the handles
    /// it opened.
    /// </para>
    /// <para>
    /// The names become directory names, so a separator or a NUL in any of them is refused
    /// rather than letting the application's directory land somewhere else.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">A name is null.</exception>
    /// <exception cref="ArgumentException">
    /// A name cannot be used in a directory name, the application name is empty, or
    /// <paramref name="authority"/> was never acquired.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="policy"/> is not a value the enumeration defines.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">
    /// There is no home directory, or known folder, to put the directories under, or a
    /// location names something that can neither be opened nor created.
    /// </exception>
    public static ProjectDirs From(
        string qualifier,
        string organization,
        string application,
        AmbientAuthority authority,
        SymlinkPolicy policy = SymlinkPolicy.FollowWithinSandbox) =>
        FromHost(qualifier, organization, application, authority, policy, HostEnvironment.Current);

    /// <summary>
    /// Finds an application's directories on a described host, for testing the conventions
    /// without changing the environment of the process under test.
    /// </summary>
    internal static ProjectDirs FromHost(
        string qualifier,
        string organization,
        string application,
        AmbientAuthority authority,
        SymlinkPolicy policy,
        HostEnvironment host)
    {
        authority.Demand(nameof(authority));
        if (!policy.IsDefinedValue())
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy,
                "The symbolic-link policy is not one of the defined values.");
        }

        ProjectLayout layout = ProjectLayout.Resolve(qualifier, organization, application, host);

        List<Slot> opened = [];
        try
        {
            Slot Open(ProjectLocation location)
            {
                Slot slot = OpenNearest(location, authority, policy);
                opened.Add(slot);
                return slot;
            }

            Slot config = Open(layout.Config);
            Slot data = Open(layout.Data);
            Slot cache = Open(layout.Cache);
            Slot? state = layout.State is null ? null : Open(layout.State);
            Slot? runtime = layout.Runtime is null ? null : OpenRuntimeBase(layout.Runtime, authority, policy);

            return new ProjectDirs(config, data, cache, state, runtime);
        }
        catch
        {
            foreach (Slot slot in opened)
            {
                slot.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// The application's configuration directory, created if it is not there.
    /// </summary>
    /// <returns>
    /// A new handle on the directory, which the caller owns and must dispose. Every call
    /// returns a separate handle, so disposing one does not affect any other.
    /// </returns>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the creation or the open.</exception>
    /// <exception cref="CapIOException">
    /// Something that is not a directory holds one of the names, or the creation failed
    /// otherwise.
    /// </exception>
    /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
    public Dir OpenConfig() => _config.Open(this);

    /// <summary>
    /// The application's data directory, created if it is not there.
    /// </summary>
    /// <returns>A new handle on the directory, which the caller owns. See <see cref="OpenConfig"/>.</returns>
    /// <remarks>
    /// On macOS this is the same directory as the configuration directory, because the
    /// platform keeps both in one place.
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the creation or the open.</exception>
    /// <exception cref="CapIOException">The directory could not be created or opened.</exception>
    /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
    public Dir OpenData() => _data.Open(this);

    /// <summary>
    /// The application's cache directory, created if it is not there.
    /// </summary>
    /// <returns>A new handle on the directory, which the caller owns. See <see cref="OpenConfig"/>.</returns>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the creation or the open.</exception>
    /// <exception cref="CapIOException">The directory could not be created or opened.</exception>
    /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
    public Dir OpenCache() => _cache.Open(this);

    /// <summary>
    /// The application's state directory, created if it is not there, or null on a platform
    /// whose conventions have no such directory.
    /// </summary>
    /// <returns>
    /// A new handle on the directory, which the caller owns, or null on macOS and Windows.
    /// </returns>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the creation or the open.</exception>
    /// <exception cref="CapIOException">The directory could not be created or opened.</exception>
    /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
    public Dir? OpenState() => _state?.Open(this) ?? ThrowIfDisposed();

    /// <summary>
    /// The application's runtime directory, created if it is not there, or null when the
    /// session provides no usable runtime directory.
    /// </summary>
    /// <returns>
    /// A new handle on the directory, which the caller owns, or null on macOS and Windows,
    /// when <c>XDG_RUNTIME_DIR</c> is unset or not absolute, or when the directory it names
    /// is not owned by the current account or is open to anybody else.
    /// </returns>
    /// <remarks>
    /// Null is an ordinary answer, and the caller decides what to do without one: put its
    /// socket in the state directory, say, or run without it. There is no fallback chosen
    /// here, because none of the candidates shares the property that makes a runtime
    /// directory worth having — that it is removed when the session ends.
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">The filesystem refused the creation or the open.</exception>
    /// <exception cref="CapIOException">The directory could not be created or opened.</exception>
    /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
    public Dir? OpenRuntime() => _runtime?.Open(this) ?? ThrowIfDisposed();

    /// <summary>
    /// Closes the handles this instance holds.
    /// </summary>
    /// <remarks>
    /// Handles already returned by the <c>Open</c> methods are the caller's and stay open.
    /// Disposing twice does nothing the second time.
    /// </remarks>
    public void Dispose()
    {
        _config.Dispose();
        _data.Dispose();
        _cache.Dispose();
        _state?.Dispose();
        _runtime?.Dispose();
    }

    /// <summary>
    /// Whether a directory as described is private to the account this process acts as.
    /// </summary>
    /// <remarks>
    /// The XDG specification requires the runtime directory to be owned by the user with
    /// mode <c>0700</c>. Only the permission bits are compared, so a set-group-id or sticky
    /// bit does not disqualify a directory that is otherwise closed to everybody else.
    /// </remarks>
    internal static bool IsPrivateTo(CapFileType type, uint? owner, UnixFileMode? mode, uint user)
    {
        const UnixFileMode PermissionBits =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        return type == CapFileType.Directory
            && owner == user
            && mode is { } bits
            && (bits & PermissionBits) == OwnerOnly;
    }

    /// <summary>
    /// Opens the deepest part of a location that exists, and notes what is missing beneath
    /// it.
    /// </summary>
    /// <remarks>
    /// Each step up drops the last component of the path as written. That is lexical, which
    /// is wrong in general under symbolic links, but here it decides only where creation
    /// starts: each candidate is opened as the host resolves it, and whatever turns out to be
    /// missing beneath the one that opens is created through its handle. A component that is
    /// <c>.</c> or <c>..</c> cannot be created as a directory, so a location that would need
    /// one created is refused.
    /// </remarks>
    private static Slot OpenNearest(ProjectLocation location, AmbientAuthority authority, SymlinkPolicy policy)
    {
        string full = location.FullPath;
        string candidate = full;
        Stack<string> missing = new();

        while (true)
        {
            if (Dir.TryOpen(candidate, authority, out Dir? found, policy))
            {
                return new Slot(found, [.. missing]);
            }

            string trimmed = Path.TrimEndingDirectorySeparator(candidate);
            string? parent = Path.GetDirectoryName(trimmed);
            string name = Path.GetFileName(trimmed);

            if (parent is null || name is "" or "." or "..")
            {
                throw new DirectoryNotFoundException(
                    $"Neither '{full}' nor any directory above it could be opened, so there is " +
                    "nowhere to create it from.");
            }

            missing.Push(name);
            candidate = parent;
        }
    }

    /// <summary>
    /// Opens the session's runtime directory, if it is there and private, and notes the
    /// application's directory beneath it.
    /// </summary>
    /// <remarks>
    /// Unlike the other kinds nothing above the base is ever created: the runtime directory
    /// is the session's to provide, and one that does not exist is one that was not provided.
    /// </remarks>
    private static Slot? OpenRuntimeBase(ProjectLocation location, AmbientAuthority authority, SymlinkPolicy policy)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return null;
        }

        if (!Dir.TryOpen(location.Base, authority, out Dir? runtime, policy))
        {
            return null;
        }

        bool usable;
        try
        {
            CapMetadata metadata = runtime.GetMetadata();
            usable = IsPrivateTo(
                metadata.Type,
                metadata.UnixOwnerId,
                metadata.Permissions.TryGetUnixMode(out UnixFileMode mode) ? mode : null,
                UnixProcessIdentity.EffectiveUserId);
        }
        catch (IOException)
        {
            usable = false;
        }
        catch (UnauthorizedAccessException)
        {
            usable = false;
        }

        if (!usable)
        {
            runtime.Dispose();
            return null;
        }

        return new Slot(runtime, [.. location.Components]);
    }

    /// <summary>Answers null for an absent kind, unless this instance has been disposed.</summary>
    private Dir? ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_config.IsDisposed, this);
        return null;
    }

    /// <summary>
    /// One kind of directory: a handle on the deepest part of its location that existed,
    /// the names still to be created beneath it, and the directory once it has been.
    /// </summary>
    private sealed class Slot(Dir anchor, string[] missing) : IDisposable
    {
        private readonly Lock _gate = new();
        private Dir? _anchor = anchor;
        private Dir? _directory;
        private bool _disposed;

        internal bool IsDisposed
        {
            get
            {
                lock (_gate)
                {
                    return _disposed;
                }
            }
        }

        /// <summary>
        /// Hands out a new handle on the directory, creating what is missing the first time.
        /// </summary>
        /// <remarks>
        /// If a creation fails part-way, what was created stays, the anchor is kept, and the
        /// next call starts again from it — finding the part already made and carrying on.
        /// </remarks>
        internal Dir Open(ProjectDirs owner)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, owner);

                if (_directory is null)
                {
                    Dir current = _anchor!;
                    try
                    {
                        foreach (string name in missing)
                        {
                            Dir next = current.OpenOrCreateOwnedDir(name);
                            if (!ReferenceEquals(current, _anchor))
                            {
                                current.Dispose();
                            }

                            current = next;
                        }
                    }
                    catch
                    {
                        if (!ReferenceEquals(current, _anchor))
                        {
                            current.Dispose();
                        }

                        throw;
                    }

                    _directory = current;
                    if (!ReferenceEquals(current, _anchor))
                    {
                        _anchor!.Dispose();
                    }

                    _anchor = null;
                }

                return _directory.Clone();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _anchor?.Dispose();
                _directory?.Dispose();
                _anchor = null;
                _directory = null;
            }
        }
    }
}
