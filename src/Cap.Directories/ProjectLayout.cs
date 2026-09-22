namespace Cap.Directories;

/// <summary>
/// Where one of an application's directories belongs: a base the platform names, and the
/// components beneath it that name the application.
/// </summary>
/// <param name="Base">
/// An absolute path taken from the environment or the platform — the home directory, an XDG
/// variable, a known folder.
/// </param>
/// <param name="Components">Single names, beneath <paramref name="Base"/>, in order.</param>
/// <remarks>
/// The two halves are kept apart because they are trusted differently. The base came from
/// outside and is resolved the way any program resolves a path; the components were derived
/// here from the application's name, and are always created and opened one at a time,
/// through a handle, beneath whatever the base turned out to be.
/// </remarks>
internal sealed record ProjectLocation(string Base, IReadOnlyList<string> Components)
{
    /// <summary>The location as one path, for opening it and for messages.</summary>
    internal string FullPath => Components.Aggregate(Base, Path.Join);
}

/// <summary>
/// Where each of an application's directories belongs on a given host.
/// </summary>
/// <remarks>
/// <para>
/// Computing this involves no filesystem access at all: it is the part of finding the
/// directories that is purely a matter of convention, kept separate so that the conventions
/// can be checked against a described host rather than the real one.
/// </para>
/// <para>
/// The layout is the one the Rust <c>directories</c> crate uses, so an application written
/// against either finds the same directories. On Linux and the other Unixes it follows the
/// XDG Base Directory specification, naming the application's directory after the
/// application alone, lowercased with its whitespace removed. On macOS the directories sit in
/// the per-user <c>Library</c> folders under a reverse-domain bundle identifier built from
/// all three names. On Windows they sit under the organization and application in the
/// roaming and local application-data folders, with the kind of directory as a last
/// component, because configuration and data share a parent there.
/// </para>
/// <para>
/// State and runtime directories exist only where the convention defines them, which is on
/// the XDG side alone. Elsewhere they are absent rather than invented.
/// </para>
/// </remarks>
internal sealed class ProjectLayout
{
    private ProjectLayout(
        ProjectLocation config,
        ProjectLocation data,
        ProjectLocation cache,
        ProjectLocation? state,
        ProjectLocation? runtime)
    {
        Config = config;
        Data = data;
        Cache = cache;
        State = state;
        Runtime = runtime;
    }

    /// <summary>Configuration: what a user edits, or the application writes on their behalf.</summary>
    internal ProjectLocation Config { get; }

    /// <summary>Data: what the application keeps and a user would want kept.</summary>
    internal ProjectLocation Data { get; }

    /// <summary>Cache: what the application could rebuild if it were deleted.</summary>
    internal ProjectLocation Cache { get; }

    /// <summary>State: what should survive a restart but is not worth keeping or moving.</summary>
    internal ProjectLocation? State { get; }

    /// <summary>
    /// Runtime: sockets and other objects that live only as long as the user's session.
    /// </summary>
    /// <remarks>
    /// The base is used only after it has been checked to be private to the current account,
    /// which this type cannot do because it does not touch the filesystem.
    /// </remarks>
    internal ProjectLocation? Runtime { get; }

    /// <summary>Works out where an application's directories belong on a host.</summary>
    /// <exception cref="ArgumentNullException">A name is null.</exception>
    /// <exception cref="ArgumentException">A name cannot be used as, or in, a directory name.</exception>
    /// <exception cref="DirectoryNotFoundException">
    /// The host has no home directory, or no known folder, for a base that needs one.
    /// </exception>
    internal static ProjectLayout Resolve(
        string qualifier,
        string organization,
        string application,
        HostEnvironment host)
    {
        ArgumentNullException.ThrowIfNull(qualifier);
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(host);

        RefuseSeparators(qualifier, nameof(qualifier));
        RefuseSeparators(organization, nameof(organization));
        RefuseSeparators(application, nameof(application));

        return host.Convention switch
        {
            DirectoryConvention.Xdg => ResolveXdg(application, host),
            DirectoryConvention.Apple => ResolveApple(qualifier, organization, application, host),
            DirectoryConvention.Windows => ResolveWindows(organization, application, host),
            _ => throw new PlatformNotSupportedException(),
        };
    }

    /// <summary>
    /// XDG: each kind has a variable naming its base, and a default beneath the home
    /// directory for when the variable is unset, empty, or not absolute.
    /// </summary>
    /// <remarks>
    /// A relative value is ignored rather than resolved, because the specification says so:
    /// it would otherwise be resolved against whatever directory the process was started in,
    /// which is not a place anybody meant to configure. There is no default for the runtime
    /// directory, whose whole point is that the session provides it.
    /// </remarks>
    private static ProjectLayout ResolveXdg(string application, HostEnvironment host)
    {
        string name = Checked(Squash(application, separator: ""), nameof(application));
        string[] components = [name];

        ProjectLocation At(string variable, string fallbackBeneathHome) =>
            new(AbsoluteVariable(host, variable) ?? Path.Join(RequireHome(host), fallbackBeneathHome), components);

        string? runtime = AbsoluteVariable(host, "XDG_RUNTIME_DIR");

        return new ProjectLayout(
            config: At("XDG_CONFIG_HOME", ".config"),
            data: At("XDG_DATA_HOME", Path.Join(".local", "share")),
            cache: At("XDG_CACHE_HOME", ".cache"),
            state: At("XDG_STATE_HOME", Path.Join(".local", "state")),
            runtime: runtime is null ? null : new ProjectLocation(runtime, components));
    }

    /// <summary>
    /// macOS: <c>~/Library/Application Support</c> for configuration and data alike, and
    /// <c>~/Library/Caches</c>, each under a bundle identifier.
    /// </summary>
    private static ProjectLayout ResolveApple(
        string qualifier,
        string organization,
        string application,
        HostEnvironment host)
    {
        if (Hyphenate(application).Length == 0)
        {
            throw EmptyApplication();
        }

        string bundle = Checked(
            string.Join(
                '.',
                new[] { qualifier, organization, application }
                    .Select(Hyphenate)
                    .Where(part => part.Length > 0)),
            nameof(application));

        string library = Path.Join(RequireHome(host), "Library");
        ProjectLocation support = new(Path.Join(library, "Application Support"), [bundle]);

        return new ProjectLayout(
            config: support,
            data: support,
            cache: new ProjectLocation(Path.Join(library, "Caches"), [bundle]),
            state: null,
            runtime: null);
    }

    /// <summary>
    /// Windows: the roaming application-data folder for configuration and data, the local
    /// one for the cache, each under the organization and application.
    /// </summary>
    /// <remarks>
    /// The names are used as given. An empty organization is left out rather than turned
    /// into an empty component.
    /// </remarks>
    private static ProjectLayout ResolveWindows(string organization, string application, HostEnvironment host)
    {
        if (application.Length == 0)
        {
            throw EmptyApplication();
        }

        string[] project = organization.Length == 0
            ? [Checked(application, nameof(application))]
            : [Checked(organization, nameof(organization)), Checked(application, nameof(application))];

        string roaming = RequireKnownFolder(host, Environment.SpecialFolder.ApplicationData);
        string local = RequireKnownFolder(host, Environment.SpecialFolder.LocalApplicationData);

        return new ProjectLayout(
            config: new ProjectLocation(roaming, [.. project, "config"]),
            data: new ProjectLocation(roaming, [.. project, "data"]),
            cache: new ProjectLocation(local, [.. project, "cache"]),
            state: null,
            runtime: null);
    }

    /// <summary>A variable's value, provided it is an absolute path.</summary>
    private static string? AbsoluteVariable(HostEnvironment host, string name) =>
        host.Variable(name) is { } value && Path.IsPathFullyQualified(value) ? value : null;

    private static string RequireHome(HostEnvironment host) =>
        host.Home is { } home && Path.IsPathFullyQualified(home)
            ? home
            : throw new DirectoryNotFoundException(
                "This account has no home directory, so there is nowhere to put an " +
                "application's directories that the environment does not name itself.");

    private static string RequireKnownFolder(HostEnvironment host, Environment.SpecialFolder folder) =>
        host.KnownFolder(folder) is { } path && Path.IsPathFullyQualified(path)
            ? path
            : throw new DirectoryNotFoundException(
                $"This account has no {folder} folder to put an application's directories in.");

    /// <summary>
    /// Refuses a name that would reach past the single component it is meant to become.
    /// </summary>
    /// <remarks>
    /// Both separators are refused on every platform, so that a name accepted on one is not
    /// read as a path on another. A NUL would end the name early wherever it reaches the
    /// operating system as a C string.
    /// </remarks>
    private static void RefuseSeparators(string value, string parameterName)
    {
        if (value.AsSpan().IndexOfAny('/', '\\', '\0') >= 0)
        {
            throw new ArgumentException(
                "A project name becomes part of a directory name, so it cannot contain a " +
                "path separator or a NUL character.",
                parameterName);
        }
    }

    /// <summary>Refuses a derived component that is empty or names a directory's own links.</summary>
    private static string Checked(string component, string parameterName) => component switch
    {
        "" => throw EmptyApplication(),
        "." or ".." => throw new ArgumentException(
            $"The project name would become the directory name '{component}', which names " +
            "an existing directory rather than a new one.",
            parameterName),
        _ => component,
    };

    private static ArgumentException EmptyApplication() => new(
        "The application name is empty once its whitespace is set aside, and an application's " +
        "directories are named after it.",
        "application");

    /// <summary>
    /// Trims, lowercases each whitespace-separated word and joins the words with
    /// <paramref name="separator"/>.
    /// </summary>
    private static string Squash(string name, string separator) =>
        string.Join(
            separator,
            name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.ToLowerInvariant()));

    /// <summary>Trims and joins the whitespace-separated words with hyphens, keeping case.</summary>
    private static string Hyphenate(string name) =>
        string.Join('-', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
