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
    /// Every disguise a device name can wear and still reach the device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The extension cases are the surprising ones: Windows matches the device before the
    /// extension, so <c>CON.txt</c> is the console and not a text file, and no number of
    /// further extensions changes that.
    /// </para>
    /// <para>
    /// The trailing dots and spaces are the subtle ones. They are stripped below the API,
    /// which means a checker that treats <c>CON.</c> as an ordinary name is checking a name
    /// that will never reach the filesystem. The same stripping happens between a name and
    /// its extension, which is why <c>CON .txt</c> is the console too.
    /// </para>
    /// <para>
    /// Position is in here as well. A device name is recognised wherever it occurs, not only
    /// at the start of a path, so a reserved component with ordinary components on either
    /// side of it is still reserved.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> Manglings(string stem)
    {
        yield return stem;
        yield return stem.ToLowerInvariant();
        yield return MixedCase(stem);

        yield return stem + ".txt";
        yield return stem.ToLowerInvariant() + ".TXT";
        yield return stem + ".tar.gz";
        yield return stem + " .txt";

        yield return stem + ".";
        yield return stem + "...";
        yield return stem + " ";
        yield return stem + "   ";
        yield return stem + ". ";

        yield return "dir\\" + stem;
        yield return "dir/" + stem + ".txt";
        yield return stem + "\\file.txt";
        yield return "dir/" + stem + "/file.txt";
        yield return "dir\\" + stem + ".\\file.txt";
    }

    /// <summary>
    /// Every reserved name crossed with every disguise, all refused as the device they
    /// reach.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReservedStems))]
    public void Every_mangling_of_a_reserved_name_is_refused(string stem)
    {
        foreach (string mangling in Manglings(stem))
        {
            Assert.Equal(CapPathError.ReservedName, Parse(mangling));
        }
    }

    /// <summary>
    /// Every reserved name crossed with the alternate-data-stream spellings.
    /// </summary>
    /// <remarks>
    /// A stream is a second, hidden body of the same file, and naming one is how
    /// <c>CON::$DATA</c> reaches the console. These are refused for the <c>:</c>, which is
    /// checked before the name is matched against the device table — so the answer names the
    /// character rather than the device. Either answer is a refusal, and the refusal is the
    /// property: no spelling gets through.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ReservedStems))]
    public void Every_stream_spelling_of_a_reserved_name_is_refused(string stem)
    {
        string[] streams =
        [
            stem + ":",
            stem + ":stream",
            stem + "::$DATA",
            stem + ":$DATA",
            stem + ".txt:stream",
            "dir/" + stem + "::$DATA",
        ];

        foreach (string raw in streams)
        {
            Assert.Equal(CapPathError.InvalidCharacter, Parse(raw));
        }
    }

    /// <summary>
    /// A device-namespace prefix in front of a device name is refused for the prefix, which
    /// is classified before any component is looked at.
    /// </summary>
    /// <remarks>
    /// These spellings hand the rest of the string to the object manager with the Win32
    /// normalisation skipped, which is a way to reach a device that does not depend on the
    /// name being reserved at all. Both separators reach the same place, so both are refused.
    /// </remarks>
    [Theory]
    [InlineData("\\\\.\\CON")]
    [InlineData("\\\\.\\NUL")]
    [InlineData("\\\\.\\COM1")]
    [InlineData("\\\\?\\CON")]
    [InlineData("\\\\?\\C:\\CON")]
    [InlineData("//./CON")]
    [InlineData("//?/NUL")]
    [InlineData("\\\\./CONIN$")]
    public void A_device_namespace_prefix_on_a_device_name_is_refused(string raw)
    {
        Assert.Equal(CapPathError.DeviceNamespace, Parse(raw));
    }

    /// <summary>
    /// The same spellings in the middle of a path are not prefixes, and the name behind them
    /// is still checked.
    /// </summary>
    /// <remarks>
    /// <c>\\?\</c> and <c>\\.\</c> mean something only at the start of a path. Further in,
    /// they are separators around a component of <c>?</c> or <c>.</c>, and each is dealt with
    /// on its own terms: <c>?</c> is a character the native open would read as a pattern, and
    /// <c>.</c> names the directory it is already in and is dropped. Either way the component
    /// that follows gets the same scrutiny it would have had anywhere else, which is what
    /// keeps the prefix classification from being the only thing between a caller and a
    /// device.
    /// </remarks>
    [Theory]
    [InlineData("dir\\\\?\\CON", CapPathError.InvalidCharacter)]
    [InlineData("dir\\\\.\\CON", CapPathError.ReservedName)]
    [InlineData("dir/./NUL", CapPathError.ReservedName)]
    [InlineData("dir\\\\.\\ordinary.txt", CapPathError.None)]
    public void A_device_namespace_spelling_inside_a_path_is_not_a_prefix(string raw, CapPathError expected)
    {
        Assert.Equal(expected, Parse(raw));
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
