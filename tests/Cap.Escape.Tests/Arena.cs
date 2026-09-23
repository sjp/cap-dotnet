using System.Security.Cryptography;

namespace Cap.Escape.Tests;

/// <summary>
/// The ground one case is fought over: a sandbox, a directory beside it that nothing inside
/// may reach, and a record of everything outside the sandbox taken before the attack.
/// </summary>
/// <remarks>
/// <para>
/// Built fresh for every case and every operation. The operations mutate the tree — a removal
/// that succeeds takes its target with it — so a tree shared between them would make each
/// result depend on what ran before.
/// </para>
/// <para>
/// Everything here is done through the host's ordinary path-based API. That is the only way to
/// arrange the tree without asking the code under test to agree to it, and the only way to
/// check afterwards what happened outside without trusting the code under test to report it.
/// </para>
/// </remarks>
internal sealed class Arena : IDisposable
{
    /// <summary>
    /// The environment variable naming a directory to build in, so that the corpus can be
    /// pointed at a particular filesystem. The system's temporary location otherwise.
    /// </summary>
    public const string LocationVariable = "CAPDOTNET_TEST_ROOT";

    private readonly ScratchTree _scratch;

    public Arena()
    {
        _scratch = new ScratchTree(Location);
        HostPath = _scratch.HostPath;
        SandboxPath = Path.Join(HostPath, "sandbox");
        OutsidePath = Path.Join(HostPath, EscapeCorpus.OutsideDirectory);

        Directory.CreateDirectory(SandboxPath);
        Directory.CreateDirectory(Path.Join(OutsidePath, EscapeCorpus.OutsideSubdirectory));
        File.WriteAllText(Path.Join(OutsidePath, EscapeCorpus.OutsideFile), EscapeCorpus.OutsideContent);
        File.WriteAllText(
            Path.Join(OutsidePath, EscapeCorpus.OutsideSubdirectory, EscapeCorpus.OutsideNestedFile),
            EscapeCorpus.OutsideContent);

        AddFile(Path.Join(EscapeCorpus.PlainDirectory, EscapeCorpus.PlainFile));
        AddFile(EscapeCorpus.SourceFile);
    }

    /// <summary>Where arenas are built.</summary>
    public static string Location =>
        Environment.GetEnvironmentVariable(LocationVariable) is { Length: > 0 } configured
            ? configured
            : Path.GetTempPath();

    /// <summary>The directory holding the sandbox and the directory beside it.</summary>
    public string HostPath { get; }

    /// <summary>The directory the handle under attack is opened on.</summary>
    public string SandboxPath { get; }

    /// <summary>The directory beside the sandbox.</summary>
    public string OutsidePath { get; }

    /// <summary>Plants what a case asks for.</summary>
    public void Plant(IEnumerable<SetupStep> steps)
    {
        foreach (SetupStep step in steps)
        {
            string full = Inside(step.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            switch (step.Kind)
            {
                case SetupKind.Directory:
                    Directory.CreateDirectory(full);
                    break;

                case SetupKind.File:
                    File.WriteAllText(full, EscapeCorpus.InsideContent);
                    break;

                case SetupKind.FileLink:
                    File.CreateSymbolicLink(full, LinkTarget(step.Target!));
                    break;

                case SetupKind.DirectoryLink:
                    Directory.CreateSymbolicLink(full, LinkTarget(step.Target!));
                    break;

                case SetupKind.Junction:
                    HostFilesystem.CreateJunction(full, Expand(step.Target!));
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(steps), step.Kind, "Unknown setup step.");
            }
        }
    }

    /// <summary>Replaces the placeholders a case writes in paths and link targets.</summary>
    public string Expand(string text) =>
        text.Replace("{outside}", OutsidePath, StringComparison.Ordinal)
            .Replace("{sandbox}", SandboxPath, StringComparison.Ordinal);

    /// <summary>
    /// Everything outside the sandbox, as text that changes if anything there does.
    /// </summary>
    /// <remarks>
    /// Links are recorded by what they store and never followed, and files by a digest of
    /// their contents. What is compared is what an attack would change: an entry appearing,
    /// disappearing, changing kind, being retargeted or being written to.
    /// </remarks>
    /// <param name="alsoExcluded">
    /// Directories beside the sandbox that the operation is entitled to change, such as the
    /// destination of a copy.
    /// </param>
    public string SnapshotOutside(params string[] alsoExcluded) => Snapshot(HostPath, [SandboxPath, .. alsoExcluded]);

    /// <summary>Everything inside the sandbox, recorded the same way.</summary>
    public string SnapshotSandbox() => Snapshot(SandboxPath, []);

    /// <summary>Whether a name beneath the sandbox is taken, without following a link.</summary>
    public bool ExistsInside(string path)
    {
        string full = Inside(path);
        return File.Exists(full) || Directory.Exists(full) || new FileInfo(full).LinkTarget is not null;
    }

    public void Dispose()
    {
        _scratch.Dispose();

        // The library's own cleanup leaves behind, silently and by design, anything it will not
        // descend to -- and one case builds a tree deeper than resolution goes, on purpose. What
        // survives is removed here without following a link, so that the run leaves nothing.
        if (Directory.Exists(HostPath))
        {
            RemoveWithoutFollowing(new DirectoryInfo(HostPath));
        }
    }

    private static void RemoveWithoutFollowing(DirectoryInfo directory)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is null && entry is DirectoryInfo nested)
            {
                RemoveWithoutFollowing(nested);
            }
            else
            {
                entry.Delete();
            }
        }

        directory.Delete();
    }

    private void AddFile(string relative)
    {
        string full = Path.Join(SandboxPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, EscapeCorpus.InsideContent);
    }

    private string Inside(string path) =>
        Path.Join(SandboxPath, path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// A link target as the host should store it: the placeholders expanded, and a relative
    /// target written with the platform's own separator, since a Windows link stores its
    /// target as given and the library reads it under Windows rules either way.
    /// </summary>
    private string LinkTarget(string target)
    {
        string expanded = Expand(target);
        return Path.IsPathRooted(expanded) ? expanded : expanded.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string Snapshot(string root, string[] excluded)
    {
        List<string> lines = [];
        Record(new DirectoryInfo(root), root, excluded, lines);
        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    private static void Record(DirectoryInfo directory, string root, string[] excluded, List<string> lines)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
        {
            if (excluded.Contains(entry.FullName, StringComparer.Ordinal))
            {
                continue;
            }

            string relative = Path.GetRelativePath(root, entry.FullName);
            if (entry.LinkTarget is { } target)
            {
                lines.Add($"{relative} -> {target}");
            }
            else if (entry is DirectoryInfo nested)
            {
                lines.Add($"{relative}/");
                Record(nested, root, excluded, lines);
            }
            else
            {
                byte[] digest = SHA256.HashData(File.ReadAllBytes(entry.FullName));
                lines.Add($"{relative} {Convert.ToHexString(digest)}");
            }
        }
    }
}

/// <summary>What a case needs from the host, found once per run.</summary>
internal static class HostFeatures
{
    private static readonly Lazy<HostFeature> Probed = new(Probe);

    /// <summary>The features of the host, and of the volume arenas are built on.</summary>
    public static HostFeature Current => Probed.Value;

    private static HostFeature Probe()
    {
        using ScratchTree scratch = new(Arena.Location);
        string root = scratch.HostPath;
        HostFeature features = HostFeature.None;

        if (Attempt(() => File.CreateSymbolicLink(Path.Join(root, "link"), "target")))
        {
            features |= HostFeature.Symlinks;
        }

        File.WriteAllText(Path.Join(root, "Probe-Case"), string.Empty);
        if (Attempt(() => HostFilesystem.CreateHardLink(Path.Join(root, "Probe-Case"), Path.Join(root, "hard"))))
        {
            features |= HostFeature.HardLinks;
        }

        if (File.Exists(Path.Join(root, "PROBE-CASE")))
        {
            features |= HostFeature.CaseInsensitive;
        }

        File.WriteAllText(Path.Join(root, "café"), string.Empty);
        if (File.Exists(Path.Join(root, "café")))
        {
            features |= HostFeature.NormalizationInsensitive;
        }

        if (!OperatingSystem.IsWindows() &&
            Attempt(() => File.WriteAllText(Path.Join(root, "a:b\\c*d?e<f>g|h\"i."), string.Empty)))
        {
            features |= HostFeature.PosixNames;
        }

        if (OperatingSystem.IsWindows() &&
            Attempt(() => HostFilesystem.CreateJunction(Path.Join(root, "junction"), root)))
        {
            features |= HostFeature.Junctions;
        }

        if (OperatingSystem.IsLinux() && Directory.Exists("/proc/self/fd"))
        {
            features |= HostFeature.ProcessFilesystem;
        }

        return features;
    }

    private static bool Attempt(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
