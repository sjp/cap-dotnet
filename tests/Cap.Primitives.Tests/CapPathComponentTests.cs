namespace Cap.Primitives.Tests;

/// <summary>
/// What a parsed path says about itself: its components, and the three facts a resolver
/// acts on before it makes a single syscall.
/// </summary>
public sealed class CapPathComponentTests
{
    [Theory]
    [InlineData("foo.txt", CapPathSyntax.Unix, "foo.txt")]
    [InlineData("a/b/c", CapPathSyntax.Unix, "a|b|c")]
    [InlineData("a//b", CapPathSyntax.Unix, "a|b")]
    [InlineData("a/./b", CapPathSyntax.Unix, "a|b")]
    [InlineData("./a/", CapPathSyntax.Unix, "a")]
    [InlineData("a/b/", CapPathSyntax.Unix, "a|b")]
    [InlineData("a\\b", CapPathSyntax.Unix, "a\\b")]
    [InlineData("a\\b", CapPathSyntax.Windows, "a|b")]
    [InlineData("a/b\\c", CapPathSyntax.Windows, "a|b|c")]
    [InlineData("a\\\\b", CapPathSyntax.Windows, "a|b")]
    public void Enumerates_components(string raw, CapPathSyntax syntax, string expected)
    {
        CapPath path = ParseOrFail(raw, syntax);

        Assert.Equal(expected.Split('|'), Components(path));
        Assert.Equal(expected.Split('|').Length, path.ComponentCount);
    }

    /// <summary>
    /// A backslash is a separator on Windows and an ordinary filename character on Unix.
    /// Getting this backwards means either splitting a legitimate Unix filename in two, or
    /// handing Windows a string it will split for us after we have finished checking it.
    /// </summary>
    [Fact]
    public void Backslash_separates_only_under_windows_syntax()
    {
        Assert.Equal(["a\\b"], Components(ParseOrFail("a\\b", CapPathSyntax.Unix)));
        Assert.Equal(["a", "b"], Components(ParseOrFail("a\\b", CapPathSyntax.Windows)));
    }

    /// <summary>
    /// The fast path: one ordinary name, resolvable in a single lookup with no loop.
    /// </summary>
    [Theory]
    [InlineData("foo.txt", true)]
    [InlineData("a/b", false)]
    [InlineData("./foo.txt", true)]
    [InlineData("foo.txt/", false)]
    [InlineData("a/.", false)]
    public void IsSingleComponent_marks_exactly_the_one_lookup_case(string raw, bool expected)
    {
        Assert.Equal(expected, ParseOrFail(raw, CapPathSyntax.Unix).IsSingleComponent);
    }

    /// <summary>
    /// A lone <c>..</c> is one component but is not a single lookup, so the fast path must
    /// not claim it. If it did, a resolver could take the shortcut and open the parent
    /// directly -- one component out of the sandbox, with no check having run.
    /// </summary>
    [Fact]
    public void IsSingleComponent_is_false_for_a_lone_parent_link()
    {
        CapPath path = ParseOrFail("..", CapPathSyntax.Unix, ParentLinkPolicy.Preserve);

        Assert.Equal(1, path.ComponentCount);
        Assert.True(path.ContainsParentLink);
        Assert.False(path.IsSingleComponent);
    }

    /// <summary>
    /// Under the preserving policy, <c>..</c> survives as its own component in place. It is
    /// never removed, and neither is the component before it -- that removal is the lexical
    /// collapse this library refuses to perform, and doing it after the split rather than
    /// before would be no more correct.
    /// </summary>
    [Theory]
    [InlineData("a/../b", "a|..|b")]
    [InlineData("../a", "..|a")]
    [InlineData("a/..", "a|..")]
    [InlineData("a/../../b", "a|..|..|b")]
    public void Parent_links_are_preserved_in_place(string raw, string expected)
    {
        CapPath path = ParseOrFail(raw, CapPathSyntax.Unix, ParentLinkPolicy.Preserve);

        Assert.Equal(expected.Split('|'), Components(path));
        Assert.True(path.ContainsParentLink);
    }

    [Fact]
    public void ContainsParentLink_is_false_when_there_is_none()
    {
        Assert.False(ParseOrFail("a/b", CapPathSyntax.Unix, ParentLinkPolicy.Preserve).ContainsParentLink);
    }

    /// <summary>
    /// <c>foo/</c> and <c>foo</c> are different requests: the first must fail when
    /// <c>foo</c> is a regular file. Splitting into components throws the trailing
    /// separator away, so the distinction has to be recorded at parse time or it is gone.
    /// </summary>
    [Theory]
    [InlineData("a/b/", true)]
    [InlineData("a/b/.", true)]
    [InlineData("a/..", true)]
    [InlineData("a/b", false)]
    [InlineData("a/b.", false)]
    public void RequiresDirectory_survives_the_split(string raw, bool expected)
    {
        Assert.Equal(expected, ParseOrFail(raw, CapPathSyntax.Unix, ParentLinkPolicy.Preserve).RequiresDirectory);
    }

    /// <summary>
    /// Components are the caller's own characters. Nothing is normalised on the way through
    /// -- most importantly not Unicode, because the two encodings of an accented letter are
    /// different names to a Linux kernel, and folding them here would let a check be passed
    /// in one form and the file opened in the other.
    /// </summary>
    [Fact]
    public void Components_are_returned_verbatim()
    {
        const string Composed = "café";      // e-acute as one code point
        const string Decomposed = "café";   // e followed by a combining acute

        Assert.Equal([Composed], Components(ParseOrFail(Composed, CapPathSyntax.Unix)));
        Assert.Equal([Decomposed], Components(ParseOrFail(Decomposed, CapPathSyntax.Unix)));
        Assert.NotEqual(Composed, Decomposed);
    }

    /// <summary>The original string is handed back untouched, not a rebuilt one.</summary>
    [Fact]
    public void Raw_is_the_callers_string()
    {
        const string Raw = "a//b/./c";
        CapPath path = ParseOrFail(Raw, CapPathSyntax.Unix);

        Assert.Equal(Raw, path.ToString());
        Assert.True(path.Raw.SequenceEqual(Raw));
    }

    /// <summary>A default instance is inert rather than a trap.</summary>
    [Fact]
    public void Default_instance_names_nothing()
    {
        CapPath path = default;

        Assert.Equal(0, path.ComponentCount);
        Assert.False(path.IsSingleComponent);
        Assert.False(path.ContainsParentLink);
        Assert.Equal(string.Empty, path.ToString());
        Assert.Empty(Components(path));
    }

    private static CapPath ParseOrFail(
        string raw,
        CapPathSyntax syntax,
        ParentLinkPolicy parentLinks = ParentLinkPolicy.Reject)
    {
        Assert.True(
            CapPath.TryParse(raw, syntax, parentLinks, out CapPath path, out CapPathError error),
            $"expected '{raw}' to parse, got {error}");

        return path;
    }

    private static List<string> Components(CapPath path)
    {
        List<string> components = [];
        foreach (ReadOnlySpan<char> component in path.EnumerateComponents())
        {
            components.Add(component.ToString());
        }

        return components;
    }
}
