using System.Text;
using Cap.Primitives;

namespace Cap.Std.Testing.Tests;

/// <summary>
/// The walk and the confined open reach the same answers: the same script run against two
/// identically seeded filesystems, one per resolution, succeeds and fails at the same steps
/// in the same way and leaves the same tree behind. The one documented difference, the
/// confined open's 4096-unit path limit, is pinned on its own.
/// </summary>
public sealed class ResolutionAgreementTests
{
    private static readonly CapFileTime Written =
        CapFileTime.At(new DateTimeOffset(2010, 6, 1, 12, 0, 0, TimeSpan.Zero));

    public static TheoryData<CapPathSyntax, SymlinkPolicy> Cases => new()
    {
        { CapPathSyntax.Unix, SymlinkPolicy.FollowWithinSandbox },
        { CapPathSyntax.Unix, SymlinkPolicy.Deny },
        { CapPathSyntax.Windows, SymlinkPolicy.FollowWithinSandbox },
        { CapPathSyntax.Windows, SymlinkPolicy.Deny },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_walk_and_the_confined_open_agree_on_every_step(CapPathSyntax syntax, SymlinkPolicy policy)
    {
        InMemoryFileSystem walk = Seed(syntax, ResolutionBackend.PortableWalk);
        InMemoryFileSystem confined = Seed(syntax, ResolutionBackend.ConfinedOpen);

        List<string> walked = Run(walk, policy);
        List<string> opened = Run(confined, policy);

        Assert.Equal(walked.Count, opened.Count);
        for (int i = 0; i < walked.Count; i++)
        {
            Assert.True(
                walked[i] == opened[i],
                $"Step {i} differs.\n  walk:     {walked[i]}\n  confined: {opened[i]}");
        }

        // Snapshot.Diff compares volumes too, and every filesystem is a volume of its own.
        Assert.Equal(Tree(walk), Tree(confined));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Only_the_confined_open_refuses_a_path_of_4096_units(CapPathSyntax syntax, SymlinkPolicy policy)
    {
        string path = string.Concat(Enumerable.Repeat("a/../", 820)) + "a/file.txt";
        Assert.True(path.Length >= 4096);

        using (Dir root = Seed(syntax, ResolutionBackend.PortableWalk).OpenRoot(policy))
        {
            Assert.Equal("hello", root.ReadAllText(path));
        }

        using (Dir root = Seed(syntax, ResolutionBackend.ConfinedOpen).OpenRoot(policy))
        {
            Exception refused = Assert.ThrowsAny<IOException>(() => root.ReadAllText(path));
            Assert.Equal(CapErrorKind.NameTooLong, CapIOException.KindOf(refused));
        }
    }

    private static InMemoryFileSystem Seed(CapPathSyntax syntax, ResolutionBackend resolution)
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = syntax, Resolution = resolution });

        fs.AddFile("a/file.txt", "hello");
        fs.AddFile("a/b/deep.txt", "deep");
        fs.AddDirectory("empty");
        fs.AddDirectory("inner");
        fs.AddSymbolicLink("rel", "a/file.txt");
        fs.AddSymbolicLink("chain", "rel");
        fs.AddSymbolicLink("inner/up", "../a/file.txt");
        fs.AddSymbolicLink("rooted", "/a/file.txt");
        fs.AddSymbolicLink("dangling", "missing.txt");
        fs.AddSymbolicLink("dirlink", "a", targetIsDirectory: true);
        fs.AddSymbolicLink("out", "../../outside.txt");
        fs.AddSymbolicLink("loop1", "loop2");
        fs.AddSymbolicLink("loop2", "loop1");
        fs.AddHardLink("hard.txt", "a/file.txt");
        fs.AddFile("ro.txt", "read only");
        fs.AddFile("keep.txt", "kept");
        fs.AddFile("secret.txt", "hidden");
        fs.SetUndeletable("keep.txt");
        fs.SetUnreadable("secret.txt");
        if (syntax == CapPathSyntax.Windows)
        {
            fs.SetAttributes("ro.txt", FileAttributes.ReadOnly);
        }
        else
        {
            fs.SetUnixMode("ro.txt", UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return fs;
    }

    /// <summary>
    /// Runs every step of the script in order, each seeing what the steps before it left, and
    /// records what each answered.
    /// </summary>
    private static List<string> Run(InMemoryFileSystem fs, SymlinkPolicy policy)
    {
        List<string> transcript = [];
        using Dir root = fs.OpenRoot(policy);
        foreach ((string name, Func<Dir, object?> step) in Script)
        {
            string outcome;
            try
            {
                outcome = "ok " + Describe(step(root));
            }
            catch (Exception e)
            {
                outcome = $"{e.GetType().Name}:{CapIOException.KindOf(e)}";
            }

            transcript.Add($"{name} → {outcome}");
        }

        return transcript;
    }

    /// <summary>Every name in the tree and everything about what it names but its volume.</summary>
    private static List<string> Tree(InMemoryFileSystem fs) =>
        fs.Snapshot().Select(pair =>
        {
            CapMetadata metadata = pair.Value.Metadata;
            return $"{pair.Key}: {Describe(metadata)} node={metadata.FileId.NodeId} " +
                   $"permissions={metadata.Permissions} target={pair.Value.LinkTarget} " +
                   $"contents={Convert.ToHexString(pair.Value.Contents.Span)}";
        }).ToList();

    private static string Describe(object? result) => result switch
    {
        null => string.Empty,
        string text => $"\"{text}\"",
        byte[] bytes => Convert.ToHexString(bytes),
        CapMetadata metadata => $"{metadata.Type} len={metadata.Length} links={metadata.LinkCount} " +
                                $"written={metadata.LastWriteTime:O}",
        IEnumerable<string> items => "[" + string.Join(", ", items) + "]",
        _ => result.ToString() ?? string.Empty,
    };

    private static List<string> List(Dir dir) =>
        dir.EnumerateEntries().Select(entry => $"{entry.Name}:{entry.Type}").Order(StringComparer.Ordinal).ToList();

    private static object? Opened(Dir dir)
    {
        dir.Dispose();
        return null;
    }

    /// <summary>
    /// Reads first, then changes, so that each read sees the seeded tree and each change sees
    /// what the changes before it left.
    /// </summary>
    private static readonly (string Name, Func<Dir, object?> Step)[] Script =
    [
        ("read plain", root => root.ReadAllText("a/file.txt")),
        ("read other case", root => root.ReadAllText("A/FILE.TXT")),
        ("read backslash", root => root.ReadAllText(@"a\file.txt")),
        ("read relative link", root => root.ReadAllText("rel")),
        ("read chained link", root => root.ReadAllText("chain")),
        ("read link up and back", root => root.ReadAllText("inner/up")),
        ("read rooted link", root => root.ReadAllText("rooted")),
        ("read dangling link", root => root.ReadAllText("dangling")),
        ("read through directory link", root => root.ReadAllText("dirlink/b/deep.txt")),
        ("read escaping link", root => root.ReadAllText("out")),
        ("read link loop", root => root.ReadAllText("loop1")),
        ("read hard link", root => root.ReadAllText("hard.txt")),
        ("read dot-dot inside", root => root.ReadAllText("a/b/../file.txt")),
        ("read dot-dot above", root => root.ReadAllText("../a/file.txt")),
        ("read dot-dot above from deep", root => root.ReadAllText("a/b/../../../a/file.txt")),
        ("read dot-dot through link", root => root.ReadAllText("dirlink/../a/file.txt")),
        ("read file with trailing separator", root => root.ReadAllText("a/file.txt/")),
        ("read through a file", root => root.ReadAllText("a/file.txt/x")),
        ("read directory", root => root.ReadAllText("a")),
        ("read unreadable", root => root.ReadAllText("secret.txt")),
        ("read missing", root => root.ReadAllText("a/none.txt")),
        ("read under missing", root => root.ReadAllText("none/file.txt")),
        ("exists link", root => root.Exists("rel")),
        ("exists dangling", root => root.Exists("dangling")),
        ("exists escaping", root => root.Exists("out")),
        ("open directory with trailing separator", root => Opened(root.OpenDir("a/"))),
        ("open directory link", root => Opened(root.OpenDir("dirlink"))),
        ("open directory link without following", root => Opened(root.OpenDir("dirlink", noFollow: true))),
        ("open file as directory", root => Opened(root.OpenDir("a/file.txt"))),
        ("list top", root => List(root)),
        ("list through directory link", root =>
        {
            using Dir linked = root.OpenDir("dirlink");
            return List(linked);
        }),
        ("describe link", root => root.GetMetadata("rel")),
        ("describe link followed", root => root.GetMetadata("rel", followLink: true)),
        ("describe chained link followed", root => root.GetMetadata("chain", followLink: true)),
        ("describe dangling followed", root => root.GetMetadata("dangling", followLink: true)),
        ("describe escaping", root => root.GetMetadata("out")),
        ("describe escaping followed", root => root.GetMetadata("out", followLink: true)),
        ("describe unreadable", root => root.GetMetadata("secret.txt")),
        ("describe directory with trailing separator", root => root.GetMetadata("a/")),
        ("describe file with trailing separator", root => root.GetMetadata("a/file.txt/")),
        ("read link", root => root.ReadLink("inner/up")),
        ("read link of a file", root => root.ReadLink("a/file.txt")),
        ("create new over file", root => { root.CreateNewFile("a/file.txt").Dispose(); return null; }),
        ("create new over link", root => { root.CreateNewFile("rel").Dispose(); return null; }),
        ("create new over dangling link", root => { root.CreateNewFile("dangling").Dispose(); return null; }),
        ("write through dangling link", root => { root.WriteAllBytes("dangling", [1]); return null; }),
        ("write through link", root => { root.WriteAllBytes("rel", [2]); return null; }),
        ("open existing through link", root =>
        {
            using CapFile file = root.OpenFile("chain", FileMode.Open, FileAccess.ReadWrite);
            file.Write([3], 0);
            return file.GetMetadata();
        }),
        ("write under missing", root => { root.WriteAllBytes("none/file.txt", [1]); return null; }),
        ("write read-only", root => { root.WriteAllBytes("ro.txt", [1]); return null; }),
        ("write unreadable", root => { root.WriteAllBytes("secret.txt", [1]); return null; }),
        ("write 255-byte name", root => { root.WriteAllBytes(new string('n', 255), [1]); return null; }),
        ("write 256-byte name", root => { root.WriteAllBytes(new string('m', 256), [1]); return null; }),
        ("write 400-byte name", root => { root.WriteAllBytes(new string('o', 400), [1]); return null; }),
        ("write 128 two-byte characters", root => { root.WriteAllBytes(new string('é', 128), [1]); return null; }),
        ("write new file", root => { root.WriteAllBytes("a/new.txt", Encoding.UTF8.GetBytes("new")); return null; }),
        ("write with dot-dot above", root => { root.WriteAllBytes("../escaped.txt", [1]); return null; }),
        ("write through escaping link", root => { root.WriteAllBytes("out", [1]); return null; }),
        ("create existing directory", root => Opened(root.CreateDir("a"))),
        ("create directory with trailing separator", root => Opened(root.CreateDir("made/"))),
        ("create directory through link", root => Opened(root.CreateDir("dirlink/sub"))),
        ("create directory over link", root => Opened(root.CreateDir("rel"))),
        ("create directory over dangling link", root => Opened(root.CreateDir("dangling"))),
        ("create directory under missing", root => Opened(root.CreateDir("none/sub"))),
        ("create directories all", root => Opened(root.OpenOrCreateDirAll("x/y/z"))),
        ("create directories all through link", root => Opened(root.OpenOrCreateDirAll("dirlink/p/q"))),
        ("open or create existing directory", root => Opened(root.OpenOrCreateDir("a/b"))),
        ("delete non-empty directory", root => { root.DeleteDir("a"); return null; }),
        ("delete directory link as directory", root => { root.DeleteDir("dirlink"); return null; }),
        ("delete empty directory", root => { root.DeleteDir("empty"); return null; }),
        ("delete directory with trailing separator", root => { root.DeleteDir("made/"); return null; }),
        ("delete directory as file", root => { root.DeleteFile("x"); return null; }),
        ("delete file as directory", root => { root.DeleteDir("hard.txt"); return null; }),
        ("delete undeletable", root => { root.DeleteFile("keep.txt"); return null; }),
        ("delete read-only", root => { root.DeleteFile("ro.txt"); return null; }),
        ("delete through directory link", root => { root.DeleteFile("dirlink/b/deep.txt"); return null; }),
        ("delete missing", root => { root.DeleteFile("none.txt"); return null; }),
        ("delete dot-dot above", root => { root.DeleteFile("../a/file.txt"); return null; }),
        ("delete held open without sharing delete", root =>
        {
            using CapFile held = root.OpenFile("a/new.txt", FileMode.Open, FileAccess.Read, FileShare.None);
            root.DeleteFile("a/new.txt");
            return null;
        }),
        ("open held open without sharing", root =>
        {
            using CapFile held = root.OpenFile("hard.txt", FileMode.Open, FileAccess.Read, FileShare.None);
            root.OpenFile("a/file.txt").Dispose();
            return null;
        }),
        ("rename onto existing", root => { root.Rename("hard.txt", root, "secret.txt"); return null; }),
        ("rename onto another name of itself", root => { root.Rename("hard.txt", root, "a/file.txt"); return null; }),
        ("rename onto existing replacing", root => { root.Rename("hard.txt", root, "a/file.txt", replaceExisting: true); return null; }),
        ("rename into itself", root => { root.Rename("a", root, "a/b/inside"); return null; }),
        ("rename directory onto file", root => { root.Rename("x", root, "a/file.txt", replaceExisting: true); return null; }),
        ("rename file onto non-empty directory", root => { root.Rename("a/file.txt", root, "x", replaceExisting: true); return null; }),
        ("rename through directory link", root => { root.Rename("dirlink/b", root, "moved"); return null; }),
        ("rename link", root => { root.Rename("chain", root, "inner/chain"); return null; }),
        ("rename only case", root => { root.Rename("inner", root, "INNER"); return null; }),
        ("rename undeletable", root => { root.Rename("keep.txt", root, "kept.txt"); return null; }),
        ("rename onto undeletable", root => { root.Rename("loop1", root, "keep.txt", replaceExisting: true); return null; }),
        ("rename out", root => { root.Rename("moved", root, "../moved"); return null; }),
        ("rename missing", root => { root.Rename("none", root, "other"); return null; }),
        ("make link", root => { root.CreateSymlink("made-link", "a/file.txt"); return root.ReadLink("made-link"); }),
        ("make link over existing", root => { root.CreateSymlink("rel", "a/file.txt"); return null; }),
        ("make escaping link", root => { root.CreateSymlink("made-out", "../outside"); return null; }),
        ("make directory link", root =>
        {
            root.CreateDirSymlink("made-dirlink", "x/y");
            using Dir linked = root.OpenDir("made-dirlink");
            return List(linked);
        }),
        ("make hard link following", root => { root.CreateHardLink("rel", root, "hard2.txt", followLink: true); return root.GetMetadata("hard2.txt"); }),
        ("make hard link to link", root => { root.CreateHardLink("rel", root, "hard3"); return root.GetMetadata("hard3"); }),
        ("make hard link to directory", root => { root.CreateHardLink("x", root, "xlink"); return null; }),
        ("make hard link over existing", root => { root.CreateHardLink("a/file.txt", root, "ro.txt"); return null; }),
        ("set times", root => { root.SetTimes("a/file.txt", lastWrite: Written); return root.GetMetadata("a/file.txt"); }),
        ("set times on link", root => { root.SetTimes("rel", lastWrite: Written); return root.GetMetadata("rel"); }),
        ("set times through link", root => { root.SetTimes("rel", lastWrite: Written, followLink: true); return root.GetMetadata("rel", followLink: true); }),
        ("set times on unreadable", root => { root.SetTimes("secret.txt", lastWrite: Written); return null; }),
        ("set times on escaping", root => { root.SetTimes("out", lastWrite: Written, followLink: true); return null; }),
        ("list top at the end", root => List(root)),
    ];
}
