namespace Cap.Directories;

/// <summary>Which platform's rules decide where an application's directories go.</summary>
internal enum DirectoryConvention
{
    /// <summary>The XDG Base Directory specification, followed by Linux and the other Unixes.</summary>
    Xdg,

    /// <summary>The per-user <c>Library</c> folders of macOS.</summary>
    Apple,

    /// <summary>The roaming and local application-data known folders of Windows.</summary>
    Windows,
}

/// <summary>
/// The ambient facts the directory layout is computed from: which convention applies, the
/// environment variables, the home directory and the platform's known folders.
/// </summary>
/// <remarks>
/// Gathered behind one type so that the layout can be computed from a description of a host
/// rather than from the process it happens to run in. That is what lets the rules for every
/// platform, and for every combination of variables set and unset, be checked on any one of
/// them without changing the environment of the process doing the checking.
/// </remarks>
internal sealed class HostEnvironment
{
    private readonly Func<string, string?> _variable;
    private readonly Func<string?> _home;
    private readonly Func<Environment.SpecialFolder, string?> _knownFolder;

    internal HostEnvironment(
        DirectoryConvention convention,
        Func<string, string?> variable,
        Func<string?> home,
        Func<Environment.SpecialFolder, string?> knownFolder)
    {
        Convention = convention;
        _variable = variable;
        _home = home;
        _knownFolder = knownFolder;
    }

    /// <summary>The host this process is running on.</summary>
    internal static HostEnvironment Current { get; } = new(
        OperatingSystem.IsWindows() ? DirectoryConvention.Windows
            : OperatingSystem.IsMacOS() ? DirectoryConvention.Apple
            : DirectoryConvention.Xdg,
        Environment.GetEnvironmentVariable,
        // The runtime's answer rather than HOME alone: it reads HOME first, as every other
        // program does, and falls back to the account database when HOME is unset.
        () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        folder => Environment.GetFolderPath(folder));

    /// <summary>Which rules apply.</summary>
    internal DirectoryConvention Convention { get; }

    /// <summary>An environment variable, or null when it is unset or empty.</summary>
    /// <remarks>
    /// Empty is folded into unset because the XDG specification treats the two alike, and
    /// no other variable read here gives an empty value a meaning of its own.
    /// </remarks>
    internal string? Variable(string name) => NullIfEmpty(_variable(name));

    /// <summary>The current account's home directory, or null when there is none.</summary>
    internal string? Home => NullIfEmpty(_home());

    /// <summary>One of the platform's known folders, or null when it does not exist.</summary>
    internal string? KnownFolder(Environment.SpecialFolder folder) => NullIfEmpty(_knownFolder(folder));

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
