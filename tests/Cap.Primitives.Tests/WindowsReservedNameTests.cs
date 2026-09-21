namespace Cap.Primitives.Tests;

/// <summary>
/// The Windows device-name corpus: every reserved name crossed with every mangling that
/// still reaches the device.
/// </summary>
/// <remarks>
/// <para>
/// This is the mechanism behind a published escape in the Rust library this one is modelled
/// on. A sandboxed path of <c>CON</c> does not name a file under the directory handle at
/// all -- the object manager routes it to the console -- so a resolver that treats it as an
/// ordinary name hands back a handle to something outside the sandbox without ever
/// traversing out of it.
/// </para>
/// <para>
/// A directory named <c>CON</c> cannot be created on Windows, so refusing these names is
/// never a false positive there. They are ordinary filenames on Unix and are accepted under
/// Unix syntax, which is why the rules are selected by syntax rather than by the running OS.
/// </para>
/// </remarks>
public sealed class WindowsReservedNameTests
{
    /// <summary>
    /// The names as documented, before any mangling. <c>COM0</c> and <c>LPT0</c> are here
    /// although they are the least reachable of the set: the cost of refusing a name that
    /// might have been usable is that a caller renames a file, and the cost of allowing one
    /// that is not is a handle to a device.
    /// </summary>
    public static TheoryData<string> ReservedStems()
    {
        TheoryData<string> data = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"];

        string[] portPrefixes = ["COM", "LPT"];
        char[] superscripts = ['\u00b9', '\u00b2', '\u00b3'];

        foreach (string prefix in portPrefixes)
        {
            for (char digit = '0'; digit <= '9'; digit++)
            {
                data.Add(prefix + digit);
            }

            // The Latin-1 superscript spellings of one, two and three address the same
            // ports as the ASCII digits do.
            foreach (char superscript in superscripts)
            {
                data.Add(prefix + superscript);
            }
        }

        return data;
    }

    /// <summary>
    /// Each reserved name against each disguise. The extension cases are the surprising
    /// ones: Windows matches the device before the extension, so <c>CON.txt</c> is the
    /// console and not a text file.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReservedStems))]
    public void Every_mangling_of_a_reserved_name_is_refused(string stem)
    {
        string[] manglings =
        [
            stem,
            stem.ToLowerInvariant(),
            MixedCase(stem),
            stem + ".txt",
            stem.ToLowerInvariant() + ".TXT",
            stem + " .txt",
            stem + ".tar.gz",
            "dir\\" + stem,
            "dir/" + stem + ".txt",
            stem + "\\file.txt",
        ];

        foreach (string mangling in manglings)
        {
            Assert.Equal(CapPathError.ReservedName, Parse(mangling));
        }
    }

    /// <summary>
    /// Trailing dots and spaces are stripped below the API, so <c>CON.</c> and <c>CON </c>
    /// are the console too. They are reported as the device they reach rather than as a
    /// stray character, because that is the more useful half of the truth.
    /// </summary>
    [Theory]
    [InlineData("CON.")]
    [InlineData("CON ")]
    [InlineData("CON...")]
    [InlineData("NUL   ")]
    [InlineData("COM1.")]
    public void Trailing_dots_and_spaces_do_not_hide_a_device(string raw)
    {
        Assert.Equal(CapPathError.ReservedName, Parse(raw));
    }

    /// <summary>
    /// The alternate-data-stream forms are refused for the character, which happens first.
    /// Either answer is a refusal; the point is that no spelling gets through.
    /// </summary>
    [Theory]
    [InlineData("CON::$DATA")]
    [InlineData("COM1:")]
    [InlineData("NUL:stream")]
    public void Stream_syntax_is_refused(string raw)
    {
        Assert.Equal(CapPathError.InvalidCharacter, Parse(raw));
    }

    /// <summary>
    /// The boundaries of the blocklist. Refusing too much would make ordinary filenames
    /// unusable, and these are all real names a caller may legitimately have.
    /// </summary>
    [Theory]
    [InlineData("CONSOLE")]
    [InlineData("CONS")]
    [InlineData("CONIN")]
    [InlineData("CONOUT")]
    [InlineData("COM")]
    [InlineData("COM10")]
    [InlineData("COM1A")]
    [InlineData("LPT99")]
    [InlineData("NULL")]
    [InlineData("AUXILIARY")]
    [InlineData("PRINTER")]
    [InlineData("my.CON")]
    [InlineData("a_CON")]
    public void Names_that_merely_resemble_a_device_are_allowed(string raw)
    {
        Assert.Equal(CapPathError.None, Parse(raw));
    }

    /// <summary>
    /// The same names on Unix, where they are files like any other. A blanket refusal would
    /// be simpler and would make those files permanently unreachable.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReservedStems))]
    public void Reserved_names_are_ordinary_filenames_under_unix_syntax(string stem)
    {
        CapPath.TryParse(stem, CapPathSyntax.Unix, ParentLinkPolicy.Reject, out _, out CapPathError error);
        Assert.Equal(CapPathError.None, error);

        CapPath.TryParse(stem + ".txt", CapPathSyntax.Unix, ParentLinkPolicy.Reject, out _, out error);
        Assert.Equal(CapPathError.None, error);
    }

    private static string MixedCase(string value)
    {
        char[] characters = value.ToCharArray();
        for (int i = 1; i < characters.Length; i += 2)
        {
            characters[i] = char.ToLowerInvariant(characters[i]);
        }

        return new string(characters);
    }

    private static CapPathError Parse(string raw)
    {
        CapPath.TryParse(raw, CapPathSyntax.Windows, ParentLinkPolicy.Reject, out _, out CapPathError error);
        return error;
    }
}
