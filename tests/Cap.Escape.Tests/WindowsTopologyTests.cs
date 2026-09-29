using Cap.Primitives;
using Cap.Std;

namespace Cap.Escape.Tests;

/// <summary>
/// Windows attacks whose names only the volume, or the running system, can supply.
/// </summary>
[Collection(CorpusGroup.Name)]
public sealed class WindowsTopologyTests
{
    /// <summary>
    /// The generated short alias of a long name does not reach the entry it aliases.
    /// </summary>
    /// <remarks>
    /// Not an escape — the alias names an entry beneath the same handle — but a rule stated
    /// about one spelling of a name is defeated by the other, so an alias is refused. The alias
    /// is whatever the volume generated, which is why this is not a row of the case table.
    /// </remarks>
    [Fact]
    [Defends("W6")]
    [NotInMemory("The alias is one the host's volume generates.")]
    public void A_short_name_alias_does_not_reach_the_entry_it_aliases()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Short-name aliases exist only on Windows volumes.");
            return;
        }

        const string LongName = "a long directory name";
        using Arena arena = new();
        arena.Plant([new(SetupKind.File, $"{LongName}/{EscapeCorpus.PlainFile}")]);

        string? alias = HostFilesystem.ShortNameOf(Path.Join(arena.SandboxPath, LongName));
        if (alias is null || alias.Equals(LongName, StringComparison.OrdinalIgnoreCase))
        {
            // A host that is supposed to generate them says so, and there the absence of an
            // alias is a broken set-up rather than a configuration to accommodate.
            Assert.False(
                Environment.GetEnvironmentVariable("CAPDOTNET_EXPECT_SHORT_NAMES") == "1",
                $"No short name was generated for '{LongName}', but this host was set up to generate them.");
            Assert.Skip("This volume does not generate short names.");
        }

        using BackendScope scope = Backends.Enter(Backends.Windows);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire());

        Assert.Equal(EscapeCorpus.InsideContent, root.ReadAllText($"{LongName}/{EscapeCorpus.PlainFile}"));
        Assert.IsType<CapIOException>(Assert.ThrowsAny<IOException>(() => root.OpenDir(alias)), exactMatch: true);
        Assert.IsType<CapIOException>(
            Assert.ThrowsAny<IOException>(() => root.ReadAllText($"{alias}/{EscapeCorpus.PlainFile}")),
            exactMatch: true);
    }

    /// <summary>
    /// An application execution alias — a reparse point that is not a filesystem link, and
    /// that redirects nothing on the filesystem — is never read as a link, and failing to open
    /// it is not reported as a way out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The alias is a stub only the process launcher reads; no filter on the volume serves
    /// it. So it is described as the file it is, opening it fails because nothing can serve
    /// its contents, and reading it as a link yields no target. None of those is an escape,
    /// and reporting one would put noise into the one log worth reading closely.
    /// </para>
    /// <para>
    /// Such aliases cannot be created without the store's installer, so the ones the system
    /// already has are used where the running account has any.
    /// </para>
    /// </remarks>
    [Fact]
    [Defends("S18")]
    [NotInMemory("The alias is one the host's system installed.")]
    public void An_application_execution_alias_is_never_read_as_a_link_nor_reported_as_an_escape()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Application execution aliases exist only on Windows.");
            return;
        }

        string aliases = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        string? alias = Directory.Exists(aliases)
            ? new DirectoryInfo(aliases).EnumerateFiles("*.exe")
                .FirstOrDefault(file => (file.Attributes & FileAttributes.ReparsePoint) != 0)?.Name
            : null;

        if (alias is null)
        {
            Assert.Skip($"This account has no application execution aliases in '{aliases}'.");
        }

        using BackendScope scope = Backends.Enter(Backends.Windows);
        using Dir root = Dir.Open(aliases, AmbientAuthority.Acquire());

        Assert.Equal(CapFileType.File, root.GetMetadata(alias).Type);

        IOException opened = Assert.ThrowsAny<IOException>(() => root.OpenFile(alias).Dispose());
        Assert.Equal(CapErrorKind.NotSupported, CapIOException.KindOf(opened));

        IOException entered = Assert.ThrowsAny<IOException>(() => root.OpenDir(alias).Dispose());
        Assert.NotEqual(CapErrorKind.Escaped, CapIOException.KindOf(entered));

        IOException read = Assert.ThrowsAny<IOException>(() => root.ReadLink(alias));
        Assert.Equal(CapErrorKind.NotALink, CapIOException.KindOf(read));
    }

    /// <summary>
    /// A root named by a path that reaches a device, rather than a directory, is not opened.
    /// </summary>
    [Theory]
    [InlineData(@"\\.\NUL")]
    [InlineData(@"\\.\CON")]
    [InlineData("NUL")]
    [InlineData("CONIN$")]
    [Defends("W8")]
    [NotInMemory("About the host's own device paths.")]
    public void A_root_named_by_a_path_to_a_device_is_not_opened(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Device paths are a Windows construct.");
            return;
        }

        using BackendScope scope = Backends.Enter(Backends.Windows);

        Assert.False(
            Dir.TryOpen(path, AmbientAuthority.Acquire(), out Dir? opened),
            $"'{path}' was opened as a directory.");
        Assert.Null(opened);
    }
}
