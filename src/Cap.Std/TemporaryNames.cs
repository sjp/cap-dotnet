using System.Security.Cryptography;

namespace Cap.Std;

/// <summary>
/// Names for scratch objects, drawn so that nobody can work out what the next one will be.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Unguessability is the whole job.</strong> A scratch object is typically created in
/// a directory every account on the machine can write to, and the classic attack on one is
/// not to race the creation but to predict the name and get there first — planting a symbolic
/// link where the name is about to be used, so that a file the creator believes is theirs is
/// somebody else's file somewhere else. Exclusive creation defeats that only if the attacker
/// cannot know which name to plant, which makes the quality of these characters a security
/// property rather than a tidiness one.
/// </para>
/// <para>
/// So the bits come from the operating system's cryptographic generator and not from the
/// general-purpose one. The general-purpose generator is seeded from things an attacker can
/// often observe or influence and its output is reproducible from its state, which is exactly
/// the wrong property here; the framework's own scratch-name helper is not documented to be
/// cryptographic either, so it is not relied on.
/// </para>
/// </remarks>
internal static class TemporaryNames
{
    /// <summary>
    /// How many bytes of entropy each name carries.
    /// </summary>
    /// <remarks>
    /// Fifteen bytes is 120 bits, which is both far beyond guessing and an exact multiple of
    /// the five bits each character encodes — so the text below needs no padding and every
    /// character of it is entropy.
    /// </remarks>
    private const int EntropyBytes = 15;

    /// <summary>The characters a name is spelled with.</summary>
    /// <remarks>
    /// <para>
    /// Lower-case letters and digits, without the two letters that are read as digits when a
    /// name is copied out of a log. All one case deliberately: two names differing only in
    /// case are one name on a case-insensitive filesystem, and a generator that could produce
    /// such a pair would be quietly weaker there than the bit count suggests.
    /// </para>
    /// <para>
    /// Thirty-two characters, so each one carries exactly five bits and the conversion below
    /// is a shift rather than a division with a bias to reason about.
    /// </para>
    /// </remarks>
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>What every scratch name begins with.</summary>
    /// <remarks>
    /// Not a security property — it is the same for every name and an attacker may assume it.
    /// It is there so that an operator who finds one of these left behind can tell what put
    /// it there, which is worth four characters.
    /// </remarks>
    private const string Prefix = "cap-";

    /// <summary>
    /// How many times a creation may be retried when the name it drew was already taken.
    /// </summary>
    /// <remarks>
    /// A collision at this width does not happen by chance, so a retry is not a way of
    /// coping with a crowded directory — it is there because the alternative reading of a
    /// taken name is that somebody is guessing, and the answer to that is to draw again from
    /// a space they are not going to exhaust. A budget rather than a loop, so that a
    /// directory contriving to report every name as taken ends as a failure instead of as a
    /// program that never returns.
    /// </remarks>
    public const int Attempts = 8;

    /// <summary>Draws a name.</summary>
    public static string Next()
    {
        Span<byte> entropy = stackalloc byte[EntropyBytes];
        RandomNumberGenerator.Fill(entropy);

        Span<char> name = stackalloc char[Prefix.Length + (EntropyBytes * 8 / 5)];
        Prefix.CopyTo(name);

        int at = Prefix.Length;
        for (int i = 0; i < entropy.Length; i += 5)
        {
            // Five bytes at a time, because forty bits divide evenly into eight characters of
            // five bits each. Taking them one byte at a time would leave a remainder to carry
            // between characters, which is where this kind of loop usually goes wrong.
            ulong block = ((ulong)entropy[i] << 32) |
                          ((ulong)entropy[i + 1] << 24) |
                          ((ulong)entropy[i + 2] << 16) |
                          ((ulong)entropy[i + 3] << 8) |
                          entropy[i + 4];

            for (int shift = 35; shift >= 0; shift -= 5)
            {
                name[at++] = Alphabet[(int)((block >> shift) & 0x1F)];
            }
        }

        return new string(name);
    }
}
