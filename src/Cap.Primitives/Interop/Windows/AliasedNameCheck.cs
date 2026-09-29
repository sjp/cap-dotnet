namespace Cap.Primitives.Interop.Windows;

/// <summary>
/// Decides whether an open object was reached by a name that is not its own.
/// </summary>
/// <remarks>
/// <para>
/// A filesystem that generates short names records two names for the same entry — the one it
/// was created with and an eight-plus-three alias derived from it — and an open by either
/// reaches the same object. Nothing about that leaves the directory the name was looked up in,
/// so it is not an escape; what it defeats is any rule a caller states about names. A caller
/// that refuses to serve <c>secret documents</c> is not refusing <c>SECRET~1</c>, and both are
/// the same file.
/// </para>
/// <para>
/// So the object is asked what it is called. Every generated alias contains a tilde, which
/// makes the presence of one a cheap and complete trigger: a component without one cannot be a
/// generated alias, and nothing is asked. A component genuinely named with a tilde answers with
/// itself and is allowed through — which is why the test is a comparison and not a refusal of
/// the character.
/// </para>
/// <para>
/// The name is asked for in its normalised form, in which every component is the one the
/// filesystem stores; the plain form repeats the spelling the object was opened by, alias and
/// all. The normalised query is recent, and redirectors and third-party filesystems often do
/// not implement it. Where it is declined the entry is asked for its alias instead, the older
/// and far more widely implemented question. An entry with no alias, or on a filesystem that
/// declines that question too, has no second name to have been reached by, and is allowed.
/// An entry whose alias is some other spelling was reached by its own name, and is allowed.
/// An entry whose alias is the very name that was asked for is refused — including one whose
/// own name happens already to be eight-plus-three, since the alias alone cannot tell the two
/// apart and the refusal is the side to err on. Names such as <c>~$report.docx</c> or
/// <c>notes.txt~</c> are never eight-plus-three and never meet that case.
/// </para>
/// <para>
/// Any other failure of either question fails the open rather than having the check skipped,
/// since a check that stood aside there would stand aside silently.
/// </para>
/// <para>
/// The comparisons ignore case because the filesystem does, and a name differing from the
/// stored one only in case is the same name by the only definition that matters here.
/// </para>
/// <para>
/// The two questions are passed in rather than asked here so the decision can be exercised on
/// every build agent, with replies no local volume would give.
/// </para>
/// </remarks>
internal static class AliasedNameCheck
{
    /// <summary>Asks an open object one question whose reply is a name.</summary>
    internal delegate CapError NameQuery(nint handle, out string name);

    /// <summary>
    /// Refuses, as <see cref="CapErrorCategory.AliasedName"/>, a handle reached by a name that
    /// is not the object's own.
    /// </summary>
    /// <param name="handle">The object that was opened.</param>
    /// <param name="requested">The single component it was opened by.</param>
    /// <param name="normalized">Answers the object's path from its volume root, every
    /// component in its stored spelling.</param>
    /// <param name="alternate">Answers the entry's generated eight-plus-three alias.</param>
    internal static CapError Refuse(
        nint handle, ReadOnlySpan<char> requested, NameQuery normalized, NameQuery alternate)
    {
        if (!requested.Contains('~'))
        {
            return CapError.Success;
        }

        CapError error = normalized(handle, out string full);
        if (error.IsSuccess)
        {
            int separator = full.LastIndexOf('\\');
            ReadOnlySpan<char> stored = separator < 0 ? full : full.AsSpan(separator + 1);

            return stored.Equals(requested, StringComparison.OrdinalIgnoreCase)
                ? CapError.Success
                : CapError.FromCategory(CapErrorCategory.AliasedName);
        }

        if (!Declined(error))
        {
            return error;
        }

        error = alternate(handle, out string alias);
        if (error.IsFailure)
        {
            // An entry with no alias is reported as a name that is not found, and one on a
            // filesystem that does not make aliases by the question being declined; either way
            // there is no second name.
            bool noAlias = error.Source == CapErrorSource.NtStatus &&
                error.RawCode == NtStatusCodes.STATUS_OBJECT_NAME_NOT_FOUND;
            return Declined(error) || noAlias
                ? CapError.Success
                : error;
        }

        return alias.Length != 0 && alias.AsSpan().Equals(requested, StringComparison.OrdinalIgnoreCase)
            ? CapError.FromCategory(CapErrorCategory.AliasedName)
            : CapError.Success;
    }

    /// <summary>
    /// Whether a query failed because the filesystem declined the question rather than because
    /// answering it went wrong.
    /// </summary>
    /// <remarks>
    /// A reply this layer found malformed carries one of the same statuses but is classified
    /// as unknown rather than read from the table, and is a failure to answer, not a refusal to
    /// be asked.
    /// </remarks>
    private static bool Declined(CapError error) =>
        error.Source == CapErrorSource.NtStatus &&
        (error.Category is CapErrorCategory.NotSupported or CapErrorCategory.InvalidArgument) &&
        NtStatusCodes.IsUnsupportedClass(error.RawCode);
}
