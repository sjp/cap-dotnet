namespace Cap.Primitives;

/// <summary>
/// Windows-specific validation of a single path component.
/// </summary>
/// <remarks>
/// <para>
/// Windows does not treat a filename as an opaque string. Below the Win32 API a component is
/// rewritten — trailing dots and spaces removed — and then matched against a set of names
/// that address character devices rather than files. Both happen <em>after</em> anything a
/// caller wrote, so a containment check performed on the original string is a check on a
/// name that will never reach the filesystem.
/// </para>
/// <para>
/// That is not hypothetical: it is the mechanism behind a published escape in the Rust
/// library this one is modelled on, where a sandboxed path of <c>CON</c> handed back a
/// handle to the console.
/// </para>
/// <para>
/// These rules are a blocklist, and a blocklist of OS behaviour ages badly — the reserved
/// set has grown before and can grow again. Rejecting here is therefore the first of two
/// defences, not the only one; a handle opened on Windows must also be interrogated after
/// the fact to confirm it names a filesystem object, so that a name nobody anticipated still
/// cannot be used.
/// </para>
/// </remarks>
internal static class WindowsNames
{
    /// <summary>
    /// Characters Windows forbids in a filename, minus the two separators (which can never
    /// appear inside a component). <c>:</c> is here because it introduces an alternate data
    /// stream: <c>file:stream</c> names a second, hidden body of the same file, and
    /// <c>CON::$DATA</c> reaches the device through one. The rest — <c>* ?</c> and the DOS
    /// wildcard equivalents <c>&lt; &gt; "</c> — are interpreted as patterns by the native
    /// open call, so a single name could match something other than itself.
    /// </summary>
    private const string ForbiddenCharacters = "<>:\"|?*";

    /// <summary>
    /// Validates one component against Windows' rules.
    /// </summary>
    /// <param name="component">
    /// A non-empty component containing no separator, and not <c>.</c> or <c>..</c>.
    /// </param>
    /// <returns><see cref="CapPathError.None"/> if the component is acceptable.</returns>
    public static CapPathError ValidateComponent(ReadOnlySpan<char> component)
    {
        foreach (char c in component)
        {
            // Below 0x20 covers U+0000, which would truncate the path where it is handed to
            // the kernel as a counted string, and the control characters the shell and the
            // object manager both treat specially.
            if (c < ' ' || ForbiddenCharacters.Contains(c))
            {
                return CapPathError.InvalidCharacter;
            }
        }

        // Device-name matching happens before the trailing-dot-and-space check, so that
        // `CON.` and `CON ` are reported as what they are — the console under a disguise —
        // rather than as a stray character at the end of an ordinary name.
        if (IsReservedDeviceName(DeviceStem(component)))
        {
            return CapPathError.ReservedName;
        }

        char last = component[^1];
        if (last is '.' or ' ')
        {
            return CapPathError.TrailingDotOrSpace;
        }

        return CapPathError.None;
    }

    /// <summary>
    /// The part of a component that Windows matches against the device table: everything
    /// before the first dot, with trailing spaces removed.
    /// </summary>
    /// <remarks>
    /// Both halves of that matter. Cutting at the first dot is why <c>CON.txt</c> is the
    /// console and not a text file — the device name wins over the extension. Trimming
    /// spaces is why <c>CON .txt</c> is too. A stem is never <c>.</c> or <c>..</c> here
    /// because those components are handled before this is reached.
    /// </remarks>
    private static ReadOnlySpan<char> DeviceStem(ReadOnlySpan<char> component)
    {
        int dot = component.IndexOf('.');
        ReadOnlySpan<char> stem = dot < 0 ? component : component[..dot];
        return stem.TrimEnd(' ');
    }

    /// <summary>
    /// Whether a stem names a character device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The comparison is ASCII-case-insensitive: <c>con</c>, <c>CON</c> and <c>CoN</c> are
    /// one name. It is deliberately not culture-aware — a culture-sensitive comparison would
    /// make the answer depend on the thread's locale, and under a Turkish locale the
    /// uppercase of <c>i</c> is not <c>I</c>, which is the kind of difference that turns a
    /// security check into a coin toss.
    /// </para>
    /// <para>
    /// The superscript spellings are real. The serial and parallel port names have Latin-1
    /// superscript forms for the digits one to three, and they address the same devices.
    /// <c>COM0</c> and <c>LPT0</c> are included although a file of that name is not
    /// obviously reachable: the set is documented as reserved, and refusing a name that
    /// might have been usable costs a caller nothing they cannot work around by renaming.
    /// </para>
    /// </remarks>
    private static bool IsReservedDeviceName(ReadOnlySpan<char> stem) => stem.Length switch
    {
        3 => Matches(stem, "CON") || Matches(stem, "PRN") || Matches(stem, "AUX") || Matches(stem, "NUL"),
        4 => (Matches(stem[..3], "COM") || Matches(stem[..3], "LPT")) && IsPortDigit(stem[3]),
        6 => Matches(stem, "CONIN$"),
        7 => Matches(stem, "CONOUT$"),
        _ => false,
    };

    private static bool IsPortDigit(char c) => c is >= '0' and <= '9' or '¹' or '²' or '³';

    private static bool Matches(ReadOnlySpan<char> stem, string name) =>
        stem.Equals(name, StringComparison.OrdinalIgnoreCase);
}
