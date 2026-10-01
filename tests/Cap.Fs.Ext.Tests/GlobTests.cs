using System.Diagnostics;
using Cap.Primitives;
using Cap.Std;
using Cap.Std.Testing;

namespace Cap.Fs.Ext.Tests;

/// <summary>
/// Finding the names in a tree that a pattern describes.
/// </summary>
/// <remarks>
/// <para>
/// The matching is tested through a real tree rather than against the matcher directly,
/// because the interesting part is not whether <c>*</c> matches a run of characters — it is
/// that the pattern is matched one name at a time as a walk descends, and that the walk
/// refuses the same things it refuses when nothing is driving it.
/// </para>
/// <para>
/// A search is also expected to avoid entering directories that could not lead anywhere, which
/// is the whole reason to match during the walk rather than after it. That is asserted by
/// putting something unreadable where a pruned search must not go.
/// </para>
/// </remarks>
public sealed class GlobTests : IDisposable
{
    private readonly ScratchTree _tree = new();

    public GlobTests()
    {
        Make("top.txt");
        Make("notes.md");
        Make("a", "one.txt");
        Make("a", "two.md");
        Make("a", "b", "three.txt");
        Make(".hidden", "four.txt");
    }

    public void Dispose() => _tree.Dispose();

    /// <summary>A pattern with no crossing piece matches at one level only.</summary>
    [Fact]
    public void A_pattern_matches_at_the_level_it_names()
    {
        Assert.Equal([".hidden", "a", "notes.md", "top.txt"], Matches("*").Order());
        Assert.Equal(["b", "one.txt", "two.md"], Matches(Path.Combine("a", "*")).Order());
    }

    /// <summary>A run matches within a name and never across levels.</summary>
    [Fact]
    public void A_run_does_not_cross_a_level()
    {
        Assert.Equal(["top.txt"], Matches("*.txt"));
        Assert.Equal(["three.txt"], Matches(Path.Combine("a", "b", "*.txt")));
    }

    /// <summary>A crossing piece matches any number of levels, including none.</summary>
    [Fact]
    public void A_crossing_piece_matches_any_number_of_levels()
    {
        Assert.Equal(
            ["four.txt", "one.txt", "three.txt", "top.txt"],
            Matches(Path.Combine("**", "*.txt")).Order());
    }

    /// <summary>A crossing piece at the end matches everything beneath.</summary>
    [Fact]
    public void A_crossing_piece_at_the_end_matches_everything_beneath()
    {
        Assert.Equal(["b", "one.txt", "three.txt", "two.md"], Matches(Path.Combine("a", "**")).Order());
    }

    /// <summary>
    /// An empty directory at the limit is matched and does not fail the search, since nothing
    /// in the tree lies deeper than the limit.
    /// </summary>
    [Fact]
    public void An_empty_directory_at_the_limit_is_matched_and_does_not_fail_the_search()
    {
        Make("limit", "f.txt");
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "limit", "e"));

        WalkOptions options = new() { MaxDepth = 2 };

        Assert.Equal(["e", "f.txt"], Names(GlobPattern.Parse(Path.Combine("limit", "**")), options).Order());
    }

    /// <summary>A directory at the limit with anything in it fails the search.</summary>
    [Fact]
    public void A_non_empty_directory_at_the_limit_fails_the_search()
    {
        WalkOptions options = new() { MaxDepth = 2 };

        CapIOException thrown = Assert.Throws<CapIOException>(
            () => Names(GlobPattern.Parse(Path.Combine("a", "**")), options));
        Assert.Equal(CapErrorKind.PathTooDeep, thrown.Kind);
    }

    /// <summary>A tree exactly as deep as the limit is searched to its end.</summary>
    [Fact]
    public void A_tree_exactly_as_deep_as_the_limit_is_searched_to_its_end()
    {
        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "chain", "1", "2", "3"));

        WalkOptions options = new() { MaxDepth = 4 };

        Assert.Equal(["1", "2", "3"], Names(GlobPattern.Parse(Path.Combine("chain", "**")), options));
    }

    /// <summary>One character, and a set of characters, match one character.</summary>
    [Fact]
    public void Single_characters_and_sets_match_one_character()
    {
        Make("a1.log");
        Make("a2.log");
        Make("ab.log");

        Assert.Equal(["a1.log", "a2.log", "ab.log"], Matches("a?.log").Order());
        Assert.Equal(["a1.log", "a2.log"], Matches("a[12].log").Order());
        Assert.Equal(["a1.log", "a2.log"], Matches("a[0-9].log").Order());
        Assert.Equal(["ab.log"], Matches("a[!0-9].log"));
    }

    /// <summary>
    /// A <c>]</c> first in a class, after any negation, is a member rather than the end, and a
    /// class that never ends is the literal characters it is made of.
    /// </summary>
    /// <remarks>
    /// With no escape character, a class is the only way to match <c>]</c> at all. Reading the
    /// first <c>]</c> as the end instead made <c>[]]x</c> match nothing and <c>[!]x</c> — a
    /// class excluding nothing — match every character, the opposite of what was written.
    /// </remarks>
    [Fact]
    public void A_leading_close_bracket_is_a_literal_member()
    {
        foreach (string name in (string[])["]x", "ax", "-x", "[x", "]b", "ab", "[!]"])
        {
            Make("brackets", name);
        }

        Assert.Equal(["]x"], InBrackets("[]]x"));
        Assert.Equal(["-x", "[x", "ax"], InBrackets("[!]]x"));
        Assert.Equal(["-x", "[x", "ax"], InBrackets("[^]]x"));
        Assert.Equal(["]b", "ab"], InBrackets("[]a]b"));
        Assert.Equal(["-x", "ax"], InBrackets("[a-]x"));
        Assert.Equal(["[!]"], InBrackets("[!]"));
        Assert.Equal(["[x"], InBrackets("[x"));
        Assert.Equal(["[x"], InBrackets("[[]x"));

        string[] InBrackets(string pattern) =>
            [.. Matches(Path.Combine("brackets", pattern)).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// A piece full of runs is matched against a long name in time proportional to the two,
    /// not exponential in the runs.
    /// </summary>
    /// <remarks>
    /// Both the pattern and the names come from outside, so a matcher that explored every way
    /// of dividing a name among twenty runs would be a way of stopping the process. The bound
    /// is generous: the matcher takes microseconds, and the exponential one would not finish.
    /// </remarks>
    [Fact]
    public void A_pattern_full_of_runs_matches_a_long_name_quickly()
    {
        string name = new('a', 200);
        Make("long", name);
        string missing = string.Concat(Enumerable.Repeat("*a", 20)) + "*b";
        string present = string.Concat(Enumerable.Repeat("*a", 20)) + "*";

        Stopwatch elapsed = Stopwatch.StartNew();
        string[] none = Matches(Path.Combine("long", missing));
        string[] one = Matches(Path.Combine("long", present));
        elapsed.Stop();

        Assert.Empty(none);
        Assert.Equal([name], one);
        Assert.True(elapsed.ElapsedMilliseconds < 2_000, $"Matching took {elapsed.ElapsedMilliseconds} ms.");
    }

    /// <summary>Spelling matters unless the pattern says otherwise.</summary>
    [Fact]
    public void Case_is_compared_exactly_unless_the_pattern_says_otherwise()
    {
        Make("Report.TXT");

        Assert.DoesNotContain("Report.TXT", Matches("*.txt"));
        Assert.Contains("Report.TXT", Names(GlobPattern.Parse("*.txt", ignoreCase: true)));
        Assert.Contains("Report.TXT", _tree.Directory.Glob("*.txt", ignoreCase: true).Select(e => e.Name));
        Assert.DoesNotContain("Report.TXT", _tree.Directory.Glob("*.txt", ignoreCase: false).Select(e => e.Name));
    }

    /// <summary>
    /// A search asked to ignore case divides its pattern as the handle reads a path, which a
    /// pattern parsed without a handle cannot.
    /// </summary>
    /// <remarks>
    /// Under Windows rules a backslash divides levels. On a machine where it does not, a
    /// pattern parsed by <see cref="GlobPattern.Parse(string, bool)"/> keeps it inside one
    /// piece and matches nothing, which is why the handle-taking form exists.
    /// </remarks>
    [Fact]
    public async Task A_search_that_ignores_case_divides_the_pattern_as_the_handle_does()
    {
        InMemoryFileSystem fs = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Windows });
        fs.AddFile("a/One.TXT", "one");
        fs.AddFile("a/two.md", "two");
        using Dir root = fs.OpenRoot();

        Assert.Equal(["One.TXT"], root.Glob(@"a\*.txt", ignoreCase: true).Select(e => e.Name));
        Assert.Empty(root.Glob(@"a\*.txt", ignoreCase: false));

        List<string> found = [];
        await foreach (WalkEntry entry in root.GlobAsync(
            @"a\*.txt", ignoreCase: true, cancellationToken: TestContext.Current.CancellationToken))
        {
            found.Add(entry.Name);
        }

        Assert.Equal(["One.TXT"], found);
    }

    /// <summary>A name beginning with a dot is matched like any other.</summary>
    /// <remarks>
    /// Different from a shell, and deliberately: the names come from a directory read rather
    /// than from a command line, and a second rule about which of them a pattern can see would
    /// be one more thing to know. A caller who wants them left out says so in the options,
    /// where it also keeps the search out of hidden directories.
    /// </remarks>
    [Fact]
    public void A_dotted_name_is_matched_like_any_other()
    {
        Assert.Contains(".hidden", Matches("*"));
        Assert.Contains("four.txt", Matches(Path.Combine("**", "*.txt")));

        WalkOptions skipping = new() { SkipHidden = true };
        Assert.DoesNotContain("four.txt", Names(GlobPattern.Parse(Path.Combine("**", "*.txt")), skipping));
    }

    /// <summary>A directory no piece of the pattern could match through is never read.</summary>
    /// <remarks>
    /// Observed by making a directory unreadable: a search that entered it would fail rather
    /// than return, so the search returning is the evidence that it stayed out.
    /// </remarks>
    [Fact]
    public void A_directory_that_cannot_lead_to_a_match_is_not_entered()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Making a directory unreadable here needs a security descriptor this test does not build.");
            return;
        }

        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "sealed"));
        HostFile.SetUnixFileMode(Path.Combine(_tree.HostPath, "sealed"), UnixFileMode.None);

        try
        {
            Assert.Equal(["b", "one.txt", "two.md"], Matches(Path.Combine("a", "*")).Order());
        }
        finally
        {
            HostFile.SetUnixFileMode(
                Path.Combine(_tree.HostPath, "sealed"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// An unreadable directory the pattern has to search through fails the search, while one
    /// the pattern steers away from still cannot.
    /// </summary>
    [Fact]
    public void An_unreadable_directory_on_the_pattern_path_fails_the_search()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Making a directory unreadable here needs a security descriptor this test does not build.");
            return;
        }

        if (HostTree.InMemory || Environment.IsPrivilegedProcess)
        {
            Assert.Skip("Mode bits do not stop this process reading a directory here.");
            return;
        }

        HostDirectory.CreateDirectory(Path.Combine(_tree.HostPath, "sealed"));
        HostFile.SetUnixFileMode(Path.Combine(_tree.HostPath, "sealed"), UnixFileMode.None);

        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Matches(Path.Combine("**", "*.txt")));
            Assert.Equal(["b", "one.txt", "two.md"], Matches(Path.Combine("a", "*")).Order());
        }
        finally
        {
            HostFile.SetUnixFileMode(
                Path.Combine(_tree.HostPath, "sealed"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>A match carries the handle it was found through, and no path.</summary>
    [Fact]
    public void A_match_can_be_opened_through_the_handle_it_came_from()
    {
        foreach (WalkEntry entry in _tree.Directory.Glob(Path.Combine("a", "b", "*.txt")))
        {
            Assert.Equal("three.txt", entry.Name);
            Assert.Equal(3, entry.Depth);
            using ICapFile file = entry.OpenFile();
            Assert.Equal(8, file.Length);
        }
    }

    /// <summary>A pattern that starts at a root, or climbs, is refused.</summary>
    [Fact]
    public void A_pattern_that_leaves_the_handle_is_refused()
    {
        Assert.Throws<ArgumentException>(() => GlobPattern.Parse(Path.Combine("..", "*")));
        Assert.Throws<ArgumentException>(() => GlobPattern.Parse(Path.DirectorySeparatorChar + "etc"));
        Assert.Throws<ArgumentException>(() => GlobPattern.Parse("."));
    }

    /// <summary>
    /// Every rooted form Windows rules have is refused when a pattern is read, not only a
    /// leading separator.
    /// </summary>
    /// <remarks>
    /// A drive, a drive-relative prefix, a share and the device namespace used to be split into
    /// pieces such as <c>C:</c> that matched nothing and said nothing about why. Under Unix
    /// rules none of those is rooted, and <c>C:etc</c> is an ordinary name.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\etc")]
    [InlineData(@"C:etc")]
    [InlineData(@"C:/etc")]
    [InlineData(@"\etc")]
    [InlineData(@"\\server\share\*")]
    [InlineData(@"\\?\C:\x")]
    [InlineData(@"\\.\pipe\x")]
    public void A_rooted_pattern_under_windows_rules_is_refused(string pattern)
    {
        InMemoryFileSystem windows = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Windows });
        using Dir root = windows.OpenRoot();

        Assert.Throws<ArgumentException>(() => root.Glob(pattern));
        Assert.Throws<ArgumentException>(() => root.Glob(pattern, ignoreCase: true));
        Assert.Throws<ArgumentException>(() => root.GlobAsync(pattern, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>What is rooted under Windows rules is an ordinary name under Unix rules.</summary>
    [Fact]
    public void A_drive_shaped_name_under_unix_rules_is_an_ordinary_name()
    {
        InMemoryFileSystem unix = new(new InMemoryFileSystemOptions { PathSyntax = CapPathSyntax.Unix });
        unix.AddFile("C:etc", "x");
        using Dir root = unix.OpenRoot();

        Assert.Equal(["C:etc"], root.Glob("C:etc").Select(e => e.Name));
        Assert.Throws<ArgumentException>(() => root.Glob("/etc"));
    }

    /// <summary>A pattern parsed once can be used more than once.</summary>
    [Fact]
    public void A_parsed_pattern_can_be_reused()
    {
        GlobPattern pattern = GlobPattern.Parse("*.txt");

        Assert.Equal(Names(pattern), Names(pattern));
        Assert.Equal("*.txt", pattern.ToString());
    }

    /// <summary>The asynchronous search finds exactly what the synchronous one finds.</summary>
    [Theory]
    [InlineData("*")]
    [InlineData("*.txt")]
    [InlineData("a/*")]
    [InlineData("**/*.txt")]
    [InlineData("a/**")]
    public async Task The_asynchronous_search_finds_the_same_names(string pattern)
    {
        string native = pattern.Replace('/', Path.DirectorySeparatorChar);
        List<string> found = [];
        List<int> depths = [];

        await foreach (WalkEntry entry in _tree.Directory.GlobAsync(
            native, cancellationToken: TestContext.Current.CancellationToken))
        {
            found.Add(entry.Name);
            depths.Add(entry.Depth);
        }

        Assert.Equal(Matches(native).Order(), found.Order());
        Assert.Equal(
            _tree.Directory.Glob(native).Select(e => e.Depth).Order(),
            depths.Order());
    }

    /// <summary>A pattern parsed once drives the asynchronous search as it drives the synchronous one.</summary>
    [Fact]
    public async Task A_parsed_pattern_drives_the_asynchronous_search()
    {
        GlobPattern pattern = GlobPattern.Parse(Path.Combine("**", "*.md"));
        List<string> found = [];

        await foreach (WalkEntry entry in _tree.Directory.GlobAsync(
            pattern, cancellationToken: TestContext.Current.CancellationToken))
        {
            found.Add(entry.Name);
        }

        Assert.Equal(Names(pattern).Order(), found.Order());
    }

    /// <summary>An asynchronous search asked to stop throws rather than finishing.</summary>
    [Fact]
    public async Task A_cancelled_asynchronous_search_stops()
    {
        CancellationToken cancelled = new(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (WalkEntry entry in _tree.Directory.GlobAsync(
                Path.Combine("**", "*.txt"), cancellationToken: cancelled))
            {
                _ = entry.Name;
            }
        });
    }

    /// <summary>A pattern that cannot be matched is refused when the search is asked for, not when it is read.</summary>
    [Fact]
    public void An_unusable_pattern_is_refused_by_the_asynchronous_form_at_the_call()
    {
        Assert.Throws<ArgumentException>(() => _tree.Directory.GlobAsync(Path.Combine("..", "*"), cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>The names a pattern matches in the scratch tree.</summary>
    /// <summary>
    /// A pattern with more pieces than a live set can keep as bits matches what the same
    /// pattern with fewer pieces matches.
    /// </summary>
    /// <remarks>
    /// Up to 64 pieces a live set is kept as one bit per piece, and beyond that it is built as
    /// a list instead, so the boundary is tested on both sides. A run of crossing pieces
    /// means what one crossing piece means.
    /// </remarks>
    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(100)]
    public void A_pattern_with_many_pieces_matches_as_a_short_one_does(int crossings)
    {
        string pattern = string.Concat(Enumerable.Repeat("**" + Path.DirectorySeparatorChar, crossings)) + "*.txt";

        Assert.Equal(Matches(Path.Combine("**", "*.txt")).Order(), Matches(pattern).Order());
    }

    /// <summary>One parsed pattern drives many searches on many threads at once.</summary>
    /// <remarks>
    /// A pattern keeps the live sets its searches find, so that each is one shared array; the
    /// searches are run together so that two of them can find a new set at the same moment.
    /// </remarks>
    [Fact]
    public void A_parsed_pattern_drives_concurrent_searches()
    {
        string[] expected = [.. Matches(Path.Combine("**", "b", "*.txt")).Order()];
        Assert.Equal(["three.txt"], expected);

        GlobPattern pattern = GlobPattern.Parse(Path.Combine("**", "b", "*.txt"));
        string[][] found = new string[16][];

        Parallel.For(0, found.Length, i => found[i] = [.. Names(pattern).Order()]);

        Assert.All(found, names => Assert.Equal(expected, names));
    }

    /// <summary>
    /// A search under a crossing piece costs nothing per name beyond what walking the same
    /// tree costs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The walk itself allocates per name — the name is a string the directory read
    /// produced — so the search is measured against a walk of the same tree rather than
    /// against zero. Matching a name against the live pieces, and working out the pieces live
    /// beneath it, must add nothing that grows with the tree: the set inside a directory is
    /// almost always the set it was read with, and any other is one the pattern has kept.
    /// </para>
    /// <para>
    /// The search yields fewer entries than the walk, so it is held to at most what the walk
    /// cost plus a little for the pattern's own one-time work.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_search_under_a_crossing_piece_allocates_nothing_per_name()
    {
        for (int d = 0; d < 10; d++)
        {
            for (int f = 0; f < 100; f++)
            {
                Make("many", $"d{d}", $"f{f}{(f % 2 == 0 ? ".txt" : ".md")}");
            }
        }

        using Dir many = _tree.Directory.OpenDir("many");
        GlobPattern pattern = GlobPattern.Parse(Path.Combine("**", "*.txt"));

        // Warm both paths, so that what is measured is the search and the walk rather than
        // the once-per-process work of getting to them.
        Assert.Equal(500, Drain(many.Glob(pattern)));
        Assert.Equal(1010, Drain(many.Walk()));

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = Drain(many.Walk());
        long walked = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        _ = Drain(many.Glob(pattern));
        long searched = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            searched <= walked + 4096,
            $"Searching 1010 names for '**/*.txt' allocated {searched} bytes, and walking the " +
            $"same tree {walked}. The search is supposed to add nothing per name.");
    }

    /// <summary>
    /// A search honours the ordering options as a walk does: sorted siblings, a directory
    /// after its contents, and shallow matches left out while the search still goes beneath
    /// them.
    /// </summary>
    [Fact]
    public void A_search_orders_and_limits_its_matches_as_a_walk_does()
    {
        GlobPattern beneathA = GlobPattern.Parse(Path.Combine("a", "**"));

        Assert.Equal(
            ["b", "three.txt", "one.txt", "two.md"],
            Names(beneathA, new WalkOptions { Sort = string.CompareOrdinal }));
        Assert.Equal(
            ["three.txt", "b", "one.txt", "two.md"],
            Names(beneathA, new WalkOptions { Sort = string.CompareOrdinal, ContentsFirst = true }));
        Assert.Equal(
            ["three.txt"],
            Names(beneathA, new WalkOptions { MinDepth = 3 }));
    }

    /// <summary>Runs a search or a walk to its end, answering how many entries it yielded.</summary>
    private static int Drain(IEnumerable<WalkEntry> entries)
    {
        int count = 0;
        foreach (WalkEntry entry in entries)
        {
            count++;
        }

        return count;
    }

    private string[] Matches(string pattern) => Names(GlobPattern.Parse(pattern));

    /// <summary>The names a parsed pattern matches in the scratch tree.</summary>
    private string[] Names(GlobPattern pattern, WalkOptions? options = null) =>
        [.. _tree.Directory.Glob(pattern, options).Select(e => e.Name)];

    /// <summary>Creates a file, and whatever directories it needs, under the scratch tree.</summary>
    private void Make(params string[] parts)
    {
        string path = Path.Combine([_tree.HostPath, .. parts]);
        HostDirectory.CreateDirectory(Path.GetDirectoryName(path)!);
        HostFile.WriteAllText(path, "contents");
    }
}
