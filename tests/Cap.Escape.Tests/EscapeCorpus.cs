using Cap.Primitives;
using static Cap.Escape.Tests.Expectation;

namespace Cap.Escape.Tests;

/// <summary>
/// The adversarial corpus: every attack on the containment guarantee this suite knows how to
/// arrange, written down once as data.
/// </summary>
/// <remarks>
/// <para>
/// A case is a tree, a path into it, and the outcome each operation must come to. The same
/// list is run against every resolution backend the host has, under both symbolic-link
/// policies, through every operation that takes a path — so a case is written once and a
/// disagreement anywhere in that product fails a test naming the case, the operation and the
/// backend. Each backend is held to the expectation written here rather than to the answer
/// another backend gave, because comparing two backends passes whenever both are wrong in the
/// same direction.
/// </para>
/// <para>
/// Expectations are stated per path syntax, since the two platforms' rules genuinely differ:
/// <c>C:\Windows\win.ini</c> is an absolute path under Windows rules and an ordinary filename
/// under POSIX ones, and a corpus that refused it on Linux would make a real file unreachable.
/// What must hold on both is that nothing outside the sandbox is ever reached, and that is
/// checked after every operation whatever the case says it should come to.
/// </para>
/// <para>
/// Every absolute target points at a directory beside the sandbox that the test created
/// and owns. An attack aimed at a system file would fail for the wrong reason if a bug let it
/// through, since an ordinary account cannot write there, and the corpus would stay green.
/// </para>
/// </remarks>
internal static class EscapeCorpus
{
    /// <summary>The directory beside the sandbox, which nothing inside may reach.</summary>
    public const string OutsideDirectory = "outside";

    /// <summary>A file in it. Its name and its contents are both markers of a leak.</summary>
    public const string OutsideFile = "outside-secret";

    /// <summary>A directory in it.</summary>
    public const string OutsideSubdirectory = "outside-sub";

    /// <summary>A file in that directory.</summary>
    public const string OutsideNestedFile = "outside-inner";

    /// <summary>What every file outside holds. Seeing it through a handle is a leak.</summary>
    public const string OutsideContent = "contents that must never be read through the sandbox";

    /// <summary>What every file planted inside holds.</summary>
    public const string InsideContent = "inside";

    /// <summary>The standard directory beneath the root, with <see cref="PlainFile"/> in it.</summary>
    public const string PlainDirectory = "plain";

    /// <summary>A file in <see cref="PlainDirectory"/>.</summary>
    public const string PlainFile = "marker";

    /// <summary>A file operations that move or link something onto the case's path start from.</summary>
    public const string SourceFile = "source";

    /// <summary>The name operations that move or link the case's path land on.</summary>
    public const string LandingName = "landing";

    /// <summary>
    /// The last-write time the operation that sets times gives what the case's path names:
    /// far enough in the past that it cannot be the time anything was made during a run, so
    /// that an outside entry given it is seen in the snapshot of what lies outside.
    /// </summary>
    public static readonly DateTimeOffset PlantedTime = new(2001, 9, 9, 1, 46, 40, TimeSpan.Zero);

    /// <summary>The link the operation that stores the path as a link target creates.</summary>
    public const string CreatedLinkName = "created-link";

    /// <summary>What a link created at the case's path stores.</summary>
    public const string CreatedLinkTarget = "plain/marker";

    /// <summary>
    /// Whether a case's path, stored as a link's target, is rooted on this host, and so refused
    /// before the link is made. Answered by the framework rather than the library, so that the
    /// two readings are checked against each other; the placeholders always stand for rooted
    /// host paths.
    /// </summary>
    public static bool IsRootedTarget(string path) =>
        path.StartsWith("{outside}", StringComparison.Ordinal) ||
        path.StartsWith("{sandbox}", StringComparison.Ordinal) ||
        Path.IsPathRooted(path);

    /// <summary>
    /// Longer than the forty links this library follows and than the limits the kernels apply
    /// to their own resolution, so that every backend refuses it for its own reasons.
    /// </summary>
    private const int OverlongChainLength = 45;

    /// <summary>
    /// Deeper than the walk will descend, so that the bound on handles held at once is reached
    /// on a real tree rather than a simulated one.
    /// </summary>
    private const int OverDeepLevels = 260;

    /// <summary>The table.</summary>
    public static IReadOnlyList<EscapeCase> Cases { get; } = Build();

    /// <summary>Finds a case by name.</summary>
    public static EscapeCase Named(string name) =>
        Cases.FirstOrDefault(entry => entry.Name == name)
            ?? throw new ArgumentOutOfRangeException(nameof(name), name, "No such case in the corpus.");

    private static List<EscapeCase> Build()
    {
        List<EscapeCase> cases = [];
        Lexical(cases);
        Links(cases);
        ProcessFilesystem(cases);
        WindowsNames(cases);
        WindowsReparsePoints(cases);
        Folding(cases);
        return cases;
    }

    /// <summary>What the caller's own string says, before any link is met.</summary>
    private static void Lexical(List<EscapeCase> cases)
    {
        Expectation escape = Uniform(Outcome.Escape);
        Expectation malformed = Uniform(Outcome.Malformed);

        // A parent step that climbs above the root, in each position a check might look for it
        // in and some it might not. Each is walked, never collapsed as text, and refused at the
        // step that leaves -- however the rest of the path would read.
        foreach ((string name, string path) in new[]
        {
            ("parent", ".."),
            ("parent-twice", "../.."),
            ("parent-after-descent", "plain/../.."),
            ("dot-parent-dot", "./../."),
            ("parent-to-a-file-outside", $"../{OutsideDirectory}/{OutsideFile}"),
            ("parent-far-past-the-root", "plain/" + string.Concat(Enumerable.Repeat("../", 64)) + OutsideDirectory),
        })
        {
            cases.Add(new(name, ["L1"], path, escape, escape));
        }

        // A parent step that stays beneath the root is walked like any other component: into
        // `plain`, back out to the root, and into `plain` again.
        cases.Add(new("parent-that-stays-inside", ["L1"], "plain/../plain/marker",
            ExistingFile(), ExistingFile()));
        cases.Add(new("parent-that-stays-inside-to-a-name-not-there", ["L1"], "plain/../plain/absent",
            Absent(), Absent()));
        cases.Add(new("parent-through-a-name-not-there", ["L1"], "absent/../plain/marker",
            Uniform(Outcome.NotFound), Uniform(Outcome.NotFound)));

        // A path ending in a parent step names the directory it climbs back to, here the root
        // itself. It can be opened and described, and nothing can act on it as a name: there
        // is none, and removing or moving what it names would reach a directory the caller
        // never spelled out, up to and including the handle's own.
        Expectation climbedBackTo = ExistingDirectory().With(
            Operation.DeleteTree, Outcome.Refused,
            (Operation.RenameFrom, Outcome.Refused));
        cases.Add(new("parent-trailing", ["L1"], "plain/..", climbedBackTo, climbedBackTo));

        // Absolute, in each syntax. The target is the test's own directory, so that a bug
        // letting one through would reach something writable and be seen doing it.
        cases.Add(new("absolute-to-a-file-outside", ["L2"], $"{{outside}}/{OutsideFile}", escape, escape));
        cases.Add(new("absolute-to-a-directory-outside", ["L2"], "{outside}", escape, escape));
        cases.Add(new("absolute-root", ["L2"], "/", escape, escape));
        cases.Add(new("absolute-system-file", ["L2"], "/etc/passwd", escape, escape));

        // Windows spellings. Under POSIX rules a backslash and a colon are ordinary characters,
        // so each of these is one filename that does not exist yet -- and creating it must
        // create exactly that, beneath the root.
        Expectation ordinaryName = Absent();
        foreach ((string name, string[] threats, string path) in new[]
        {
            ("windows-absolute", new[] { "L2" }, @"C:\Windows\win.ini"),
            ("windows-root-relative", ["L3"], @"\Windows\win.ini"),
            ("windows-drive-relative", ["L3"], "C:win.ini"),
            ("windows-unc", ["L4"], @"\\server\share\file"),
            ("windows-device-namespace-drive", ["L4"], @"\\?\C:\Windows"),
            ("windows-device-namespace-physical", ["L4"], @"\\.\PhysicalDrive0"),
            ("windows-object-manager-namespace", ["L4"], @"\??\C:\Windows"),
        })
        {
            cases.Add(new(name, threats, path, ordinaryName, escape) { Requires = PosixNamesOnUnix });
        }

        // Spelled with forward slashes, a drive path is still absolute under Windows rules;
        // under POSIX ones it passes through a directory named "C:" that is not there.
        cases.Add(new("windows-absolute-forward-slashes", ["L2"], "C:/Windows/win.ini",
            Uniform(Outcome.NotFound), escape)
        {
            Requires = PosixNamesOnUnix,
        });

        // Rooted under both rules.
        cases.Add(new("unc-forward-slashes", ["L4"], "//server/share/file", escape, escape));
        cases.Add(new("device-namespace-forward-slashes", ["L4"], "//./PhysicalDrive0", escape, escape));

        // Names that name nothing. Refused as mistakes, not collapsed into the root: an
        // operation on "." would otherwise act on the directory the caller already holds.
        cases.Add(new("empty", ["L5"], "", malformed, malformed));

        // Stored in a link, the same text names the directory the link is in, which is not a
        // file to open.
        Expectation self = malformed.With(Operation.CreateSymlinkTo, Outcome.Refused);
        cases.Add(new("dot", ["L5"], ".", self, self));
        cases.Add(new("dot-slash-dot", ["L5"], "./.", self, self));

        // Repeated and trailing separators, and "." components, are dropped -- and every
        // component that is left is still checked.
        cases.Add(new("doubled-separator", ["L5"], "plain//marker", ExistingFile(), ExistingFile()));
        cases.Add(new("dot-components", ["L5"], "./plain/./marker", ExistingFile(), ExistingFile()));
        cases.Add(new("trailing-separator-on-a-directory", ["L5"], "plain/",
            ExistingDirectory(), ExistingDirectory()));
        Expectation fileAsDirectory = Uniform(Outcome.Refused);
        cases.Add(new("trailing-separator-on-a-file", ["L5"], "plain/marker/", fileAsDirectory, fileAsDirectory));

        // A NUL ends the string where it reaches the kernel, so a name holding one would be
        // checked as one name and opened as another.
        cases.Add(new("nul-truncation", ["L5"], "plain/marker\0.txt", malformed, malformed));
        cases.Add(new("nul-before-a-parent-step", ["L5"], "plain\0/../..", malformed, malformed));

        // Bounded, and refused rather than overflowing anything. A link may store a name this
        // long -- what it stores is data -- and following it is refused instead.
        Expectation overlongName = malformed.With(Operation.CreateSymlinkTo, Outcome.Refused);
        cases.Add(new("component-too-long", ["L6"], new string('a', CapPath.MaxComponentLength + 1),
            overlongName, overlongName));

        // Longer than the parser will consider, so refused before anything is looked up. The
        // same text is too long for any filesystem to store as a link target.
        string overParser = string.Join('/', Enumerable.Repeat("a", (CapPath.MaxLength / 2) + 2));
        cases.Add(new("path-too-long", ["L6"], overParser,
            malformed.With(Operation.CreateSymlinkTo, Outcome.Refused),
            malformed.With(Operation.CreateSymlinkTo, Outcome.Refused)));

        // Within what the parser accepts and beyond what a kernel takes in one piece. The walk
        // hands the kernel one name at a time and finds the first one missing; the confined
        // open hands it the whole path and is refused for its length. Both refuse, for
        // different and equally true reasons.
        string overKernel = string.Join('/', Enumerable.Repeat("a", 2500));
        cases.Add(new("longer-than-the-kernel-takes-at-once", ["L6"], overKernel,
            Uniform(Outcome.NotFound).With(Operation.CreateSymlinkTo, Outcome.Refused),
            Uniform(Outcome.NotFound).With(Operation.CreateSymlinkTo, Outcome.Refused))
        {
            Differences =
            [
                new(
                    [Backends.ConfinedOpen],
                    Uniform(Outcome.Refused),
                    "the confined open passes the whole path to the kernel, which refuses one longer " +
                    "than its own limit before looking anything up."),
            ],
        });

        // Deeper than the walk will go. The bound exists because the walk holds a handle for
        // every level it has descended through; the confined open holds none, so it has no
        // reason for the bound and resolves the path.
        string overDeep = string.Join('/', Enumerable.Repeat("d", OverDeepLevels));
        cases.Add(new("deeper-than-the-walk-descends", ["L6"], $"{overDeep}/{PlainFile}",
            Uniform(Outcome.Refused), Uniform(Outcome.Refused))
        {
            Setup = [new(SetupKind.File, $"{overDeep}/{PlainFile}")],
            Differences =
            [
                new(
                    [Backends.ConfinedOpen],
                    ExistingFile(),
                    "the depth bound is the walk's, which spends a handle per level; the kernel's " +
                    "confined open spends none and resolves the path."),
            ],
        });

        // Longer than the Win32 path limit. Nothing here goes through the Win32 path layer, so
        // the limit, and the setting that lifts it, have nothing to act on.
        string longNames = string.Join('/', Enumerable.Range(0, 8).Select(i => $"{i}-{new string('l', 48)}"));
        cases.Add(new("longer-than-the-win32-path-limit", ["L6"], $"{longNames}/{PlainFile}",
            ExistingFile(), ExistingFile())
        {
            Setup = [new(SetupKind.File, $"{longNames}/{PlainFile}")],
        });
    }

    /// <summary>Symbolic links, in every position and shape.</summary>
    private static void Links(List<EscapeCase> cases)
    {
        Expectation escapeAtEnd = FinalLink(Outcome.Escape);
        Expectation escapeOnTheWay = Through(Uniform(Outcome.Escape));

        // Absolute targets, which are refused whatever they spell.
        cases.Add(Link("absolute-link-to-a-file-outside", ["S1"], "abs", escapeAtEnd,
            File("abs", $"{{outside}}/{OutsideFile}")));
        cases.Add(Link("absolute-link-to-a-directory-outside", ["S1", "S5"], $"abs/{OutsideFile}", escapeOnTheWay,
            Directory("abs", "{outside}")));
        cases.Add(Link("absolute-link-to-inside", ["S1"], $"abs/{PlainFile}", escapeOnTheWay,
            Directory("abs", "{sandbox}/plain")));

        // Relative targets that climb out.
        cases.Add(Link("link-climbing-to-a-file-outside", ["S2"], "up", escapeAtEnd,
            File("up", $"../{OutsideDirectory}/{OutsideFile}")));
        cases.Add(Link("link-to-the-parent", ["S2"], "dotdot", escapeAtEnd,
            Directory("dotdot", "..")));
        cases.Add(Link("link-to-the-parent-as-a-component", ["S2", "S5"],
            $"dotdot/{OutsideDirectory}/{OutsideFile}", escapeOnTheWay,
            Directory("dotdot", "..")));
        cases.Add(Link("link-to-a-sibling-outside", ["S2", "S5"], $"sibling/{OutsideFile}", escapeOnTheWay,
            Directory("sibling", $"../{OutsideDirectory}")));
        cases.Add(Link("nested-link-climbing-out", ["S2", "S5"], $"plain/up/{OutsideFile}", escapeOnTheWay,
            Directory("plain/up", $"../../{OutsideDirectory}")));

        // A parent step after a link climbs from wherever the link led, not from where the
        // link sits, which is the reason a parent step is never collapsed as text. `hop` leads
        // to `plain/inner`, so `hop/..` is `plain`: collapsed, the same text would name the
        // root, where there is no `marker`. And a climb that starts one level down from where
        // the link led still stops at the root.
        cases.Add(Link("parent-after-a-link-lands-beside-its-target", ["L1", "S3", "S5"], $"hop/../{PlainFile}",
            Through(ExistingFile()),
            new(SetupKind.Directory, "plain/inner"), Directory("hop", "plain/inner")));
        cases.Add(Link("parent-after-a-link-climbing-out", ["L1", "S2", "S5"],
            $"hop/../../../{OutsideDirectory}/{OutsideFile}", escapeOnTheWay,
            new(SetupKind.Directory, "plain/inner"), Directory("hop", "plain/inner")));

        // Links that stay inside, which are followed: the control every refusal above is read
        // against. A climb that stops at the root and descends again is inside.
        cases.Add(Link("link-to-a-file-inside", ["S3", "S15"], "in",
            FinalLink(Outcome.Success, Outcome.Refused),
            File("in", "plain/marker")));
        cases.Add(Link("link-to-a-directory-inside", ["S3", "S5"], $"indir/{PlainFile}", Through(ExistingFile()),
            Directory("indir", "plain")));
        cases.Add(Link("link-to-a-directory-inside-as-the-name", ["S3", "S15"], "indir",
            FinalLink(Outcome.Refused, Outcome.Success),
            Directory("indir", "plain")));
        cases.Add(Link("link-climbing-to-the-root-and-back", ["S3", "S5"], $"plain/root/plain/{PlainFile}",
            Through(ExistingFile()),
            Directory("plain/root", "..")));
        cases.Add(Link("chain-inside", ["S3", "S5"], $"c1/{PlainFile}", Through(ExistingFile()),
            Directory("c1", "c2"), Directory("c2", "c3"), Directory("c3", "plain")));

        // Chains that end outside, and links to links.
        SetupStep[] chainToFile =
        [
            File("k1", "k2"), File("k2", "k3"), File("k3", "k4"), File("k4", "k5"),
            File("k5", $"../{OutsideDirectory}/{OutsideFile}"),
        ];
        cases.Add(Link("chain-of-five-ending-outside", ["S4"], "k1", escapeAtEnd, chainToFile));

        SetupStep[] chainToDirectory =
        [
            Directory("k1", "k2"), Directory("k2", "k3"), Directory("k3", "k4"), Directory("k4", "k5"),
            Directory("k5", $"../{OutsideDirectory}"),
        ];
        cases.Add(Link("chain-of-five-ending-outside-as-a-component", ["S4", "S5"], $"k1/{OutsideFile}",
            escapeOnTheWay, chainToDirectory));
        cases.Add(Link("link-to-a-link-to-outside", ["S4", "S5"], $"l1/{OutsideFile}", escapeOnTheWay,
            Directory("l1", "l2"), Directory("l2", "{outside}")));

        // Chains that do not end.
        Expectation loop = FinalLink(Outcome.Refused);
        cases.Add(Link("self-cycle", ["S4"], "self", loop, File("self", "self")));
        cases.Add(Link("self-cycle-as-a-component", ["S4", "S5"], "self/x",
            Through(Uniform(Outcome.Refused)), File("self", "self")));
        cases.Add(Link("mutual-cycle", ["S4"], "a", loop, File("a", "b"), File("b", "a")));
        cases.Add(Link("chain-beyond-the-budget", ["S4"], "overlong-00", loop,
            [.. Enumerable.Range(0, OverlongChainLength).Select(i => File(
                $"overlong-{i:D2}",
                i == OverlongChainLength - 1 ? "plain" : $"overlong-{i + 1:D2}"))]));

        // Aimed outside at something that exists and at something that does not. The two must
        // be indistinguishable, which is checked pairwise elsewhere; here each has to be
        // refused on its own.
        cases.Add(Link("dangling-link-outside-relative", ["S6"], "gone", escapeAtEnd,
            File("gone", $"../{OutsideDirectory}/absent")));
        cases.Add(Link("dangling-link-outside-absolute", ["S6"], "gone", escapeAtEnd,
            File("gone", "{outside}/absent")));

        // Dangling inside is an ordinary miss. Creating a file there is refused like any
        // other link at the name, rather than creating the file the link names.
        cases.Add(Link("dangling-link-inside", ["S6", "S15"], "dangling",
            FinalLink(Outcome.NotFound, Outcome.NotFound),
            File("dangling", "no-such-entry")));
    }

    /// <summary>The Linux kernel's synthetic links.</summary>
    private static void ProcessFilesystem(List<EscapeCase> cases)
    {
        foreach ((string name, string path, Expectation expected, SetupStep link) in new[]
        {
            ("magic-link-to-the-process-root", "magic", FinalLink(Outcome.Escape),
                Directory("magic", "/proc/self/root")),
            ("magic-link-to-the-process-root-as-a-component", "magic/etc/passwd",
                Through(Uniform(Outcome.Escape)), Directory("magic", "/proc/self/root")),
            ("magic-link-to-an-open-descriptor", "magic", FinalLink(Outcome.Escape),
                File("magic", "/proc/self/fd/0")),
            ("magic-link-to-the-working-directory", "magic/x",
                Through(Uniform(Outcome.Escape)), Directory("magic", "/proc/self/cwd")),
        })
        {
            cases.Add(new(name, ["S7"], path, expected, null)
            {
                Setup = [link],
                Requires = HostFeature.Symlinks | HostFeature.ProcessFilesystem,
            });
        }
    }

    /// <summary>Names Windows gives a meaning other than a filename.</summary>
    private static void WindowsNames(List<EscapeCase> cases)
    {
        Expectation escape = Uniform(Outcome.Escape);
        Expectation malformed = Uniform(Outcome.Malformed);
        Expectation ordinaryName = Absent();

        // Reserved device names, in every disguise the Win32 layer sees through. Each reaches
        // the device wherever it appears, so each is a way out of any directory at all.
        foreach (string name in new[]
        {
            "CON", "con", "CoN", "PRN", "AUX", "NUL", "COM1", "COM9", "LPT1", "LPT9",
            "COM¹", "COM²", "COM³", "LPT¹", "CONIN$", "CONOUT$",
            "CON.txt", "nul.tar.gz", "CON .txt", "Com1.log",
        })
        {
            cases.Add(new($"reserved-name {name}", ["W1", "W2", "W3"], name, ordinaryName, escape)
            {
                Requires = PosixNamesOnUnix,
            });
        }

        cases.Add(new("reserved-name nested", ["W1"], "plain/NUL", ordinaryName, escape));

        // Trailing dots and spaces, which Windows strips below its API -- so the name checked
        // would not be the name opened.
        foreach (string name in new[] { "plain.", "plain ", "plain. .", "plain/marker.", "CON.", "NUL " })
        {
            Expectation windows = name.StartsWith("plain", StringComparison.Ordinal) ? malformed : escape;
            cases.Add(new($"trailing-dot-or-space '{name}'", ["W2", "W5"], name, ordinaryName, windows)
            {
                Requires = PosixNamesOnUnix,
            });
        }

        // Alternate data streams: a second, hidden body of the same file, or a way to reach a
        // device through one.
        cases.Add(new("alternate-data-stream", ["W4"], "plain/marker:stream", ordinaryName, malformed)
        {
            Requires = PosixNamesOnUnix,
        });
        cases.Add(new("default-data-stream", ["W4"], "plain/marker::$DATA", ordinaryName, malformed)
        {
            Requires = PosixNamesOnUnix,
        });
        // Refused for the stream separator before the device name is considered, which is
        // the earlier of the two refusals it has coming.
        cases.Add(new("device-through-a-stream", ["W4"], "CON::$DATA", ordinaryName, malformed)
        {
            Requires = PosixNamesOnUnix,
        });

        // Characters the native open reads as patterns, so one name could match another.
        foreach (string name in new[] { "pl*n", "pl?in", "pl<in", "pl>in", "pl\"in", "pl|in" })
        {
            cases.Add(new($"wildcard '{name}'", ["W7"], name, ordinaryName, malformed)
            {
                Requires = PosixNamesOnUnix,
            });
        }
    }

    /// <summary>Windows reparse points that are not symbolic links.</summary>
    private static void WindowsReparsePoints(List<EscapeCase> cases)
    {
        // A junction's target is always a path from a volume root, so one can never be followed
        // beneath a handle -- even one pointing back inside.
        foreach ((string name, string path, Expectation expected, string target) in new[]
        {
            ("junction-to-outside", "jn", FinalLink(Outcome.Escape), "{outside}"),
            ("junction-to-outside-as-a-component", $"jn/{OutsideFile}", Through(Uniform(Outcome.Escape)), "{outside}"),
            ("junction-to-inside", $"jn/{PlainFile}", Through(Uniform(Outcome.Escape)), "{sandbox}/plain"),
        })
        {
            cases.Add(new(name, ["S8"], path, null, expected)
            {
                Setup = [new(SetupKind.Junction, "jn", target)],
                Requires = HostFeature.Junctions,
            });
        }
    }

    /// <summary>
    /// Volumes that fold case or Unicode normalisation, where two spellings reach one entry.
    /// </summary>
    /// <remarks>
    /// Where the volume folds, the second spelling reaches the entry and has to be treated
    /// exactly as the first would be — a link reached by an alias is still refused. Where it
    /// does not, the second spelling names nothing. Neither outcome depends on this library
    /// comparing names as strings, which is the point: containment is decided from what was
    /// opened.
    /// </remarks>
    private static void Folding(List<EscapeCase> cases)
    {
        Expectation missingParent = Uniform(Outcome.NotFound);

        cases.Add(new("case-variant-of-a-file", ["M2"], "PLAIN/MARKER", missingParent, ExistingFile())
        {
            FoldedBy = HostFeature.CaseInsensitive,
            WhenFolded = ExistingFile(),
        });
        cases.Add(new("case-variant-of-an-escaping-link", ["M2", "S2"], $"UP/{OutsideFile}", missingParent,
            Through(Uniform(Outcome.Escape)))
        {
            Setup = [Directory("up", $"../{OutsideDirectory}")],
            Requires = HostFeature.Symlinks,
            FoldedBy = HostFeature.CaseInsensitive,
            WhenFolded = Through(Uniform(Outcome.Escape)),
        });

        // U+00E9 composed, and e followed by U+0301 decomposed.
        const string Composed = "caf\u00e9";
        const string Decomposed = "cafe\u0301";

        cases.Add(new("normalisation-variant-of-a-file", ["M1"], $"{Decomposed}/{PlainFile}",
            missingParent, missingParent)
        {
            Setup = [new(SetupKind.File, $"{Composed}/{PlainFile}")],
            FoldedBy = HostFeature.NormalizationInsensitive,
            WhenFolded = ExistingFile(),
        });
        cases.Add(new("normalisation-variant-of-an-escaping-link", ["M1", "S2"], $"{Decomposed}/{OutsideFile}",
            missingParent, missingParent)
        {
            Setup = [Directory(Composed, $"../{OutsideDirectory}")],
            Requires = HostFeature.Symlinks,
            FoldedBy = HostFeature.NormalizationInsensitive,
            WhenFolded = Through(Uniform(Outcome.Escape)),
        });
    }

    /// <summary>
    /// Cases whose POSIX half creates a name holding characters a FAT volume refuses. Where the
    /// volume cannot hold the name the POSIX half has nothing to say, and is skipped with that
    /// reason; the Windows half never needs the feature.
    /// </summary>
    private static HostFeature PosixNamesOnUnix =>
        OperatingSystem.IsWindows() ? HostFeature.None : HostFeature.PosixNames;

    /// <summary>A case whose tree is made of links, the same on both platforms.</summary>
    /// <remarks>
    /// Every case runs through the operations that change something as well as the ones that
    /// read, so a link case also defends the rows about mutations: one acting on a link at the
    /// last component must act on the link, and one passing through a link before it must be
    /// confined exactly as an open is.
    /// </remarks>
    private static EscapeCase Link(
        string name, string[] threats, string path, Expectation expected, params SetupStep[] setup) =>
        new(name, [.. threats, expected.Role == LinkRole.Final ? "S13" : "S14"], path, expected, expected)
        {
            Setup = setup,
            Requires = HostFeature.Symlinks,
        };

    private static SetupStep File(string path, string target) => new(SetupKind.FileLink, path, target);

    private static SetupStep Directory(string path, string target) => new(SetupKind.DirectoryLink, path, target);
}
