using Cap.Std;

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

    /// <summary>Spelling matters unless the pattern says otherwise.</summary>
    [Fact]
    public void Case_is_compared_exactly_unless_the_pattern_says_otherwise()
    {
        Make("Report.TXT");

        Assert.DoesNotContain("Report.TXT", Matches("*.txt"));
        Assert.Contains("Report.TXT", Names(GlobPattern.Parse("*.txt", ignoreCase: true)));
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
