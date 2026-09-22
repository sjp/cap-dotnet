namespace Cap.Directories.Tests;

/// <summary>
/// Where each platform's conventions put an application's directories, checked against
/// described hosts so that every platform, and every variable both set and unset, is covered
/// wherever the suite runs.
/// </summary>
public sealed class ProjectLayoutTests
{
    /// <summary>A filesystem root, so the described hosts use paths this platform calls absolute.</summary>
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\" : "/";

    private static readonly string Home = P("home", "alice");

    /// <summary>An absolute path beneath <see cref="Root"/>.</summary>
    private static string P(params string[] parts) => Path.Join([Root, .. parts]);

    private static HostEnvironment Xdg(Dictionary<string, string?> variables) => Xdg(variables, Home);

    private static HostEnvironment Xdg(Dictionary<string, string?> variables, string? home) =>
        new(DirectoryConvention.Xdg, name => variables.GetValueOrDefault(name), () => home, _ => null);

    private static ProjectLayout ResolveXdg(Dictionary<string, string?> variables) => ResolveXdg(variables, Home);

    private static ProjectLayout ResolveXdg(Dictionary<string, string?> variables, string? home) =>
        ProjectLayout.Resolve("org", "Example Corp", "My App", Xdg(variables, home));

    public static TheoryData<string, string> XdgDefaults => new()
    {
        { "XDG_CONFIG_HOME", P("home", "alice", ".config", "myapp") },
        { "XDG_DATA_HOME", P("home", "alice", ".local", "share", "myapp") },
        { "XDG_CACHE_HOME", P("home", "alice", ".cache", "myapp") },
        { "XDG_STATE_HOME", P("home", "alice", ".local", "state", "myapp") },
    };

    private static string? Kind(ProjectLayout layout, string variable) => variable switch
    {
        "XDG_CONFIG_HOME" => layout.Config.FullPath,
        "XDG_DATA_HOME" => layout.Data.FullPath,
        "XDG_CACHE_HOME" => layout.Cache.FullPath,
        "XDG_STATE_HOME" => layout.State?.FullPath,
        "XDG_RUNTIME_DIR" => layout.Runtime?.FullPath,
        _ => throw new ArgumentOutOfRangeException(nameof(variable)),
    };

    [Theory]
    [MemberData(nameof(XdgDefaults))]
    public void An_unset_xdg_variable_falls_back_to_its_default_beneath_home(string variable, string expected)
    {
        Assert.Equal(expected, Kind(ResolveXdg([]), variable));
    }

    [Theory]
    [MemberData(nameof(XdgDefaults))]
    public void A_set_xdg_variable_replaces_the_default(string variable, string _)
    {
        ProjectLayout layout = ResolveXdg(new() { [variable] = P("srv", "elsewhere") });

        Assert.Equal(P("srv", "elsewhere", "myapp"), Kind(layout, variable));
    }

    /// <summary>The specification treats an empty variable the same as an unset one.</summary>
    [Theory]
    [MemberData(nameof(XdgDefaults))]
    public void An_empty_xdg_variable_falls_back_to_the_default(string variable, string expected)
    {
        Assert.Equal(expected, Kind(ResolveXdg(new() { [variable] = "" }), variable));
    }

    /// <summary>
    /// A relative value would be resolved against the directory the process started in,
    /// which the specification rules out by requiring it to be ignored.
    /// </summary>
    [Theory]
    [MemberData(nameof(XdgDefaults))]
    public void A_relative_xdg_variable_is_ignored(string variable, string expected)
    {
        Assert.Equal(expected, Kind(ResolveXdg(new() { [variable] = "relative/dir" }), variable));
    }

    [Fact]
    public void Every_xdg_variable_set_needs_no_home_directory()
    {
        ProjectLayout layout = ResolveXdg(
            new()
            {
                ["XDG_CONFIG_HOME"] = P("c"),
                ["XDG_DATA_HOME"] = P("d"),
                ["XDG_CACHE_HOME"] = P("k"),
                ["XDG_STATE_HOME"] = P("s"),
            },
            null);

        Assert.Equal(P("c", "myapp"), layout.Config.FullPath);
        Assert.Equal(P("s", "myapp"), layout.State!.FullPath);
    }

    [Fact]
    public void A_default_that_needs_a_home_directory_fails_without_one()
    {
        Assert.Throws<DirectoryNotFoundException>(() => ResolveXdg([], null));
        Assert.Throws<DirectoryNotFoundException>(() => ResolveXdg([], "relative/home"));
    }

    [Fact]
    public void The_runtime_directory_is_the_session_variable_alone()
    {
        Assert.Null(ResolveXdg([]).Runtime);
        Assert.Null(ResolveXdg(new() { ["XDG_RUNTIME_DIR"] = "" }).Runtime);
        Assert.Null(ResolveXdg(new() { ["XDG_RUNTIME_DIR"] = "run/user/1000" }).Runtime);
        Assert.Equal(
            P("run", "user", "1000", "myapp"),
            ResolveXdg(new() { ["XDG_RUNTIME_DIR"] = P("run", "user", "1000") }).Runtime!.FullPath);
    }

    /// <summary>The base and the application's own component are kept apart.</summary>
    [Fact]
    public void The_application_component_is_separate_from_the_base()
    {
        ProjectLocation config = ResolveXdg([]).Config;

        Assert.Equal(P("home", "alice", ".config"), config.Base);
        Assert.Equal(["myapp"], config.Components);
    }

    [Theory]
    [InlineData("My App", "myapp")]
    [InlineData("  Tabs\tand  Spaces ", "tabsandspaces")]
    [InlineData("already-fine", "already-fine")]
    public void The_xdg_directory_is_the_application_lowercased_without_whitespace(string application, string expected)
    {
        ProjectLayout layout = ProjectLayout.Resolve("", "", application, Xdg([]));

        Assert.Equal([expected], layout.Config.Components);
    }

    [Fact]
    public void Apple_uses_the_library_folders_under_a_bundle_identifier()
    {
        HostEnvironment host = new(DirectoryConvention.Apple, _ => null, () => P("Users", "alice"), _ => null);

        ProjectLayout layout = ProjectLayout.Resolve("com", "Example Corp", "My App", host);

        Assert.Equal(
            P("Users", "alice", "Library", "Application Support", "com.Example-Corp.My-App"),
            layout.Config.FullPath);
        Assert.Equal(layout.Config.FullPath, layout.Data.FullPath);
        Assert.Equal(P("Users", "alice", "Library", "Caches", "com.Example-Corp.My-App"), layout.Cache.FullPath);
        Assert.Null(layout.State);
        Assert.Null(layout.Runtime);
    }

    /// <summary>The XDG variables mean nothing on macOS, even when something has set them.</summary>
    [Fact]
    public void Apple_ignores_the_xdg_variables()
    {
        HostEnvironment host = new(DirectoryConvention.Apple, _ => P("srv", "xdg"), () => P("Users", "alice"), _ => null);

        ProjectLayout layout = ProjectLayout.Resolve("com", "Example", "App", host);

        Assert.Equal(P("Users", "alice", "Library", "Application Support"), layout.Config.Base);
        Assert.Null(layout.Runtime);
    }

    [Fact]
    public void Apple_leaves_empty_parts_out_of_the_bundle_identifier()
    {
        HostEnvironment host = new(DirectoryConvention.Apple, _ => null, () => P("Users", "alice"), _ => null);

        Assert.Equal(["App"], ProjectLayout.Resolve("", " ", "App", host).Cache.Components);
        Assert.Equal(["Org.App"], ProjectLayout.Resolve("", "Org", "App", host).Cache.Components);
    }

    [Fact]
    public void Windows_uses_the_roaming_and_local_folders_under_organization_and_application()
    {
        HostEnvironment host = WindowsHost();

        ProjectLayout layout = ProjectLayout.Resolve("com", "Example Corp", "My App", host);

        Assert.Equal(P("roaming"), layout.Config.Base);
        Assert.Equal(["Example Corp", "My App", "config"], layout.Config.Components);
        Assert.Equal(P("roaming"), layout.Data.Base);
        Assert.Equal(["Example Corp", "My App", "data"], layout.Data.Components);
        Assert.Equal(P("local"), layout.Cache.Base);
        Assert.Equal(["Example Corp", "My App", "cache"], layout.Cache.Components);
        Assert.Null(layout.State);
        Assert.Null(layout.Runtime);
    }

    [Fact]
    public void Windows_leaves_out_an_empty_organization()
    {
        ProjectLayout layout = ProjectLayout.Resolve("", "", "App", WindowsHost());

        Assert.Equal(["App", "config"], layout.Config.Components);
    }

    [Fact]
    public void Windows_fails_without_its_known_folders()
    {
        HostEnvironment host = new(DirectoryConvention.Windows, _ => null, () => null, _ => "");

        Assert.Throws<DirectoryNotFoundException>(() => ProjectLayout.Resolve("", "Org", "App", host));
    }

    /// <summary>
    /// Both separators are refused on every platform, so a name accepted on one is never a
    /// path on another.
    /// </summary>
    [Theory]
    [InlineData("com", "Org", "../escape", "application")]
    [InlineData("com", "Org", "a/b", "application")]
    [InlineData("com", "Org", "a\\b", "application")]
    [InlineData("com", "Org", "a\0b", "application")]
    [InlineData("com", "Org/Sub", "App", "organization")]
    [InlineData("co/m", "Org", "App", "qualifier")]
    public void A_name_that_is_not_a_single_component_is_refused(
        string qualifier, string organization, string application, string parameter)
    {
        foreach (HostEnvironment host in AllConventions())
        {
            Assert.Throws<ArgumentException>(
                parameter, () => ProjectLayout.Resolve(qualifier, organization, application, host));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData(".")]
    public void An_application_name_that_names_no_new_directory_is_refused(string name)
    {
        Assert.Throws<ArgumentException>("application", () => ProjectLayout.Resolve("", "", name, Xdg([])));
    }

    [Fact]
    public void A_null_name_is_refused()
    {
        Assert.Throws<ArgumentNullException>("application", () => ProjectLayout.Resolve("", "", null!, Xdg([])));
    }

    private static HostEnvironment WindowsHost() =>
        new(
            DirectoryConvention.Windows,
            _ => null,
            () => null,
            folder => folder switch
            {
                Environment.SpecialFolder.ApplicationData => P("roaming"),
                Environment.SpecialFolder.LocalApplicationData => P("local"),
                _ => null,
            });

    private static IEnumerable<HostEnvironment> AllConventions() =>
    [
        Xdg([]),
        new(DirectoryConvention.Apple, _ => null, () => Home, _ => null),
        WindowsHost(),
    ];
}
