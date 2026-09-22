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

        Directory.CreateDirectory(Path.Combine(_tree.HostPath, "sealed"));
        File.SetUnixFileMode(Path.Combine(_tree.HostPath, "sealed"), UnixFileMode.None);

        try
        {
            Assert.Equal(["b", "one.txt", "two.md"], Matches(Path.Combine("a", "*")).Order());
        }
        finally
        {
            File.SetUnixFileMode(
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
            using Cap.Std.CapFile file = entry.OpenFile();
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

    /// <summary>The names a pattern matches in the scratch tree.</summary>
    private string[] Matches(string pattern) => Names(GlobPattern.Parse(pattern));

    /// <summary>The names a parsed pattern matches in the scratch tree.</summary>
    private string[] Names(GlobPattern pattern, WalkOptions? options = null) =>
        [.. _tree.Directory.Glob(pattern, options).Select(e => e.Name)];

    /// <summary>Creates a file, and whatever directories it needs, under the scratch tree.</summary>
    private void Make(params string[] parts)
    {
        string path = Path.Combine([_tree.HostPath, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "contents");
    }
}
