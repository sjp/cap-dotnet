using System.Text;
using Cap.Primitives;
using Cap.Tests;

namespace Cap.Std.Testing.Tests;

/// <summary>
/// What a <see cref="Dir"/> on an in-memory filesystem does with a path is what it does on
/// disk: the same exception type and kind when it refuses, the same answer when it does not,
/// and the same tree afterwards. That holds for every operation that takes a path, under both
/// link policies and both in-memory resolutions, for escapes and for ordinary refusals alike.
/// </summary>
/// <remarks>
/// <para>
/// Each case builds the same tree on disk and in memory, runs one operation through a handle on
/// each, and compares. The disk is the oracle, whichever backend the host chose for it, so the
/// test says nothing about what the answer is and everything about whether the two agree. The
/// escape corpus holds the in-memory legs to the disk too, but in outcome buckets that erase
/// the kind: a refusal for a link loop and one for a name already taken land in the same
/// bucket there, and are told apart here.
/// </para>
/// <para>
/// A place where the two are known to answer differently is listed in <see cref="Differences"/>
/// with the reason, and the case then checks that they still do, so the entry goes when the
/// difference does.
/// </para>
/// </remarks>
public sealed class DiskParityTests : IDisposable
{
    /// <summary>An operation a <see cref="Dir"/> offers on a path, each with its own core.</summary>
    public enum Operation
    {
        ReadAllBytes,
        WriteAllBytes,
        AppendAllText,
        CreateNewFile,
        CreateFile,
        OpenFileNoFollow,
        OpenAny,
        OpenAnyNoFollow,
        OpenDir,
        OpenDirNoFollow,
        EnumerateEntries,
        CreateDir,
        OpenOrCreateDir,
        OpenOrCreateDirAll,
        DeleteFile,
        DeleteDir,
        CreateSymlink,
        CreateDirSymlink,
        GetMetadata,
        GetMetadataFollowingLink,
        Exists,
        ReadLink,
        Rename,
        RenameReplacing,
        CreateHardLink,
        CreateHardLinkFollowingLink,
    }

    private static readonly Operation[] TwoPathOperations =
        [Operation.Rename, Operation.RenameReplacing, Operation.CreateHardLink, Operation.CreateHardLinkFollowingLink];

    /// <summary>A second name for <c>plain</c>, given only in the cases that name it.</summary>
    private const string Twin = "twin";

    /// <summary>A name longer than the 255 bytes Linux stores, though not in characters.</summary>
    private static readonly string OverlongName = new('é', 200);

    /// <summary>
    /// Paths every one-path operation is run on: what is there, what is not, links of every
    /// kind with and without a trailing separator, and the ways out of the tree.
    /// </summary>
    private static readonly string[] Paths =
    [
        "plain",
        "plain/",
        "plain/child",
        "inner",
        "inner/",
        "inner/marker",
        "inner/marker/",
        "inner/..",
        ".",
        "empty",
        "full",
        "missing",
        "missing/",
        "missing/deeper",
        "link",
        "link/",
        "dirlink",
        "dirlink/",
        "dirlink/marker",
        "dangling",
        "dangling/",
        "loop",
        "out",
        "out/secret.txt",
        "inner/up",
        "../outside/secret.txt",
        "inner/../../outside/secret.txt",
        "/etc/passwd",
        OverlongName,
    ];

    /// <summary>
    /// Pairs every two-path operation is run on: onto nothing, onto each kind of thing already
    /// there, into itself, through and onto links, and out of the tree at either end.
    /// </summary>
    private static readonly (string From, string To)[] Pairs =
    [
        ("plain", "fresh"),
        ("plain", "fresh/"),
        ("plain", "plain"),
        ("plain", "inner/marker"),
        ("plain", "empty"),
        ("plain", "full"),
        ("plain", "missing/fresh"),
        ("plain", "dirlink/fresh"),
        ("plain", "link"),
        ("plain", "dangling"),
        ("plain", "../outside/stolen"),
        ("inner", "fresh"),
        ("inner", "plain"),
        ("inner", "empty"),
        ("inner", "inner/sub"),
        ("empty", "full"),
        ("link", "fresh"),
        ("dirlink", "fresh"),
        ("dangling", "fresh"),
        ("missing", "fresh"),
        ("out/secret.txt", "stolen"),
        ("plain", OverlongName),
        ("plain", Twin),
    ];

    /// <summary>Pairs that give a directory a second name where a name is already taken.</summary>
    private static readonly (string From, string To)[] DirectoryOntoTakenName =
        [("inner", "plain"), ("inner", "empty"), ("empty", "full")];

    /// <summary>
    /// Cases where memory is known to answer differently from the disk, by <see cref="CaseName"/>,
    /// with why.
    /// </summary>
    private static readonly Dictionary<string, string> Differences = KnownDifferences();

    private static Dictionary<string, string> KnownDifferences()
    {
        Dictionary<string, string> differences = new(StringComparer.Ordinal);
        foreach (ResolutionBackend resolution in (ResolutionBackend[])[ResolutionBackend.PortableWalk, ResolutionBackend.ConfinedOpen])
        {
            foreach (SymlinkPolicy policy in (SymlinkPolicy[])[SymlinkPolicy.FollowWithinSandbox, SymlinkPolicy.Deny])
            {
                if (OperatingSystem.IsMacOS())
                {
                    differences.Add(
                        CaseName(resolution, policy, Operation.Rename, "plain", "plain"),
                        "macOS's renamex_np with RENAME_EXCL treats a move onto the same name as done, as " +
                        "Windows does, where Linux, which the in-memory Unix rules follow, refuses the name " +
                        "as taken before it notices it is the one being moved");

                    foreach ((string from, string to) in DirectoryOntoTakenName)
                    {
                        foreach (Operation operation in (Operation[])[Operation.CreateHardLink, Operation.CreateHardLinkFollowingLink])
                        {
                            differences.Add(
                                CaseName(resolution, policy, operation, from, to),
                                "macOS refuses a hard link of a directory before it looks at the destination, as " +
                                "Windows does, where Linux, which the in-memory Unix rules follow, first reports " +
                                "the destination name as taken");
                        }
                    }
                }
            }
        }

        return differences;
    }

    private readonly DirectoryInfo _disk = Directory.CreateTempSubdirectory("cap-memory-parity-");
    private readonly bool _linksAvailable;

    public DiskParityTests()
    {
        string sandbox = Path.Combine(_disk.FullName, "sandbox");
        Directory.CreateDirectory(Path.Combine(sandbox, "inner"));
        Directory.CreateDirectory(Path.Combine(sandbox, "empty"));
        Directory.CreateDirectory(Path.Combine(sandbox, "full"));
        Directory.CreateDirectory(Path.Combine(_disk.FullName, "outside"));
        File.WriteAllText(Path.Combine(sandbox, "plain"), "p");
        File.WriteAllText(Path.Combine(sandbox, "inner", "marker"), "m");
        File.WriteAllText(Path.Combine(sandbox, "full", "child"), "c");
        File.WriteAllText(Path.Combine(_disk.FullName, "outside", "secret.txt"), "secret");
        // Windows without the privilege to create links cannot build the tree to compare
        // against, and the cases are skipped.
        _linksAvailable = HostLinks.TryCreate(
            () =>
            {
                File.CreateSymbolicLink(Path.Combine(sandbox, "link"), "plain");
                Directory.CreateSymbolicLink(Path.Combine(sandbox, "dirlink"), "inner");
                File.CreateSymbolicLink(Path.Combine(sandbox, "dangling"), "nowhere");
                File.CreateSymbolicLink(Path.Combine(sandbox, "loop"), "loop");
                Directory.CreateSymbolicLink(Path.Combine(sandbox, "out"), Path.Combine("..", "outside"));
                File.CreateSymbolicLink(
                    Path.Combine(sandbox, "inner", "up"), Path.Combine("..", "..", "outside", "secret.txt"));
            },
            out _);
    }

    public void Dispose() => _disk.Delete(recursive: true);

    public static TheoryData<ResolutionBackend, SymlinkPolicy, Operation, string, string> Cases()
    {
        TheoryData<ResolutionBackend, SymlinkPolicy, Operation, string, string> cases = [];
        foreach (ResolutionBackend resolution in (ResolutionBackend[])[ResolutionBackend.PortableWalk, ResolutionBackend.ConfinedOpen])
        {
            foreach (SymlinkPolicy policy in (SymlinkPolicy[])[SymlinkPolicy.FollowWithinSandbox, SymlinkPolicy.Deny])
            {
                foreach (Operation operation in Enum.GetValues<Operation>())
                {
                    if (TwoPathOperations.Contains(operation))
                    {
                        foreach ((string from, string to) in Pairs)
                        {
                            cases.Add(resolution, policy, operation, from, to);
                        }
                    }
                    else
                    {
                        foreach (string path in Paths)
                        {
                            cases.Add(resolution, policy, operation, path, string.Empty);
                        }
                    }
                }
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void An_operation_answers_as_it_does_on_disk(
        ResolutionBackend resolution, SymlinkPolicy policy, Operation operation, string path, string second)
    {
        ExpectedHostFeatures.Require(HostFeature.Symlinks, _linksAvailable, "The disk tree to compare against cannot be built.");
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("The disk side has a backend only on Linux, macOS and Windows.");
        }

        if (!OperatingSystem.IsLinux() && (path == OverlongName || second == OverlongName))
        {
            // NTFS counts a name in UTF-16 units and APFS in characters, and 200 of either is a
            // name they store.
            Assert.Skip("Only Linux, which the in-memory Unix rules follow, counts a name in UTF-8 bytes.");
        }

        bool twinned = path == Twin || second == Twin;
        if (twinned && !OperatingSystem.IsLinux())
        {
            // Not yet run on these hosts, so what they answer for a second hard link to the
            // object being moved or linked is not known.
            Assert.Skip("A move or link onto another hard link to the same file has been compared only on Linux.");
        }

        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { Resolution = resolution });
        fs.AddFile("sandbox/plain", "p");
        fs.AddFile("sandbox/inner/marker", "m");
        fs.AddDirectory("sandbox/empty");
        fs.AddFile("sandbox/full/child", "c");
        fs.AddFile("outside/secret.txt", "secret");
        fs.AddSymbolicLink("sandbox/link", "plain");
        fs.AddSymbolicLink("sandbox/dirlink", "inner", targetIsDirectory: true);
        fs.AddSymbolicLink("sandbox/dangling", "nowhere");
        fs.AddSymbolicLink("sandbox/loop", "loop");
        fs.AddSymbolicLink("sandbox/out", Spell(fs, "../outside"), targetIsDirectory: true);
        fs.AddSymbolicLink("sandbox/inner/up", Spell(fs, "../../outside/secret.txt"));

        if (twinned)
        {
            fs.AddHardLink("sandbox/" + Twin, "sandbox/plain");
            using Dir sandbox = Dir.Open(Path.Combine(_disk.FullName, "sandbox"), AmbientAuthority.Acquire());
            sandbox.CreateHardLink("plain", sandbox, Twin);
        }

        (string? onDiskAnswer, Exception? onDisk) = Run(
            () => Dir.Open(Path.Combine(_disk.FullName, "sandbox"), AmbientAuthority.Acquire(), policy), operation, path, second);
        (string? inMemoryAnswer, Exception? inMemory) = Run(() => fs.OpenRoot("sandbox", policy), operation, path, second);

        string disk = Describe(onDiskAnswer, onDisk, DiskTree(_disk));
        string memory = Describe(inMemoryAnswer, inMemory, MemoryTree(fs));
        string name = CaseName(resolution, policy, operation, path, second);
        if (Differences.TryGetValue(name, out string? why))
        {
            Assert.True(
                disk != memory,
                $"{name} is listed as answering differently from the disk ({why}), and now answers the same. Remove it from the list.");
            return;
        }

        Assert.Equal(disk, memory);
    }

    /// <summary>The key a case has in <see cref="Differences"/>.</summary>
    private static string CaseName(
        ResolutionBackend resolution, SymlinkPolicy policy, Operation operation, string path, string second) =>
        $"{resolution} {policy} {operation}({path}{(second.Length == 0 ? string.Empty : ", " + second)})";

    /// <summary>A relative link target in the separator the in-memory filesystem's syntax reads.</summary>
    private static string Spell(InMemoryFileSystem fs, string target) =>
        fs.PathSyntax == CapPathSyntax.Windows ? target.Replace('/', '\\') : target;

    /// <summary>
    /// Opens a handle and runs <paramref name="operation"/> through it, returning what it
    /// answered or what it threw.
    /// </summary>
    private static (string? Answer, Exception? Thrown) Run(Func<Dir> open, Operation operation, string path, string second)
    {
        using Dir root = open();
        try
        {
            return (Perform(root, operation, path, second), null);
        }
        catch (Exception thrown)
        {
            return (null, thrown);
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/>, and returns what it answered that the other side
    /// could answer differently: contents, names, kinds, targets.
    /// </summary>
    private static string Perform(Dir root, Operation operation, string path, string second)
    {
        switch (operation)
        {
            case Operation.ReadAllBytes:
                return Encoding.UTF8.GetString(root.ReadAllBytes(path));
            case Operation.WriteAllBytes:
                root.WriteAllBytes(path, "w"u8);
                return string.Empty;
            case Operation.AppendAllText:
                root.AppendAllText(path, "a");
                return string.Empty;
            case Operation.CreateNewFile:
                root.CreateNewFile(path).Dispose();
                return string.Empty;
            case Operation.CreateFile:
                root.CreateFile(path).Dispose();
                return string.Empty;
            case Operation.OpenFileNoFollow:
                root.OpenFile(path, noFollow: true).Dispose();
                return string.Empty;
            case Operation.OpenAny:
            case Operation.OpenAnyNoFollow:
                using (CapOpened opened = root.OpenAny(path, noFollow: operation == Operation.OpenAnyNoFollow))
                {
                    return opened.IsDirectory ? "directory" : "file";
                }

            case Operation.OpenDir:
                root.OpenDir(path).Dispose();
                return string.Empty;
            case Operation.OpenDirNoFollow:
                root.OpenDir(path, noFollow: true).Dispose();
                return string.Empty;
            case Operation.EnumerateEntries:
                using (Dir dir = root.OpenDir(path))
                {
                    return string.Join(
                        ", ",
                        dir.EnumerateEntries().Select(entry => $"{entry.Name}:{entry.Type}").Order(StringComparer.Ordinal));
                }

            case Operation.CreateDir:
                root.CreateDir(path).Dispose();
                return string.Empty;
            case Operation.OpenOrCreateDir:
                root.OpenOrCreateDir(path).Dispose();
                return string.Empty;
            case Operation.OpenOrCreateDirAll:
                root.OpenOrCreateDirAll(path).Dispose();
                return string.Empty;
            case Operation.DeleteFile:
                root.DeleteFile(path);
                return string.Empty;
            case Operation.DeleteDir:
                root.DeleteDir(path);
                return string.Empty;
            case Operation.CreateSymlink:
                root.CreateSymlink(path, "plain");
                return string.Empty;
            case Operation.CreateDirSymlink:
                root.CreateDirSymlink(path, "inner");
                return string.Empty;
            case Operation.GetMetadata:
            case Operation.GetMetadataFollowingLink:
                CapMetadata metadata = root.GetMetadata(path, followLink: operation == Operation.GetMetadataFollowingLink);
                return metadata.Type == CapFileType.File ? $"{metadata.Type} of {metadata.Length}" : metadata.Type.ToString();
            case Operation.Exists:
                return root.Exists(path).ToString();
            case Operation.ReadLink:
                return root.ReadLink(path).Replace('\\', '/');
            case Operation.Rename:
            case Operation.RenameReplacing:
                root.Rename(path, root, second, replaceExisting: operation == Operation.RenameReplacing);
                return string.Empty;
            case Operation.CreateHardLink:
            case Operation.CreateHardLinkFollowingLink:
                root.CreateHardLink(path, root, second, followLink: operation == Operation.CreateHardLinkFollowingLink);
                return string.Empty;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    /// <summary>
    /// The answer or the refusal, then the tree, as one text to compare: a mismatch shows both
    /// sides whole.
    /// </summary>
    private static string Describe(string? answer, Exception? thrown, IEnumerable<string> tree)
    {
        string outcome = thrown is null
            ? $"answered '{answer}'"
            : $"threw {thrown.GetType().Name} ({CapIOException.KindOf(thrown)})";
        return $"{outcome}{Environment.NewLine}{string.Join(Environment.NewLine, tree)}";
    }

    /// <summary>Everything beneath the disk tree's top, one line an entry, in ordinal order.</summary>
    private static List<string> DiskTree(DirectoryInfo top)
    {
        List<string> lines = [];
        Walk(top, string.Empty);
        lines.Sort(StringComparer.Ordinal);
        return lines;

        void Walk(DirectoryInfo directory, string prefix)
        {
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
            {
                string name = prefix + entry.Name;
                if (entry.LinkTarget is { } target)
                {
                    lines.Add($"{name} -> {target.Replace('\\', '/')}");
                }
                else if (entry is DirectoryInfo child)
                {
                    lines.Add($"{name}/");
                    Walk(child, name + "/");
                }
                else
                {
                    lines.Add($"{name} = {File.ReadAllText(entry.FullName)}");
                }
            }
        }
    }

    /// <summary>Everything in the in-memory tree, in the form <see cref="DiskTree"/> gives.</summary>
    private static List<string> MemoryTree(InMemoryFileSystem fs)
    {
        List<string> lines = [];
        Walk(string.Empty);
        lines.Sort(StringComparer.Ordinal);
        return lines;

        void Walk(string prefix)
        {
            foreach (string entry in fs.GetEntries(prefix))
            {
                string name = prefix + entry;
                try
                {
                    lines.Add($"{name} -> {fs.GetSymbolicLinkTarget(name).Replace('\\', '/')}");
                    continue;
                }
                catch (IOException)
                {
                }

                try
                {
                    _ = fs.GetEntries(name);
                }
                catch (IOException)
                {
                    lines.Add($"{name} = {fs.ReadAllText(name)}");
                    continue;
                }

                lines.Add($"{name}/");
                Walk(name + "/");
            }
        }
    }

    [Fact]
    public void A_planted_link_to_a_rooted_target_is_refused_as_an_escape()
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Unix });
        fs.AddSymbolicLink("passwd", "/etc/passwd");

        using Dir root = fs.OpenRoot();

        SandboxEscapeException refused = Assert.Throws<SandboxEscapeException>(() => root.ReadAllBytes("passwd"));
        Assert.Equal(CapErrorKind.Escaped, refused.Kind);
        Assert.Throws<SandboxEscapeException>(() => root.CreateSymlink("again", "/etc/passwd"));
    }
}
