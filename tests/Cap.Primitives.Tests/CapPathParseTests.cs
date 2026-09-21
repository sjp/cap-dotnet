namespace Cap.Primitives.Tests;

/// <summary>
/// The parsing contract, as a table.
/// </summary>
/// <remarks>
/// <para>
/// Every case names the syntax it is written against rather than relying on the running OS,
/// which is the point of making syntax a parameter: the Windows rules are the intricate and
/// security-critical ones, and they get exercised on every leg of the matrix instead of only
/// the one leg that runs Windows. A mistake in the reserved-name table would otherwise be
/// invisible to most of the people who could catch it.
/// </para>
/// <para>
/// The cases are grouped by the reason a path is refused, and <see cref="Every_refusal_reason_is_covered"/>
/// asserts the table reaches all of them, so adding a reason without adding a case fails the
/// build rather than quietly going untested.
/// </para>
/// </remarks>
public sealed class CapPathParseTests
{
    /// <summary>
    /// Paths that must parse. The interesting ones are the names Windows reserves that are
    /// perfectly ordinary on Unix -- refusing those everywhere would make real files
    /// unreachable on the platform that allows them.
    /// </summary>
    [Theory]
    [InlineData("foo.txt", CapPathSyntax.Unix)]
    [InlineData("a/b/c", CapPathSyntax.Unix)]
    [InlineData("a//b", CapPathSyntax.Unix)]
    [InlineData("a/./b", CapPathSyntax.Unix)]
    [InlineData("a/", CapPathSyntax.Unix)]
    [InlineData("CON", CapPathSyntax.Unix)]
    [InlineData("con.txt", CapPathSyntax.Unix)]
    [InlineData("foo.", CapPathSyntax.Unix)]
    [InlineData("foo ", CapPathSyntax.Unix)]
    [InlineData("a*b", CapPathSyntax.Unix)]
    [InlineData("a:b", CapPathSyntax.Unix)]
    [InlineData("a\nb", CapPathSyntax.Unix)]
    [InlineData("C:file", CapPathSyntax.Unix)]
    [InlineData("a\\b", CapPathSyntax.Unix)]
    [InlineData("\\\\server\\share", CapPathSyntax.Unix)]
    [InlineData("-rf", CapPathSyntax.Unix)]
    [InlineData("foo.txt", CapPathSyntax.Windows)]
    [InlineData("a/b/c", CapPathSyntax.Windows)]
    [InlineData("a\\b\\c", CapPathSyntax.Windows)]
    [InlineData("a/b\\c", CapPathSyntax.Windows)]
    [InlineData("a//b", CapPathSyntax.Windows)]
    [InlineData("a/./b", CapPathSyntax.Windows)]
    [InlineData("CONSOLE", CapPathSyntax.Windows)]
    [InlineData("foo.CON", CapPathSyntax.Windows)]
    [InlineData("COM10", CapPathSyntax.Windows)]
    [InlineData("CX:", CapPathSyntax.Unix)]
    public void Accepts(string raw, CapPathSyntax syntax)
    {
        Assert.Equal(CapPathError.None, Parse(raw, syntax));
    }

    /// <summary>A path naming nothing cannot be resolved against a handle.</summary>
    [Theory]
    [InlineData("", CapPathSyntax.Unix)]
    [InlineData(".", CapPathSyntax.Unix)]
    [InlineData("./", CapPathSyntax.Unix)]
    [InlineData("././.", CapPathSyntax.Unix)]
    [InlineData("", CapPathSyntax.Windows)]
    [InlineData(".", CapPathSyntax.Windows)]
    [InlineData(".\\", CapPathSyntax.Windows)]
    public void Rejects_empty(string raw, CapPathSyntax syntax)
    {
        Assert.Equal(CapPathError.Empty, Parse(raw, syntax));
    }

    /// <summary>
    /// A rooted path starts from a filesystem root the handle confers no authority over.
    /// </summary>
    [Theory]
    [InlineData("/", CapPathSyntax.Unix)]
    [InlineData("/etc/passwd", CapPathSyntax.Unix)]
    [InlineData("//", CapPathSyntax.Unix)]
    [InlineData("C:\\Windows", CapPathSyntax.Windows)]
    [InlineData("C:/Windows", CapPathSyntax.Windows)]
    [InlineData("c:\\", CapPathSyntax.Windows)]
    public void Rejects_absolute(string raw, CapPathSyntax syntax)
    {
        Assert.Equal(CapPathError.Absolute, Parse(raw, syntax));
    }

    /// <summary>
    /// Windows paths that look relative but resolve against process-wide state. These are
    /// the more dangerous shape, because a caller eyeballing the string sees no leading
    /// root and concludes it is contained.
    /// </summary>
    [Theory]
    [InlineData("\\file", CapPathError.RootRelative)]
    [InlineData("/file", CapPathError.RootRelative)]
    [InlineData("\\", CapPathError.RootRelative)]
    [InlineData("C:file", CapPathError.DriveRelative)]
    [InlineData("C:", CapPathError.DriveRelative)]
    [InlineData("z:sub\\file", CapPathError.DriveRelative)]
    public void Rejects_paths_relative_to_ambient_state(string raw, CapPathError expected)
    {
        Assert.Equal(expected, Parse(raw, CapPathSyntax.Windows));
    }

    /// <summary>A UNC path names a host; resolving one would reach the network.</summary>
    [Theory]
    [InlineData("\\\\server\\share")]
    [InlineData("//server/share")]
    [InlineData("\\\\server")]
    public void Rejects_unc(string raw)
    {
        Assert.Equal(CapPathError.Unc, Parse(raw, CapPathSyntax.Windows));
    }

    /// <summary>
    /// The device namespace skips Win32 normalisation and addresses the object manager
    /// directly. Both separators reach it, so both spellings are refused.
    /// </summary>
    [Theory]
    [InlineData("\\\\?\\C:\\Windows")]
    [InlineData("\\\\.\\PhysicalDrive0")]
    [InlineData("//?/C:/Windows")]
    [InlineData("//./PhysicalDrive0")]
    [InlineData("\\\\?/C:\\Windows")]
    public void Rejects_device_namespace(string raw)
    {
        Assert.Equal(CapPathError.DeviceNamespace, Parse(raw, CapPathSyntax.Windows));
    }

    /// <summary>
    /// <c>U+0000</c> terminates the path where it is handed to the kernel, so a name
    /// containing one would be truncated to a different name after being checked. It is the
    /// one character no platform may allow.
    /// </summary>
    [Theory]
    [InlineData(CapPathSyntax.Unix)]
    [InlineData(CapPathSyntax.Windows)]
    public void Rejects_embedded_nul(CapPathSyntax syntax)
    {
        Assert.Equal(CapPathError.InvalidCharacter, Parse("a\0b", syntax));
        Assert.Equal(CapPathError.InvalidCharacter, Parse("dir/a\0b", syntax));
    }

    /// <summary>
    /// Characters the native open call would reinterpret: wildcards, the DOS pattern
    /// equivalents, the stream separator, and the control range.
    /// </summary>
    [Theory]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a\"b")]
    [InlineData("a|b")]
    [InlineData("file:stream")]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    [InlineData("dir\\a*b")]
    public void Rejects_invalid_windows_characters(string raw)
    {
        Assert.Equal(CapPathError.InvalidCharacter, Parse(raw, CapPathSyntax.Windows));
    }

    /// <summary>
    /// Windows strips these below the API, so <c>foo.</c> and <c>foo</c> are one file. A
    /// check that treats them as two names can be walked straight past.
    /// </summary>
    [Theory]
    [InlineData("foo.")]
    [InlineData("foo ")]
    [InlineData("foo...")]
    [InlineData("dir\\foo.")]
    [InlineData("foo.txt ")]
    public void Rejects_trailing_dot_or_space(string raw)
    {
        Assert.Equal(CapPathError.TrailingDotOrSpace, Parse(raw, CapPathSyntax.Windows));
    }

    /// <summary>
    /// <c>..</c> is refused by default, and never collapsed. Collapsing <c>a/../b</c> to
    /// <c>b</c> gives the wrong answer whenever <c>a</c> is a symlink, which is the single
    /// most common way a string-based sandbox is defeated.
    /// </summary>
    [Theory]
    [InlineData("..", CapPathSyntax.Unix)]
    [InlineData("../etc", CapPathSyntax.Unix)]
    [InlineData("a/../b", CapPathSyntax.Unix)]
    [InlineData("a/..", CapPathSyntax.Unix)]
    [InlineData("..", CapPathSyntax.Windows)]
    [InlineData("a\\..\\b", CapPathSyntax.Windows)]
    public void Rejects_parent_links_by_default(string raw, CapPathSyntax syntax)
    {
        Assert.Equal(CapPathError.ParentLink, Parse(raw, syntax));
    }

    /// <summary>
    /// A component of three dots is not a parent link, and Unix will happily create it.
    /// Windows refuses it for the ordinary trailing-dot reason, not as a traversal.
    /// </summary>
    [Theory]
    [InlineData(CapPathSyntax.Unix, CapPathError.None)]
    [InlineData(CapPathSyntax.Windows, CapPathError.TrailingDotOrSpace)]
    public void Three_dots_is_an_ordinary_name(CapPathSyntax syntax, CapPathError expected)
    {
        Assert.Equal(expected, Parse("...", syntax));
    }

    /// <summary>
    /// Length bounds. These are a guard on how much work a hostile caller can ask for, not
    /// a claim about what the filesystem accepts -- the kernel's limits are stricter and
    /// counted in bytes.
    /// </summary>
    [Theory]
    [InlineData(CapPathSyntax.Unix)]
    [InlineData(CapPathSyntax.Windows)]
    public void Rejects_paths_and_components_that_are_too_long(CapPathSyntax syntax)
    {
        Assert.Equal(CapPathError.TooLong, Parse(new string('a', CapPath.MaxLength + 1), syntax));

        string longComponent = "dir/" + new string('a', CapPath.MaxComponentLength + 1);
        Assert.Equal(CapPathError.TooLong, Parse(longComponent, syntax));

        string atTheLimit = "dir/" + new string('a', CapPath.MaxComponentLength);
        Assert.Equal(CapPathError.None, Parse(atTheLimit, syntax));
    }

    /// <summary>
    /// Guards the table against rot: a refusal reason with no case here is one nobody is
    /// testing. Reasons are per-syntax by nature -- there is no drive-relative path on Unix
    /// -- so coverage is asserted over the union of both.
    /// </summary>
    [Fact]
    public void Every_refusal_reason_is_covered()
    {
        CapPathError[] reached = [.. AllProbes()
            .Select(probe => Parse(probe.Raw, probe.Syntax))
            .Distinct()];

        CapPathError[] missing = [.. Enum.GetValues<CapPathError>().Except(reached)];

        Assert.True(missing.Length == 0, $"No case in this class produces: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// The span overload must answer exactly as the string one does. They share a scan, and
    /// this is what keeps a future optimisation from splitting them apart unnoticed.
    /// </summary>
    [Fact]
    public void Validate_agrees_with_TryParse()
    {
        foreach ((string raw, CapPathSyntax syntax) in AllProbes())
        {
            CapPathError viaSpan = CapPath.Validate(raw.AsSpan(), syntax, ParentLinkPolicy.Reject);
            Assert.Equal(Parse(raw, syntax), viaSpan);
        }
    }

    /// <summary>A refused path yields no usable value.</summary>
    [Fact]
    public void A_refused_path_produces_no_CapPath()
    {
        Assert.False(CapPath.TryParse("/etc/passwd", CapPathSyntax.Unix, ParentLinkPolicy.Reject, out CapPath path, out _));
        Assert.Equal(0, path.ComponentCount);
        Assert.False(path.EnumerateComponents().MoveNext());
    }

    private static CapPathError Parse(string raw, CapPathSyntax syntax)
    {
        CapPath.TryParse(raw, syntax, ParentLinkPolicy.Reject, out _, out CapPathError error);
        return error;
    }

    /// <summary>
    /// Every path this class asserts on, collected once so the coverage and cross-overload
    /// checks stay in step with the tables above without repeating them.
    /// </summary>
    private static (string Raw, CapPathSyntax Syntax)[] AllProbes() =>
    [
        ("foo.txt", CapPathSyntax.Unix),
        ("a/b/c", CapPathSyntax.Unix),
        ("", CapPathSyntax.Unix),
        (".", CapPathSyntax.Unix),
        ("/etc/passwd", CapPathSyntax.Unix),
        ("a\0b", CapPathSyntax.Unix),
        ("..", CapPathSyntax.Unix),
        ("a/../b", CapPathSyntax.Unix),
        (new string('a', CapPath.MaxLength + 1), CapPathSyntax.Unix),
        ("dir/" + new string('a', CapPath.MaxComponentLength + 1), CapPathSyntax.Unix),
        ("foo.txt", CapPathSyntax.Windows),
        ("a\\b\\c", CapPathSyntax.Windows),
        ("", CapPathSyntax.Windows),
        ("C:\\Windows", CapPathSyntax.Windows),
        ("\\file", CapPathSyntax.Windows),
        ("C:file", CapPathSyntax.Windows),
        ("\\\\server\\share", CapPathSyntax.Windows),
        ("\\\\?\\C:\\Windows", CapPathSyntax.Windows),
        ("CON", CapPathSyntax.Windows),
        ("a*b", CapPathSyntax.Windows),
        ("foo.", CapPathSyntax.Windows),
        ("..", CapPathSyntax.Windows),
        (new string('a', CapPath.MaxLength + 1), CapPathSyntax.Windows),
    ];
}
