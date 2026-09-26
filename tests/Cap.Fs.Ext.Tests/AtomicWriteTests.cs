using System.Diagnostics;
using System.Text;
using Cap.Std;

namespace Cap.Fs.Ext.Tests;

/// <summary>
/// Publishing a file by writing it somewhere else first.
/// </summary>
/// <remarks>
/// <para>
/// The property under test is negative and therefore easy to write a test that does not
/// check it: a publish that simply worked would pass every assertion about the contents
/// afterwards. So what is asserted here is what a reader can see <em>while</em> the publish
/// is happening, what is left behind when it fails, and where the scratch file is put —
/// which is the part that decides whether the move at the end is a move at all.
/// </para>
/// <para>
/// The set-up and the checking are done with ambient <c>System.IO</c> on purpose. A test that
/// looked at the result through the capability API would be asking the code under test
/// whether it had done what it said.
/// </para>
/// </remarks>
public sealed class AtomicWriteTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>A published file holds what it was given.</summary>
    [Theory]
    [InlineData(Durability.None)]
    [InlineData(Durability.File)]
    [InlineData(Durability.FileAndDirectory)]
    public void A_published_file_holds_what_it_was_given(Durability durability)
    {
        byte[] contents = [1, 2, 3, 4, 5];

        _tree.Directory.WriteAllBytesAtomic("report", contents, durability);

        Assert.Equal(contents, HostFile.ReadAllBytes(Path.Combine(_tree.HostPath, "report")));
    }

    /// <summary>A path that climbs and descends again, staying inside, publishes where it leads.</summary>
    [Fact]
    public void A_path_that_climbs_and_stays_inside_publishes_where_it_leads()
    {
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "a", "b"));

        _tree.Directory.WriteAllTextAtomic("a/b/../report", "published");

        Assert.Equal("published", HostFile.ReadAllText(Path.Combine(_tree.HostPath, "a", "report")));
        Assert.Throws<SandboxEscapeException>(() => _tree.Directory.WriteAllTextAtomic("a/../../report", "leaked"));
        Assert.Throws<SandboxEscapeException>(() => _tree.Directory.WriteAllTextAtomic("..", "leaked"));
        Assert.Throws<ArgumentException>(() => _tree.Directory.WriteAllTextAtomic("a/..", "nowhere"));
    }

    /// <summary>
    /// Asking for the strongest durability is not refused anywhere.
    /// </summary>
    /// <remarks>
    /// The default, so a platform that reported the last step as unsupported would make the
    /// default form of the operation fail there. That is the behaviour the documentation
    /// promises and the reason it is worth a test of its own: the answer differs by platform
    /// and the promise does not.
    /// </remarks>
    [Fact]
    public void The_strongest_durability_is_accepted_on_every_platform()
    {
        _tree.Directory.WriteAllBytesAtomic("committed", [7], Durability.FileAndDirectory);

        Assert.Equal([7], HostFile.ReadAllBytes(Path.Combine(_tree.HostPath, "committed")));
    }

    /// <summary>Publishing over a file replaces it, and leaves nothing else behind.</summary>
    [Fact]
    public void Publishing_over_a_file_replaces_it_and_leaves_no_scratch_behind()
    {
        HostFile.WriteAllText(Path.Combine(_tree.HostPath, "report"), "old");

        _tree.Directory.WriteAllTextAtomic("report", "new");

        Assert.Equal("new", HostFile.ReadAllText(Path.Combine(_tree.HostPath, "report")));
        Assert.Equal(["report"], Names());
    }

    /// <summary>
    /// The name resolves to the old contents right up until it resolves to the new ones.
    /// </summary>
    /// <remarks>
    /// The whole point of the operation, and the one thing an ordinary write cannot promise.
    /// A reader is opened repeatedly while a publish runs, and every read it gets must be one
    /// of the two whole answers — never a prefix of the new contents, and never a failure
    /// because the name momentarily held nothing.
    /// </remarks>
    [Fact]
    public async Task A_reader_never_sees_a_partly_written_file()
    {
        string oldText = new('a', 1 << 16);
        string newText = new('b', 1 << 20);
        HostFile.WriteAllText(Path.Combine(_tree.HostPath, "report"), oldText);

        using CancellationTokenSource stop = new();
        Task<List<string>> reader = Observing(() => ReadUntilStopped(stop.Token));

        for (int i = 0; i < 20; i++)
        {
            _tree.Directory.WriteAllTextAtomic("report", newText, Durability.None);
            _tree.Directory.WriteAllTextAtomic("report", oldText, Durability.None);
        }

        await stop.CancelAsync();
        List<string> seen = await reader;

        Assert.NotEmpty(seen);
        Assert.All(seen, text => Assert.True(
            text == oldText || text == newText,
            $"A reader saw {text.Length} bytes, which is neither of the two whole answers."));
    }

    /// <summary>A failed publish leaves the old file alone and removes its scratch name.</summary>
    /// <remarks>
    /// The failure is arranged by asking to publish onto a name that is a directory, which the
    /// move at the end cannot satisfy. Everything before it succeeds, so this exercises the
    /// path where a scratch file exists and the operation then gives up — the path that leaves
    /// litter if the cleanup is missing.
    /// </remarks>
    [Fact]
    public void A_failed_publish_leaves_nothing_behind()
    {
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "occupied"));

        Assert.ThrowsAny<IOException>(
            () => _tree.Directory.WriteAllTextAtomic("occupied", "contents"));

        Assert.Equal(["occupied"], Names());
        Assert.True(HostDirectory.Exists(Path.Combine(_tree.HostPath, "occupied")));
    }

    /// <summary>The scratch file is made in the directory the published file lands in.</summary>
    /// <remarks>
    /// <para>
    /// Not a detail. A scratch file written anywhere else cannot be moved onto the target when
    /// the two turn out to be on different filesystems, and the usual repair for that is to
    /// fall back to copying — which is not atomic, which is exactly what this operation exists
    /// to provide.
    /// </para>
    /// <para>
    /// Observed by publishing into a subdirectory and watching that subdirectory rather than
    /// the root, so a scratch file placed at the handle's own level would fail the test.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_scratch_file_is_made_beside_the_published_file()
    {
        string nested = Path.Combine(_tree.HostPath, "nested");
        HostDirectory.CreateDirectory(nested);

        using CancellationTokenSource stop = new();
        Task<bool> watcher = Observing(() => SawScratchIn(nested, stop.Token));

        for (int i = 0; i < 40; i++)
        {
            _tree.Directory.WriteAllTextAtomic(
                Path.Combine("nested", "report"), new string('c', 1 << 20), Durability.None);
        }

        await stop.CancelAsync();
        Assert.True(await watcher, "No scratch file was ever seen in the directory being published into.");
    }

    /// <summary>A path spelled so that it must be a directory is refused.</summary>
    /// <remarks>
    /// The distinction is carried by the parser and lost the moment a path is split into
    /// components, so an operation that acts on a file has to apply it before splitting or not
    /// at all. Publishing onto such a name would otherwise create a file under a name the
    /// caller had said was a directory.
    /// </remarks>
    [Fact]
    public void A_name_spelled_as_a_directory_is_refused()
    {
        Assert.Throws<ArgumentException>(
            () => _tree.Directory.WriteAllTextAtomic("report/", "contents"));
    }

    /// <summary>Text is stored as UTF-8 with no byte-order mark.</summary>
    [Fact]
    public void Text_is_stored_as_plain_utf8()
    {
        _tree.Directory.WriteAllTextAtomic("report", "naïve");

        byte[] stored = HostFile.ReadAllBytes(Path.Combine(_tree.HostPath, "report"));
        Assert.Equal(Encoding.UTF8.GetBytes("naïve"), stored);
    }

    /// <summary>The asynchronous form publishes the same way.</summary>
    [Fact]
    public async Task The_asynchronous_form_publishes_the_same_way()
    {
        HostFile.WriteAllText(Path.Combine(_tree.HostPath, "report"), "old");

        await _tree.Directory.WriteAllTextAtomicAsync(
            "report", "new", Durability.FileAndDirectory, TestContext.Current.CancellationToken);

        Assert.Equal("new", HostFile.ReadAllText(Path.Combine(_tree.HostPath, "report")));
        Assert.Equal(["report"], Names());
    }

    /// <summary>
    /// A cancelled asynchronous publish leaves the name as it was.
    /// </summary>
    /// <remarks>
    /// The difference from cancelling an ordinary write, which has already emptied the file it
    /// was writing to by the time it notices. Here the contents were going somewhere else, so
    /// there is nothing to half-destroy.
    /// </remarks>
    [Fact]
    public async Task A_cancelled_publish_leaves_the_name_as_it_was()
    {
        HostFile.WriteAllText(Path.Combine(_tree.HostPath, "report"), "old");

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _tree.Directory.WriteAllTextAtomicAsync(
                "report", new string('d', 1 << 20), Durability.None, cancelled.Token));

        Assert.Equal("old", HostFile.ReadAllText(Path.Combine(_tree.HostPath, "report")));
        Assert.Equal(["report"], Names());
    }

    /// <summary>
    /// A link at the name, to another file in the tree, is replaced by the published file and
    /// the file it pointed at is left alone.
    /// </summary>
    /// <remarks>
    /// The case that matters: a publish that opened the name for writing would follow the link
    /// and rewrite the other file, so whoever placed the link would choose which file in the
    /// tree the publish overwrites. The move acts on the name, so the link is what goes.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_link_to_a_file_in_the_tree_is_replaced_and_its_target_left_alone(bool asynchronous)
    {
        HostFile.WriteAllText(Path.Combine(_tree.HostPath, "keep"), "untouched");
        HostFile.CreateSymbolicLink(Path.Combine(_tree.HostPath, "report"), "keep");

        await Publish(_tree.Directory, "report", "new", asynchronous);

        string published = Path.Combine(_tree.HostPath, "report");
        Assert.Null(HostEntry.LinkTarget(published));
        Assert.Equal("new", HostFile.ReadAllText(published));
        Assert.Equal("untouched", HostFile.ReadAllText(Path.Combine(_tree.HostPath, "keep")));
        Assert.Equal(["keep", "report"], Names());
    }

    /// <summary>
    /// A link at the name is replaced the same way wherever it points, including outside the
    /// handle's subtree and at nothing.
    /// </summary>
    /// <remarks>
    /// Published through a handle on a subdirectory, so that the link's target is genuinely
    /// outside what that handle grants. Following it would be refused as an escape; replacing
    /// it is not an escape, because the name being replaced is inside.
    /// </remarks>
    [Theory]
    [InlineData("outside")]
    [InlineData("dangling")]
    public void A_link_at_the_name_is_replaced_wherever_it_points(string kind)
    {
        string inside = Path.Combine(_tree.HostPath, "inside");
        HostDirectory.CreateDirectory(inside);
        HostFile.WriteAllText(Path.Combine(_tree.HostPath, "secret"), "untouched");
        string target = kind == "outside" ? Path.Combine("..", "secret") : "missing";
        HostFile.CreateSymbolicLink(Path.Combine(inside, "report"), target);

        using (Dir handle = _tree.Directory.OpenDir("inside"))
        {
            handle.WriteAllTextAtomic("report", "new");
        }

        string published = Path.Combine(inside, "report");
        Assert.Null(HostEntry.LinkTarget(published));
        Assert.Equal("new", HostFile.ReadAllText(published));
        Assert.Equal("untouched", HostFile.ReadAllText(Path.Combine(_tree.HostPath, "secret")));
        Assert.Equal(["report"], HostDirectory.GetFileSystemEntries(inside).Select(Path.GetFileName));
    }

    /// <summary>
    /// A link to a directory at the name is replaced like any other link, and the directory it
    /// pointed at is left as it was.
    /// </summary>
    /// <remarks>
    /// A link is not a directory, whatever it points at, so moving a file onto its name is an
    /// ordinary replacement of one name by another. On Windows such a link is a directory
    /// entry, which a rename that did not treat it as a name would refuse to move a file over;
    /// the replacing rename used there does treat it as one.
    /// </remarks>
    [Fact]
    public void A_directory_link_at_the_name_is_replaced() =>
        AssertADirectoryLinkIsReplaced(link => HostDirectory.CreateSymbolicLink(link, "elsewhere"));

    /// <summary>On Windows, the same for a junction.</summary>
    [Fact]
    [NotInMemory("A junction is a Windows reparse point that only the host's filesystem holds.")]
    public void On_windows_a_junction_at_the_name_is_replaced()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Junctions exist only on Windows.");
        }

        AssertADirectoryLinkIsReplaced(link => CreateJunction(link, Path.Combine(_tree.HostPath, "elsewhere")));
    }

    private void AssertADirectoryLinkIsReplaced(Action<string> makeLink)
    {
        string elsewhere = Path.Combine(_tree.HostPath, "elsewhere");
        string published = Path.Combine(_tree.HostPath, "report");
        HostDirectory.CreateDirectory(elsewhere);
        HostFile.WriteAllText(Path.Combine(elsewhere, "inner"), "untouched");
        makeLink(published);

        _tree.Directory.WriteAllTextAtomic("report", "new");

        Assert.Null(HostEntry.LinkTarget(published));
        Assert.Equal("new", HostFile.ReadAllText(published));
        Assert.Equal(["inner"], HostDirectory.GetFileSystemEntries(elsewhere).Select(Path.GetFileName));
        Assert.Equal("untouched", HostFile.ReadAllText(Path.Combine(elsewhere, "inner")));
        Assert.Equal(["elsewhere", "report"], Names());
    }

    /// <summary>Publishes text through either form of the operation.</summary>
    private static Task Publish(Dir directory, string path, string contents, bool asynchronous)
    {
        if (asynchronous)
        {
            return directory.WriteAllTextAtomicAsync(
                path, contents, Durability.FileAndDirectory, TestContext.Current.CancellationToken);
        }

        directory.WriteAllTextAtomic(path, contents);
        return Task.CompletedTask;
    }

    /// <summary>Creates a junction, which unlike a symbolic link needs no privilege.</summary>
    /// <remarks>
    /// Through the shell because the framework has no API for one. The paths are quoted rather
    /// than passed as separate arguments: the shell re-parses its own command line, and a
    /// temporary directory can contain a space.
    /// </remarks>
    private static void CreateJunction(string link, string target)
    {
        using Process? process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        Assert.NotNull(process);

        // Both streams drained before waiting: a process whose output fills the pipe while
        // nobody is reading it never exits.
        string output = process.StandardOutput.ReadToEnd();
        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(HostDirectory.Exists(link), $"Could not create a junction at '{link}': {errors}{output}");
    }

    /// <summary>Everything currently in the scratch tree, by name.</summary>
    private string[] Names() =>
        [.. HostDirectory.GetFileSystemEntries(_tree.HostPath).Select(Path.GetFileName).Order()!];

    /// <summary>Reads the published name over and over until it is told to stop.</summary>
    /// <remarks>
    /// A read that fails because the name is being replaced is recorded as the empty string,
    /// which is neither whole answer and so fails the assertion. That is the point: a publish
    /// that removed the name before creating it again would show up here.
    /// </remarks>
    private List<string> ReadUntilStopped(CancellationToken stopping)
    {
        List<string> seen = [];
        string path = Path.Combine(_tree.HostPath, "report");

        while (!stopping.IsCancellationRequested)
        {
            try
            {
                seen.Add(HostFile.ReadAllText(path));
            }
            catch (IOException)
            {
                // Windows refuses a read of a file another handle has open without sharing,
                // which is a fact about that platform's sharing rules rather than about what
                // the name resolved to. A name that held nothing would be reported as a
                // missing file instead, and that is not caught.
            }
        }

        return seen;
    }

    /// <summary>
    /// Starts an observer, and returns once it is running rather than once it is queued.
    /// </summary>
    /// <remarks>
    /// What these tests are about is only there to be seen while a publish is in flight, so an
    /// observer that has not started looking by the time the publishing loop ends sees nothing
    /// — and an observer that saw nothing is indistinguishable from one that never looked, so
    /// the test fails for a reason that has nothing to do with the code under test. Handing the
    /// work to the thread pool invites exactly that: the rest of the suite is running at the
    /// same time, and a pool with nothing free will start it whenever it can. A thread of its
    /// own, waited for, is the observer the assertions assume.
    /// </remarks>
    private static Task<T> Observing<T>(Func<T> observe)
    {
        TaskCompletionSource<T> observed = new();
        TaskCompletionSource running = new();

        Thread thread = new(() =>
        {
            running.SetResult();

            try
            {
                observed.SetResult(observe());
            }
            catch (Exception exception)
            {
                observed.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "atomic write observer",
        };

        thread.Start();
        running.Task.GetAwaiter().GetResult();

        return observed.Task;
    }

    /// <summary>Watches a directory for a scratch file appearing in it.</summary>
    private static bool SawScratchIn(string directory, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            foreach (string entry in HostDirectory.GetFileSystemEntries(directory))
            {
                if (Path.GetFileName(entry).StartsWith("cap-", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
