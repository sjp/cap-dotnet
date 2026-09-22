using Cap.Primitives;
using Cap.Std;

namespace Cap.Directories.Tests;

/// <summary>
/// Finding, creating and handing out an application's directories against a real
/// filesystem, with the host's environment described rather than read.
/// </summary>
/// <remarks>
/// <para>
/// Every test points the XDG variables and the home directory into a throwaway tree, so
/// nothing here touches the real account's directories and nothing depends on how the
/// machine running the suite is set up.
/// </para>
/// <para>
/// The properties that carry the weight are that nothing is created until it is asked for,
/// that what is created is closed to other accounts while what already existed is left
/// alone, that the runtime directory is used only when it really is private, and that once
/// the locations have been found nothing is reached by path again.
/// </para>
/// </remarks>
public sealed class ProjectDirsTests : IDisposable
{
    private const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode Open755 =
        OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly string _root = Directory.CreateTempSubdirectory("capdirs-").FullName;
    private readonly Dictionary<string, string?> _variables = [];

    public ProjectDirsTests()
    {
        Directory.CreateDirectory(HomePath);
    }

    private string HomePath => Path.Join(_root, "home");

    public void Dispose()
    {
        foreach (string directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(directory, OwnerOnly);
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    private ProjectDirs Find(string application = "My App") =>
        ProjectDirs.FromHost(
            "org",
            "Example",
            application,
            AmbientAuthority.Acquire(),
            SymlinkPolicy.FollowWithinSandbox,
            new HostEnvironment(
                DirectoryConvention.Xdg,
                name => _variables.GetValueOrDefault(name),
                () => HomePath,
                _ => null));

    private static string HostPath(Dir dir)
    {
        Assert.True(dir.TryGetPath(AmbientAuthority.Acquire(), out string? path));
        return path!;
    }

    private const string NoModeBits = "This platform records no mode bits, and decides access another way.";

    /// <summary>A default value is not a token, and finds nobody any directories.</summary>
    [Fact]
    public void Finding_the_directories_requires_an_acquired_token()
    {
        Assert.Throws<ArgumentException>(
            "authority",
            () => ProjectDirs.From("org", "Example", "App", default));
    }

    [Fact]
    public void An_undefined_symlink_policy_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            "policy",
            () => ProjectDirs.From("org", "Example", "App", AmbientAuthority.Acquire(), (SymlinkPolicy)99));
    }

    [Fact]
    public void Finding_the_directories_creates_none_of_them()
    {
        using ProjectDirs dirs = Find();

        Assert.Empty(Directory.EnumerateFileSystemEntries(HomePath));
    }

    [Fact]
    public void The_first_request_creates_the_directory_and_its_missing_parents()
    {
        using ProjectDirs dirs = Find();
        using Dir data = dirs.OpenData();

        string expected = Path.Join(HomePath, ".local", "share", "myapp");
        Assert.True(Directory.Exists(expected));
        Assert.Equal(new FileInfo(expected).FullName, new FileInfo(HostPath(data)).FullName);

        // Only the kind that was asked for.
        Assert.False(Directory.Exists(Path.Join(HomePath, ".config")));
        Assert.False(Directory.Exists(Path.Join(HomePath, ".cache")));
        Assert.False(Directory.Exists(Path.Join(HomePath, ".local", "state")));
    }

    [Fact]
    public void Every_directory_created_is_closed_to_other_accounts()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(NoModeBits);
            return;
        }

        using ProjectDirs dirs = Find();
        dirs.OpenState()!.Dispose();

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(Path.Join(HomePath, ".local")));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(Path.Join(HomePath, ".local", "state")));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(Path.Join(HomePath, ".local", "state", "myapp")));
    }

    [Fact]
    public void A_directory_that_already_exists_keeps_its_permissions()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(NoModeBits);
            return;
        }

        string config = Path.Join(HomePath, ".config");
        string application = Path.Join(config, "myapp");
        Directory.CreateDirectory(application);
        File.SetUnixFileMode(config, Open755);
        File.SetUnixFileMode(application, Open755);

        using ProjectDirs dirs = Find();
        dirs.OpenConfig().Dispose();

        Assert.Equal(Open755, File.GetUnixFileMode(config));
        Assert.Equal(Open755, File.GetUnixFileMode(application));
    }

    [Fact]
    public void A_set_xdg_variable_places_the_directory_beneath_it()
    {
        string cache = Path.Join(_root, "elsewhere", "cache");
        _variables["XDG_CACHE_HOME"] = cache;

        using ProjectDirs dirs = Find();
        dirs.OpenCache().Dispose();

        Assert.True(Directory.Exists(Path.Join(cache, "myapp")));
        Assert.False(Directory.Exists(Path.Join(HomePath, ".cache")));
    }

    /// <summary>
    /// A relative variable would be resolved against whatever directory the process started
    /// in, so the default is used instead and nothing lands beside the working directory.
    /// </summary>
    [Fact]
    public void A_relative_xdg_variable_is_ignored_on_disk()
    {
        _variables["XDG_CONFIG_HOME"] = "relative-config";

        using ProjectDirs dirs = Find();
        dirs.OpenConfig().Dispose();

        Assert.True(Directory.Exists(Path.Join(HomePath, ".config", "myapp")));
    }

    /// <summary>
    /// A configuration directory kept elsewhere and linked into place is how dotfiles are
    /// commonly managed, and the ambient step follows the link the way any program would.
    /// </summary>
    [Fact]
    public void A_linked_base_directory_is_followed()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Making a symbolic link needs a privilege this test does not assume.");
            return;
        }

        string dotfiles = Path.Join(_root, "dotfiles");
        Directory.CreateDirectory(dotfiles);
        Directory.CreateSymbolicLink(Path.Join(HomePath, ".config"), dotfiles);

        using ProjectDirs dirs = Find();
        dirs.OpenConfig().Dispose();

        Assert.True(Directory.Exists(Path.Join(dotfiles, "myapp")));
    }

    /// <summary>
    /// The locations are resolved once. Renaming the part that existed when they were found
    /// does not redirect the creation to whatever now holds the old name.
    /// </summary>
    [Fact]
    public void Nothing_is_resolved_by_path_after_the_directories_are_found()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A directory cannot be renamed here while a handle on it is open.");
            return;
        }

        string config = Path.Join(HomePath, ".config");
        Directory.CreateDirectory(config);

        using ProjectDirs dirs = Find();

        string moved = Path.Join(_root, "moved");
        Directory.Move(config, moved);
        Directory.CreateDirectory(config);

        dirs.OpenConfig().Dispose();

        Assert.True(Directory.Exists(Path.Join(moved, "myapp")));
        Assert.False(Directory.Exists(Path.Join(config, "myapp")));
    }

    [Fact]
    public void The_handles_handed_out_are_confined_to_the_directory()
    {
        using ProjectDirs dirs = Find();
        using Dir config = dirs.OpenConfig();

        Assert.Throws<SandboxEscapeException>(() => config.OpenDir(".."));
        Assert.Equal(SymlinkPolicy.FollowWithinSandbox, config.SymlinkPolicy);
    }

    [Fact]
    public void Each_request_is_a_separate_handle_the_caller_owns()
    {
        using ProjectDirs dirs = Find();

        Dir first = dirs.OpenCache();
        using Dir second = dirs.OpenCache();
        Assert.NotSame(first, second);

        first.Dispose();
        second.CreateDir("still-usable").Dispose();

        Assert.True(Directory.Exists(Path.Join(HomePath, ".cache", "myapp", "still-usable")));
    }

    [Fact]
    public void Disposing_leaves_handles_already_handed_out_open_and_refuses_new_requests()
    {
        ProjectDirs dirs = Find();
        using Dir config = dirs.OpenConfig();

        dirs.Dispose();
        dirs.Dispose();

        config.CreateDir("after").Dispose();
        Assert.Throws<ObjectDisposedException>(() => dirs.OpenConfig());
        Assert.Throws<ObjectDisposedException>(() => dirs.OpenData());
        Assert.Throws<ObjectDisposedException>(() => dirs.OpenCache());
        Assert.Throws<ObjectDisposedException>(() => dirs.OpenState());
        Assert.Throws<ObjectDisposedException>(() => dirs.OpenRuntime());
    }

    /// <summary>A name held by a file cannot become a directory, and the request says so.</summary>
    [Fact]
    public void A_file_in_the_way_is_reported_when_the_directory_is_requested()
    {
        File.WriteAllText(Path.Join(HomePath, ".cache"), "not a directory");

        using ProjectDirs dirs = Find();

        Assert.Throws<CapIOException>(() => dirs.OpenCache());
    }

    [Fact]
    public void Concurrent_first_requests_all_succeed_on_one_directory()
    {
        using ProjectDirs dirs = Find();

        Dir[] handles = new Dir[16];
        Parallel.For(0, handles.Length, i => handles[i] = dirs.OpenState()!);

        try
        {
            CapMetadata first = handles[0].GetMetadata();
            Assert.All(handles, handle => Assert.True(handle.GetMetadata().IsSameFileAs(first)));
        }
        finally
        {
            foreach (Dir handle in handles)
            {
                handle.Dispose();
            }
        }
    }

    [Fact]
    public void There_is_no_runtime_directory_when_the_session_names_none()
    {
        using ProjectDirs dirs = Find();

        Assert.Null(dirs.OpenRuntime());
    }

    [Fact]
    public void There_is_no_runtime_directory_when_the_one_named_is_missing()
    {
        _variables["XDG_RUNTIME_DIR"] = Path.Join(_root, "no-such-runtime");

        using ProjectDirs dirs = Find();

        Assert.Null(dirs.OpenRuntime());
        Assert.False(Directory.Exists(Path.Join(_root, "no-such-runtime")));
    }

    [Fact]
    public void A_private_runtime_directory_is_used()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(NoModeBits);
            return;
        }

        string runtime = Path.Join(_root, "runtime");
        Directory.CreateDirectory(runtime);
        File.SetUnixFileMode(runtime, OwnerOnly);
        _variables["XDG_RUNTIME_DIR"] = runtime;

        using ProjectDirs dirs = Find();
        using Dir? dir = dirs.OpenRuntime();

        Assert.NotNull(dir);
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(Path.Join(runtime, "myapp")));
    }

    /// <summary>
    /// What goes in a runtime directory — a socket, a lock — is trusted because nobody else
    /// can reach it, so a directory other accounts can read is not used at all.
    /// </summary>
    [Fact]
    public void A_runtime_directory_open_to_other_accounts_is_not_used()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(NoModeBits);
            return;
        }

        string runtime = Path.Join(_root, "runtime");
        Directory.CreateDirectory(runtime);
        File.SetUnixFileMode(runtime, Open755);
        _variables["XDG_RUNTIME_DIR"] = runtime;

        using ProjectDirs dirs = Find();

        Assert.Null(dirs.OpenRuntime());
        Assert.False(Directory.Exists(Path.Join(runtime, "myapp")));
    }

    [Fact]
    public void A_runtime_variable_naming_a_file_is_not_used()
    {
        string runtime = Path.Join(_root, "runtime-file");
        File.WriteAllText(runtime, "");
        _variables["XDG_RUNTIME_DIR"] = runtime;

        using ProjectDirs dirs = Find();

        Assert.Null(dirs.OpenRuntime());
    }

    [Theory]
    [InlineData(CapFileType.Directory, 1000u, OwnerOnly, true)]
    [InlineData(CapFileType.Directory, 1000u, OwnerOnly | UnixFileMode.StickyBit, true)]
    [InlineData(CapFileType.Directory, 1000u, OwnerOnly | UnixFileMode.SetGroup, true)]
    [InlineData(CapFileType.Directory, 0u, OwnerOnly, false)]
    [InlineData(CapFileType.Directory, 1000u, OwnerOnly | UnixFileMode.GroupRead, false)]
    [InlineData(CapFileType.Directory, 1000u, OwnerOnly | UnixFileMode.OtherExecute, false)]
    [InlineData(CapFileType.Directory, 1000u, UnixFileMode.UserRead | UnixFileMode.UserExecute, false)]
    [InlineData(CapFileType.File, 1000u, OwnerOnly, false)]
    public void A_runtime_directory_must_be_owned_by_the_user_and_closed_to_everybody_else(
        CapFileType type, uint owner, UnixFileMode mode, bool expected)
    {
        Assert.Equal(expected, ProjectDirs.IsPrivateTo(type, owner, mode, user: 1000));
    }

    [Fact]
    public void A_directory_whose_owner_or_mode_is_unknown_is_not_private()
    {
        Assert.False(ProjectDirs.IsPrivateTo(CapFileType.Directory, null, OwnerOnly, user: 1000));
        Assert.False(ProjectDirs.IsPrivateTo(CapFileType.Directory, 1000, null, user: 1000));
    }
}
