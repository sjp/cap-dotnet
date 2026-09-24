namespace Cap.Escape.Tests;

/// <summary>
/// What an operation came to, as far as a caller of the public API can tell.
/// </summary>
/// <remarks>
/// Deliberately coarse. The corpus asserts what a caller can act on — did it work, was it
/// refused as an attempt to leave, was the name simply not there — and not the platform's
/// own code for it, which legitimately differs between backends that agree on everything
/// that matters.
/// </remarks>
internal enum Outcome
{
    /// <summary>The operation did what it was asked, beneath the handle.</summary>
    Success,

    /// <summary>Refused on containment grounds: a <c>SandboxEscapeException</c>.</summary>
    Escape,

    /// <summary>
    /// The name, or a directory above it, is not there. For <see cref="Operation.Exists"/>,
    /// the answer false.
    /// </summary>
    NotFound,

    /// <summary>
    /// Refused for a reason that is neither containment nor absence: a link loop, a link the
    /// policy will not follow, a name already taken, the wrong kind of object, a depth or
    /// length limit, or a filesystem that cannot do what was asked.
    /// </summary>
    Refused,

    /// <summary>Refused as a malformed argument before anything was looked up.</summary>
    Malformed,

    /// <summary>
    /// Refused as a permission failure. Every case is built from objects the test owns, so
    /// this is never the right answer and no case expects it. It is an outcome of its own so
    /// that a permission failure is reported as one, rather than passing as some other refusal.
    /// </summary>
    Denied,
}

/// <summary>
/// The operations every case is driven through.
/// </summary>
/// <remarks>
/// <para>
/// Every member of the public surface that takes a path, and each end of the ones that take
/// two. A corpus run only against opening a file would miss the class of bug that matters
/// most here, which is a mutation that resolves its path differently from an open: the
/// classic sandbox escape is a removal or a rename that follows a link the open would have
/// refused.
/// </para>
/// <para>
/// Grouped by what each does with the last component, because that is what decides the
/// expected outcome. Opening a file or a directory follows a link that holds the name.
/// Creating a file refuses one, so that a write cannot be steered onto whatever a planted
/// link leads to. Every other operation acts on the name itself, so a link there is removed,
/// moved, described or read as a link and what it points at is never reached.
/// </para>
/// <para>
/// Five ask the other way about a final link, per call: opening without following it, and
/// describing, setting times on and hard-linking what it leads to. Following one there is
/// held to the same containment as following one on the way, which is why they are driven
/// through every case like the rest.
/// </para>
/// <para>
/// The last two open whatever the name holds without saying which kind, following a link
/// there or refusing it. They reach what either kind of open would, through a single
/// resolution, and are held to the same containment as both.
/// </para>
/// </remarks>
internal enum Operation
{
    /// <summary>Opens an existing file for reading, and reads it.</summary>
    OpenFile,

    /// <summary>Opens a directory, and lists it.</summary>
    OpenDir,

    /// <summary>Creates or truncates a file for writing, and writes to it.</summary>
    CreateFile,

    /// <summary>Creates a directory.</summary>
    CreateDir,

    /// <summary>Describes what the name holds.</summary>
    GetMetadata,

    /// <summary>Sets the times of what the name holds, without following a link there.</summary>
    SetTimes,

    /// <summary>Asks whether the name is taken. Never throws.</summary>
    Exists,

    /// <summary>Reads the target a symbolic link stores.</summary>
    ReadLink,

    /// <summary>Removes a name that is not a directory.</summary>
    DeleteFile,

    /// <summary>Removes an empty directory.</summary>
    DeleteDir,

    /// <summary>Removes a directory and everything beneath it.</summary>
    DeleteTree,

    /// <summary>Moves the named entry to a fixed name beneath the root.</summary>
    RenameFrom,

    /// <summary>Moves a fixed file beneath the root onto the name.</summary>
    RenameTo,

    /// <summary>Creates a symbolic link at the name.</summary>
    CreateSymlinkAt,

    /// <summary>
    /// Creates a symbolic link at a fixed name that stores the path as its target, then opens
    /// the link. The path is attacker-controlled data a second way: written to disk now, and
    /// resolved by whoever follows the link later.
    /// </summary>
    CreateSymlinkTo,

    /// <summary>Gives the named entry a second name, at a fixed name beneath the root.</summary>
    HardLinkFrom,

    /// <summary>Gives a fixed file beneath the root a second name, at the name.</summary>
    HardLinkTo,

    /// <summary>Opens an existing file for reading, refusing a link at the name, and reads it.</summary>
    OpenFileNoFollow,

    /// <summary>Opens a directory, refusing a link at the name, and lists it.</summary>
    OpenDirNoFollow,

    /// <summary>Describes what the name holds, following a link there.</summary>
    GetMetadataFollowing,

    /// <summary>Sets the times of what the name holds, following a link there.</summary>
    SetTimesFollowing,

    /// <summary>
    /// Gives what the name holds a second name, at a fixed name beneath the root, following a
    /// link there.
    /// </summary>
    HardLinkFromFollowing,

    /// <summary>
    /// Opens whatever the name holds without saying which kind, then lists it if it is a
    /// directory or reads it if it is not.
    /// </summary>
    OpenAny,

    /// <summary>Opens whatever the name holds, refusing a link at the name, and lists or reads it.</summary>
    OpenAnyNoFollow,
}

/// <summary>
/// Where in a path the link that decided the outcome sits, which is what the stricter
/// symbolic-link policy changes.
/// </summary>
internal enum LinkRole
{
    /// <summary>No link is met on the way: the policy changes nothing.</summary>
    None,

    /// <summary>
    /// The last component is a link. The operations that follow one refuse it under the
    /// stricter policy; the ones that act on the name, or refuse a link there under every
    /// policy, are unaffected.
    /// </summary>
    Final,

    /// <summary>
    /// A link sits before the last component. Every operation has to pass through it, so under
    /// the stricter policy every operation is refused.
    /// </summary>
    Prefix,
}

/// <summary>
/// Properties of the host and its filesystem that a case may need, or whose presence changes
/// what a case should see.
/// </summary>
[Flags]
internal enum HostFeature
{
    None = 0,

    /// <summary>Symbolic links can be created here.</summary>
    Symlinks = 1 << 0,

    /// <summary>Hard links can be created here.</summary>
    HardLinks = 1 << 1,

    /// <summary>Windows junctions can be created here.</summary>
    Junctions = 1 << 2,

    /// <summary>The Linux process filesystem, and its synthetic links, are mounted.</summary>
    ProcessFilesystem = 1 << 3,

    /// <summary>Names differing only in case reach the same entry.</summary>
    CaseInsensitive = 1 << 4,

    /// <summary>The composed and decomposed spellings of a name reach the same entry.</summary>
    NormalizationInsensitive = 1 << 5,

    /// <summary>
    /// The volume stores a name containing the characters Windows reserves, as POSIX allows.
    /// Absent on a FAT volume, which refuses them even under Linux.
    /// </summary>
    PosixNames = 1 << 6,
}

/// <summary>One step of arranging the tree a case attacks.</summary>
/// <remarks>
/// Performed with the host's ordinary path-based API and never with the library under test. A
/// case arranged through the capability API would be attacking a tree that API had already
/// agreed to build.
/// </remarks>
/// <param name="Kind">What to create.</param>
/// <param name="Path">Where, relative to the sandbox root, with <c>/</c> as the separator.</param>
/// <param name="Target">
/// For a link, what it stores. <c>{outside}</c> and <c>{sandbox}</c> stand for the absolute
/// paths of the directory beside the sandbox and of the sandbox itself, and a relative target
/// is written with <c>/</c> and converted to the platform's separator.
/// </param>
internal sealed record SetupStep(SetupKind Kind, string Path, string? Target = null);

/// <summary>The kinds of thing a case can plant.</summary>
internal enum SetupKind
{
    Directory,
    File,

    /// <summary>A symbolic link recorded as naming a file.</summary>
    FileLink,

    /// <summary>
    /// A symbolic link recorded as naming a directory. The same thing as a file link except
    /// on Windows, which records the kind and will traverse only the right one.
    /// </summary>
    DirectoryLink,

    /// <summary>A Windows junction, whose target is always stored as an absolute path.</summary>
    Junction,
}

/// <summary>
/// A behaviour some backends are known to differ in, recorded rather than skipped.
/// </summary>
/// <remarks>
/// Recorded as the whole expectation those backends are held to instead, so a difference is
/// asserted exactly as tightly as the behaviour it replaces. A difference that stops being
/// true fails its test like any other wrong expectation, which is what gets the record removed
/// once the backends agree.
/// </remarks>
/// <param name="Backends">The backends it applies to.</param>
/// <param name="Expected">What those backends come to instead.</param>
/// <param name="Reason">Why, in terms a reader of the failure would need.</param>
internal sealed record KnownDifference(string[] Backends, Expectation Expected, string Reason);

/// <summary>
/// What each operation should come to for one path, before the policy and the host are taken
/// into account.
/// </summary>
internal sealed class Expectation
{
    private static readonly Operation[] FollowingOperations =
        [
            Operation.OpenFile, Operation.OpenDir, Operation.GetMetadataFollowing,
            Operation.SetTimesFollowing, Operation.HardLinkFromFollowing, Operation.OpenAny,
        ];

    private readonly Dictionary<Operation, Outcome> _outcomes;

    private Expectation(Dictionary<Operation, Outcome> outcomes, LinkRole role, string shape)
    {
        _outcomes = outcomes;
        Role = role;
        Shape = shape;
        DerivePerCallFollowing();
    }

    /// <summary>
    /// Fills in what the per-call forms come to from what the ordinary forms do, for any the
    /// expectation did not state.
    /// </summary>
    /// <remarks>
    /// Where no link holds the name, asking to follow one or not changes nothing, so each form
    /// comes to what its ordinary counterpart does. Where one does, refusing to follow it is
    /// refused, and following it reaches what opening it reaches: described and timed
    /// wherever it leads to a file or a directory, given a second name only for a file, and
    /// otherwise refused, missing or an escape exactly as opening it is.
    /// </remarks>
    private void DerivePerCallFollowing()
    {
        bool final = Role == LinkRole.Final;
        Outcome asFile = _outcomes[Operation.OpenFile];
        Outcome asDirectory = _outcomes[Operation.OpenDir];
        Outcome reached = asFile == Outcome.Success || asDirectory == Outcome.Success ? Outcome.Success : asFile;
        Outcome linked = asFile == Outcome.Success ? Outcome.Success
            : asDirectory == Outcome.Success ? Outcome.Refused
            : asFile;

        // Opening whatever is there reaches it if either kind of open would; otherwise it
        // fails as a directory open does, since a path spelled as a directory is the one
        // shape where the two disagree without either succeeding, and it opens only one.
        Outcome either = asFile == Outcome.Success || asDirectory == Outcome.Success ? Outcome.Success : asDirectory;

        _outcomes.TryAdd(Operation.OpenAny, either);
        _outcomes.TryAdd(Operation.OpenAnyNoFollow, final ? Outcome.Refused : either);
        _outcomes.TryAdd(Operation.OpenFileNoFollow, final ? Outcome.Refused : asFile);
        _outcomes.TryAdd(Operation.OpenDirNoFollow, final ? Outcome.Refused : asDirectory);
        _outcomes.TryAdd(Operation.GetMetadataFollowing, final ? reached : _outcomes[Operation.GetMetadata]);
        _outcomes.TryAdd(Operation.SetTimesFollowing, final ? reached : _outcomes[Operation.SetTimes]);
        _outcomes.TryAdd(Operation.HardLinkFromFollowing, final ? linked : _outcomes[Operation.HardLinkFrom]);
    }

    /// <summary>Where the link that decides the outcome sits.</summary>
    public LinkRole Role { get; }

    /// <summary>A short description, for failure messages.</summary>
    public string Shape { get; }

    /// <summary>The outcome for one operation under the policy that follows links.</summary>
    public Outcome this[Operation operation] => _outcomes[operation];

    /// <summary>
    /// Every operation comes to the same outcome, decided before anything is looked up, or by
    /// something every operation has to pass through.
    /// </summary>
    public static Expectation Uniform(Outcome outcome, LinkRole role = LinkRole.None)
    {
        Dictionary<Operation, Outcome> outcomes = [];
        foreach (Operation operation in Enum.GetValues<Operation>())
        {
            outcomes[operation] = outcome;
        }

        // Asking never throws. It says yes only for a name that is there.
        outcomes[Operation.Exists] = outcome == Outcome.Success ? Outcome.Success : Outcome.NotFound;
        return new(outcomes, role, $"uniformly {outcome}");
    }

    /// <summary>The path names an ordinary file beneath the root.</summary>
    public static Expectation ExistingFile() => new(
        new()
        {
            [Operation.OpenFile] = Outcome.Success,
            [Operation.OpenDir] = Outcome.Refused,
            [Operation.CreateFile] = Outcome.Success,
            [Operation.CreateDir] = Outcome.Refused,
            [Operation.GetMetadata] = Outcome.Success,
            [Operation.SetTimes] = Outcome.Success,
            [Operation.Exists] = Outcome.Success,
            [Operation.ReadLink] = Outcome.Refused,
            [Operation.DeleteFile] = Outcome.Success,
            [Operation.DeleteDir] = Outcome.Refused,
            [Operation.DeleteTree] = Outcome.Refused,
            [Operation.RenameFrom] = Outcome.Success,
            [Operation.RenameTo] = Outcome.Refused,
            [Operation.CreateSymlinkAt] = Outcome.Refused,
            [Operation.CreateSymlinkTo] = Outcome.Success,
            [Operation.HardLinkFrom] = Outcome.Success,
            [Operation.HardLinkTo] = Outcome.Refused,
        },
        LinkRole.None,
        "an existing file");

    /// <summary>The path names a directory beneath the root, with something in it.</summary>
    public static Expectation ExistingDirectory() => new(
        new()
        {
            [Operation.OpenFile] = Outcome.Refused,
            [Operation.OpenDir] = Outcome.Success,
            [Operation.CreateFile] = Outcome.Refused,
            [Operation.CreateDir] = Outcome.Refused,
            [Operation.GetMetadata] = Outcome.Success,
            [Operation.SetTimes] = Outcome.Success,
            [Operation.Exists] = Outcome.Success,
            [Operation.ReadLink] = Outcome.Refused,
            [Operation.DeleteFile] = Outcome.Refused,
            [Operation.DeleteDir] = Outcome.Refused,
            [Operation.DeleteTree] = Outcome.Success,
            [Operation.RenameFrom] = Outcome.Success,
            [Operation.RenameTo] = Outcome.Refused,
            [Operation.CreateSymlinkAt] = Outcome.Refused,
            [Operation.CreateSymlinkTo] = Outcome.Refused,
            [Operation.HardLinkFrom] = Outcome.Refused,
            [Operation.HardLinkTo] = Outcome.Refused,
        },
        LinkRole.None,
        "an existing directory");

    /// <summary>The path names nothing, in a directory beneath the root that exists.</summary>
    public static Expectation Absent() => new(
        new()
        {
            [Operation.OpenFile] = Outcome.NotFound,
            [Operation.OpenDir] = Outcome.NotFound,
            [Operation.CreateFile] = Outcome.Success,
            [Operation.CreateDir] = Outcome.Success,
            [Operation.GetMetadata] = Outcome.NotFound,
            [Operation.SetTimes] = Outcome.NotFound,
            [Operation.Exists] = Outcome.NotFound,
            [Operation.ReadLink] = Outcome.NotFound,
            [Operation.DeleteFile] = Outcome.NotFound,
            [Operation.DeleteDir] = Outcome.NotFound,
            [Operation.DeleteTree] = Outcome.NotFound,
            [Operation.RenameFrom] = Outcome.NotFound,
            [Operation.RenameTo] = Outcome.Success,
            [Operation.CreateSymlinkAt] = Outcome.Success,
            [Operation.CreateSymlinkTo] = Outcome.NotFound,
            [Operation.HardLinkFrom] = Outcome.NotFound,
            [Operation.HardLinkTo] = Outcome.Success,
        },
        LinkRole.None,
        "an absent name");

    /// <summary>
    /// The last component is a symbolic link. Following it comes to the outcomes given; every
    /// operation that acts on the name acts on the link itself, and creating a file there is
    /// refused without the link being read, wherever it points.
    /// </summary>
    /// <param name="asFile">Opening what the link leads to as a file, and following a chain to it.</param>
    /// <param name="asDirectory">Opening what it leads to as a directory.</param>
    public static Expectation FinalLink(Outcome asFile, Outcome asDirectory) => new(
        new()
        {
            [Operation.OpenFile] = asFile,
            [Operation.OpenDir] = asDirectory,
            [Operation.CreateFile] = Outcome.Refused,
            [Operation.CreateDir] = Outcome.Refused,
            [Operation.GetMetadata] = Outcome.Success,
            [Operation.SetTimes] = Outcome.Success,
            [Operation.Exists] = Outcome.Success,
            [Operation.ReadLink] = Outcome.Success,
            [Operation.DeleteFile] = Outcome.Success,
            [Operation.DeleteDir] = Outcome.Refused,
            [Operation.DeleteTree] = Outcome.Refused,
            [Operation.RenameFrom] = Outcome.Success,
            [Operation.RenameTo] = Outcome.Refused,
            [Operation.CreateSymlinkAt] = Outcome.Refused,
            [Operation.CreateSymlinkTo] = asFile,
            [Operation.HardLinkFrom] = Outcome.Success,
            [Operation.HardLinkTo] = Outcome.Refused,
        },
        LinkRole.Final,
        $"a link whose target opens as {asFile}");

    /// <summary>A final link whose following is refused the same way whatever is asked.</summary>
    public static Expectation FinalLink(Outcome followed) => FinalLink(followed, followed);

    /// <summary>
    /// A link sits before the last component, and following it leads to a place where the
    /// path comes to the outcomes given.
    /// </summary>
    public static Expectation Through(Expectation beyond) =>
        new(new(beyond._outcomes), LinkRole.Prefix, $"through a link to {beyond.Shape}");

    /// <summary>A copy with some outcomes replaced.</summary>
    public Expectation With(Operation operation, Outcome outcome, params (Operation Operation, Outcome Outcome)[] more)
    {
        Dictionary<Operation, Outcome> outcomes = new(_outcomes) { [operation] = outcome };
        foreach ((Operation Operation, Outcome Outcome) extra in more)
        {
            outcomes[extra.Operation] = extra.Outcome;
        }

        return new(outcomes, Role, Shape);
    }

    /// <summary>
    /// The outcome once the handle's policy and the host's features are taken into account.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <param name="denyLinks">Whether the handle refuses every symbolic link.</param>
    /// <param name="features">What the host can do.</param>
    public Outcome Resolve(Operation operation, bool denyLinks, HostFeature features)
    {
        Outcome outcome = _outcomes[operation];

        if (denyLinks && outcome != Outcome.Malformed)
        {
            // The stricter policy refuses a link wherever it has to be followed, and says so
            // as a refusal of the link rather than as an escape: the link is never read, so
            // where it pointed is never learned.
            bool follows = Role switch
            {
                LinkRole.Prefix => true,
                LinkRole.Final => Array.IndexOf(FollowingOperations, operation) >= 0,
                _ => false,
            };

            if (follows)
            {
                outcome = operation == Operation.Exists ? Outcome.NotFound : Outcome.Refused;
            }

            // The link this operation makes is itself a link, and the handle refuses to
            // follow it whatever it stores.
            if (operation == Operation.CreateSymlinkTo)
            {
                outcome = Outcome.Refused;
            }
        }

        if (outcome == Outcome.Success)
        {
            // A filesystem without links or without hard links has to refuse to make one,
            // rather than half-succeed.
            if ((features & HostFeature.Symlinks) == 0 && operation == Operation.CreateSymlinkAt)
            {
                outcome = Outcome.Refused;
            }

            if ((features & HostFeature.HardLinks) == 0 &&
                operation is Operation.HardLinkFrom or Operation.HardLinkTo or Operation.HardLinkFromFollowing)
            {
                outcome = Outcome.Refused;
            }
        }

        return outcome;
    }
}

/// <summary>
/// One attack: a tree, a path into it, and what every operation on that path must come to.
/// </summary>
/// <param name="Name">A stable name, used to select the case and in failure messages.</param>
/// <param name="Threats">The rows of the threat model this case defends.</param>
/// <param name="Path">The path handed to each operation.</param>
/// <param name="Unix">What POSIX path rules make of it.</param>
/// <param name="Windows">What Windows path rules make of it.</param>
internal sealed record EscapeCase(
    string Name,
    string[] Threats,
    string Path,
    Expectation? Unix,
    Expectation? Windows)
{
    /// <summary>What to plant beneath the sandbox root before the operation runs.</summary>
    public SetupStep[] Setup { get; init; } = [];

    /// <summary>Host features without which the case cannot be arranged.</summary>
    public HostFeature Requires { get; init; }

    /// <summary>
    /// A host feature that, when present, makes the path mean something else — a volume that
    /// folds case or normalisation reaching an entry that on another volume is simply absent.
    /// </summary>
    public HostFeature FoldedBy { get; init; }

    /// <summary>The expectation on a host with <see cref="FoldedBy"/>.</summary>
    public Expectation? WhenFolded { get; init; }

    /// <summary>Where a backend is known to behave differently, and why.</summary>
    public KnownDifference[] Differences { get; init; } = [];

    /// <summary>Whether the case applies to this host at all.</summary>
    public bool AppliesToHost => (OperatingSystem.IsWindows() ? Windows : Unix) is not null;

    /// <summary>The expectation for this host before the policy is applied.</summary>
    public Expectation ExpectationFor(HostFeature features)
    {
        if (FoldedBy != HostFeature.None && (features & FoldedBy) != 0 && WhenFolded is not null)
        {
            return WhenFolded;
        }

        return (OperatingSystem.IsWindows() ? Windows : Unix)
            ?? throw new InvalidOperationException($"'{Name}' has no expectation on this host.");
    }
}
