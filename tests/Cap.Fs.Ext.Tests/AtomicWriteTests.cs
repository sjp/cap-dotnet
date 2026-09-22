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

        Assert.Equal(contents, File.ReadAllBytes(Path.Combine(_tree.HostPath, "report")));
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

        Assert.Equal([7], File.ReadAllBytes(Path.Combine(_tree.HostPath, "committed")));
    }

    /// <summary>Publishing over a file replaces it, and leaves nothing else behind.</summary>
    [Fact]
    public void Publishing_over_a_file_replaces_it_and_leaves_no_scratch_behind()
    {
        File.WriteAllText(Path.Combine(_tree.HostPath, "report"), "old");

        _tree.Directory.WriteAllTextAtomic("report", "new");

        Assert.Equal("new", File.ReadAllText(Path.Combine(_tree.HostPath, "report")));
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
        File.WriteAllText(Path.Combine(_tree.HostPath, "report"), oldText);

        using CancellationTokenSource stop = new();
        Task<List<string>> reader = Task.Run(() => ReadUntilStopped(stop.Token));

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
        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "occupied"));

        Assert.ThrowsAny<IOException>(
            () => _tree.Directory.WriteAllTextAtomic("occupied", "contents"));

        Assert.Equal(["occupied"], Names());
        Assert.True(Directory.Exists(Path.Combine(_tree.HostPath, "occupied")));
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
        Directory.CreateDirectory(nested);

        using CancellationTokenSource stop = new();
        Task<bool> watcher = Task.Run(() => SawScratchIn(nested, stop.Token));

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

        byte[] stored = File.ReadAllBytes(Path.Combine(_tree.HostPath, "report"));
        Assert.Equal(Encoding.UTF8.GetBytes("naïve"), stored);
    }

    /// <summary>The asynchronous form publishes the same way.</summary>
    [Fact]
    public async Task The_asynchronous_form_publishes_the_same_way()
    {
        File.WriteAllText(Path.Combine(_tree.HostPath, "report"), "old");

        await _tree.Directory.WriteAllTextAtomicAsync(
            "report", "new", Durability.FileAndDirectory, TestContext.Current.CancellationToken);

        Assert.Equal("new", File.ReadAllText(Path.Combine(_tree.HostPath, "report")));
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
        File.WriteAllText(Path.Combine(_tree.HostPath, "report"), "old");

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _tree.Directory.WriteAllTextAtomicAsync(
                "report", new string('d', 1 << 20), Durability.None, cancelled.Token));

        Assert.Equal("old", File.ReadAllText(Path.Combine(_tree.HostPath, "report")));
        Assert.Equal(["report"], Names());
    }

    /// <summary>Everything currently in the scratch tree, by name.</summary>
    private string[] Names() =>
        [.. Directory.GetFileSystemEntries(_tree.HostPath).Select(Path.GetFileName).Order()!];

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
                seen.Add(File.ReadAllText(path));
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

    /// <summary>Watches a directory for a scratch file appearing in it.</summary>
    private static bool SawScratchIn(string directory, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            foreach (string entry in Directory.EnumerateFiles(directory))
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
