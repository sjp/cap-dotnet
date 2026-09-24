using Cap.Primitives;
using Cap.Std;

namespace Cap.Escape.Tests;

/// <summary>
/// Creating a symbolic link refuses a rooted target, on every backend, under both policies and
/// for both kinds of link, and stores any relative target as given — one that climbs out
/// included.
/// </summary>
/// <remarks>
/// <para>
/// A link that leaves the subtree is refused whenever resolution beneath a handle meets it, so
/// what is at stake here is not an escape through this library. A link persists on disk,
/// where a shell, a backup job or a web server serving the same tree will follow it wherever it
/// points. A rooted target names somewhere outside from wherever the link sits, so it is
/// refused from its text. Whether a relative one climbs out depends on where the link sits,
/// which a later rename can change, so it is stored and refused only when followed.
/// </para>
/// <para>
/// The case table drives link creation with every path it holds as the target, and checks that
/// a rooted one leaves no link behind. The cases here add the rooted spellings a table path is
/// never written in, the kind of link, and the reporting overloads.
/// </para>
/// </remarks>
[Collection(CorpusGroup.Name)]
public sealed class CreatedLinkTargetTests
{
    private const string LinkName = "created";

    /// <summary>Targets rooted under the running platform's rules.</summary>
    private static string[] RootedTargets =>
        OperatingSystem.IsWindows()
            ?
            [
                @"C:\", @"C:\Windows", "C:/Windows", "C:win.ini", @"\Windows", "/Windows",
                @"\\server\share", "//server/share", @"\\?\C:\Windows", @"\\.\PhysicalDrive0",
                "{outside}",
            ]
            : ["/", "/etc/passwd", "//server/share", "{outside}", "{sandbox}"];

    public static TheoryData<string, SymlinkPolicy, bool, string> RootedRows
    {
        get
        {
            TheoryData<string, SymlinkPolicy, bool, string> rows = [];
            foreach (string backend in Backends.OnThisHost)
            {
                foreach (SymlinkPolicy policy in new[] { SymlinkPolicy.FollowWithinSandbox, SymlinkPolicy.Deny })
                {
                    foreach (bool directory in new[] { false, true })
                    {
                        foreach (string target in RootedTargets)
                        {
                            rows.Add(backend, policy, directory, target);
                        }
                    }
                }
            }

            return rows;
        }
    }

    public static TheoryData<string, SymlinkPolicy> BackendsAndPolicies
    {
        get
        {
            TheoryData<string, SymlinkPolicy> rows = [];
            foreach (string backend in Backends.OnThisHost)
            {
                rows.Add(backend, SymlinkPolicy.FollowWithinSandbox);
                rows.Add(backend, SymlinkPolicy.Deny);
            }

            return rows;
        }
    }

    /// <summary>
    /// A rooted target is refused as leaving the subtree, by both the throwing and the
    /// reporting overload, and nothing is created inside or outside.
    /// </summary>
    [Theory]
    [MemberData(nameof(RootedRows))]
    [Defends("S16")]
    public void A_rooted_target_is_refused_and_nothing_is_created(
        string backend, SymlinkPolicy policy, bool directory, string target)
    {
        RequireSymlinks();

        using Arena arena = new();
        string expanded = arena.Expand(target);
        string before = arena.SnapshotSandbox();
        string outside = arena.SnapshotOutside();

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), policy);

        SandboxEscapeException refusal = Assert.Throws<SandboxEscapeException>(() =>
        {
            if (directory)
            {
                root.CreateDirSymlink(LinkName, expanded);
            }
            else
            {
                root.CreateSymlink(LinkName, expanded);
            }
        });
        Assert.Contains(expanded, refusal.Message, StringComparison.Ordinal);

        Assert.False(directory
            ? root.TryCreateDirSymlink(LinkName, expanded)
            : root.TryCreateSymlink(LinkName, expanded));

        Assert.False(arena.ExistsInside(LinkName));
        Assert.Equal(before, arena.SnapshotSandbox());
        Assert.Equal(outside, arena.SnapshotOutside());
    }

    /// <summary>
    /// A relative target that climbs out of the subtree is stored as given, and refused when
    /// something beneath the handle follows it.
    /// </summary>
    [Theory]
    [MemberData(nameof(BackendsAndPolicies))]
    [Defends("S16")]
    public void A_relative_target_that_climbs_out_is_stored_and_refused_when_followed(
        string backend, SymlinkPolicy policy)
    {
        RequireSymlinks();

        using Arena arena = new();
        string target = $"../{EscapeCorpus.OutsideDirectory}/{EscapeCorpus.OutsideFile}";
        string outside = arena.SnapshotOutside();

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), policy);

        root.CreateSymlink(LinkName, target);
        Assert.True(root.TryCreateSymlink("nested-" + LinkName, $"{EscapeCorpus.PlainDirectory}/../../escaped"));

        Assert.Equal(target, root.ReadLink(LinkName).Replace('\\', '/'));
        _ = Assert.ThrowsAny<IOException>(() => root.ReadAllText(LinkName));
        if (policy == SymlinkPolicy.FollowWithinSandbox)
        {
            _ = Assert.Throws<SandboxEscapeException>(() => root.ReadAllText(LinkName));
        }

        Assert.Equal(outside, arena.SnapshotOutside());
    }

    /// <summary>
    /// A target inside is stored as given, under either policy, and followed by a handle
    /// that follows links inside.
    /// </summary>
    [Theory]
    [MemberData(nameof(BackendsAndPolicies))]
    [Defends("S16")]
    public void A_target_inside_is_stored_and_followed(string backend, SymlinkPolicy policy)
    {
        RequireSymlinks();

        using Arena arena = new();
        string file = $"{EscapeCorpus.PlainDirectory}/{EscapeCorpus.PlainFile}";

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), policy);

        root.CreateSymlink(LinkName, file);
        root.CreateDirSymlink("directory-" + LinkName, EscapeCorpus.PlainDirectory);

        Assert.Equal(file, root.ReadLink(LinkName).Replace('\\', '/'));
        if (policy == SymlinkPolicy.FollowWithinSandbox)
        {
            Assert.Equal(EscapeCorpus.InsideContent, root.ReadAllText(LinkName));
            Assert.Equal(
                EscapeCorpus.InsideContent,
                root.ReadAllText($"directory-{LinkName}/{EscapeCorpus.PlainFile}"));
        }
    }

    /// <summary>
    /// A spelling that is rooted only under the other platform's rules is an ordinary relative
    /// name here, and is stored.
    /// </summary>
    [Theory]
    [MemberData(nameof(BackendsAndPolicies))]
    [Defends("S16")]
    public void A_target_rooted_only_under_the_other_platforms_rules_is_stored(string backend, SymlinkPolicy policy)
    {
        RequireSymlinks();
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Every spelling rooted under POSIX rules is rooted under Windows ones too.");

        using Arena arena = new();

        using BackendScope scope = Backends.Enter(backend);
        using Dir root = Dir.Open(arena.SandboxPath, AmbientAuthority.Acquire(), policy);

        string[] targets = [@"C:\Windows", "C:win.ini", @"\Windows"];
        for (int i = 0; i < targets.Length; i++)
        {
            root.CreateSymlink($"link-{i}", targets[i]);
            Assert.Equal(targets[i], root.ReadLink($"link-{i}"));
        }
    }

    private static void RequireSymlinks()
    {
        if ((HostFeatures.Current & HostFeature.Symlinks) == 0)
        {
            Assert.Skip("This host or volume cannot hold symbolic links.");
        }
    }
}
