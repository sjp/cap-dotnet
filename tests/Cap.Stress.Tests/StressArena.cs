using Cap.Primitives;
using Cap.Std;

namespace Cap.Stress.Tests;

/// <summary>
/// The ground a race is fought over: a sandbox, a directory beside it that nothing inside may
/// reach, and the identities of what is in each.
/// </summary>
/// <remarks>
/// <para>
/// The directory outside holds a file and a directory with the same names as the ones the races
/// put inside, so that a resolution steered out of the sandbox finds something there to open
/// rather than failing for want of it. An escape that only ever met a missing name would look
/// exactly like a refusal.
/// </para>
/// <para>
/// Identities are taken by opening a path with the process's own authority, before the attacker
/// starts or for objects the attacker has not yet put where a race can reach them. The library
/// is used only to read the identity of an open handle, which is a single system call on the
/// handle and involves no resolution at all.
/// </para>
/// </remarks>
internal sealed class StressArena : IDisposable
{
    /// <summary>The name of the directory the races put inside the sandbox and plant outside it.</summary>
    public const string DirectoryName = "sub";

    /// <summary>The name of the file the races put inside each directory, inside and outside.</summary>
    public const string FileName = "f";

    /// <summary>What every file outside the sandbox holds, so that a copy of one is recognised.</summary>
    public const string OutsideContent = "outside the sandbox: must never be read or written from within it";

    private readonly ScratchTree _scratch;
    private readonly HashSet<CapFileId> _outside = [];

    public StressArena()
    {
        _scratch = new ScratchTree(Location);
        HostPath = _scratch.HostPath;
        SandboxPath = Path.Join(HostPath, "sandbox");
        OutsidePath = Path.Join(HostPath, "outside");

        Directory.CreateDirectory(SandboxPath);
        Directory.CreateDirectory(Path.Join(OutsidePath, DirectoryName));
        File.WriteAllText(Path.Join(OutsidePath, FileName), OutsideContent);
        File.WriteAllText(Path.Join(OutsidePath, DirectoryName, FileName), OutsideContent);

        foreach (string path in Directory.EnumerateFileSystemEntries(OutsidePath, "*", SearchOption.AllDirectories).Append(OutsidePath))
        {
            _outside.Add(IdentityOf(path));
        }

        SupportsSymbolicLinks = HostOps.CanCreateSymbolicLinks(HostPath);
    }

    /// <summary>
    /// Where arenas are built: the escape corpus's own location setting, so that a run pointed
    /// at a particular filesystem fights its races there too.
    /// </summary>
    public static string Location =>
        Environment.GetEnvironmentVariable("CAPDOTNET_TEST_ROOT") is { Length: > 0 } configured
            ? configured
            : Path.GetTempPath();

    /// <summary>The directory holding the sandbox and the directory beside it.</summary>
    public string HostPath { get; }

    /// <summary>The directory the handle under attack is opened on.</summary>
    public string SandboxPath { get; }

    /// <summary>The directory beside the sandbox.</summary>
    public string OutsidePath { get; }

    /// <summary>Whether this volume, and this account, can make a symbolic link.</summary>
    public bool SupportsSymbolicLinks { get; }

    /// <summary>A path beneath the sandbox, written with forward slashes.</summary>
    public string Inside(string relative) => Path.Join(SandboxPath, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>A path beneath the directory outside, written with forward slashes.</summary>
    public string Outside(string relative) => Path.Join(OutsidePath, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Whether an identity is one of the objects outside the sandbox.</summary>
    public bool IsOutside(CapFileId identity) => _outside.Contains(identity);

    /// <summary>Skips a race that needs a symbolic link on a host that cannot make one.</summary>
    public void RequireSymbolicLinks()
    {
        if (!SupportsSymbolicLinks)
        {
            Assert.Skip("This host or volume cannot make symbolic links, so the attack cannot be staged.");
        }
    }

    /// <summary>
    /// Everything outside the sandbox, as text that changes if anything there does.
    /// </summary>
    /// <remarks>
    /// Links are recorded by what they store and never followed, files by their contents. The
    /// races never touch the directory outside themselves, so any difference was made by the
    /// code under test.
    /// </remarks>
    public string SnapshotOutside()
    {
        List<string> lines = [];
        foreach (string path in Directory.EnumerateFileSystemEntries(OutsidePath, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(OutsidePath, path);
            FileInfo info = new(path);
            lines.Add(
                info.LinkTarget is { } target ? $"{relative} -> {target}"
                : Directory.Exists(path) ? $"{relative}/"
                : $"{relative} = {File.ReadAllText(path)}");
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The identity of whatever a path names, following nothing but what the host's own
    /// resolution follows, opened with the process's own authority.
    /// </summary>
    public static CapFileId IdentityOf(string path)
    {
        if (Directory.Exists(path))
        {
            using Dir directory = Dir.Open(path, AmbientAuthority.Acquire());
            return directory.GetMetadata().FileId;
        }

        using Dir parent = Dir.Open(Path.GetDirectoryName(path)!, AmbientAuthority.Acquire());
        using CapFile file = parent.OpenFile(Path.GetFileName(path));
        return file.GetMetadata().FileId;
    }

    public void Dispose()
    {
        _scratch.Dispose();

        // The library's own cleanup refuses, by design, to descend into anything it did not
        // expect, and a race can leave the tree in any shape at all. Whatever survived is
        // removed here without following a link.
        if (Directory.Exists(HostPath))
        {
            HostOps.RemoveWithoutFollowing(HostPath);
        }
    }
}
