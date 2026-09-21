namespace Cap.Primitives;

/// <summary>
/// Which platform's path rules to apply when parsing.
/// </summary>
/// <remarks>
/// <para>
/// Parsing is a pure function of the input string and one of these values. Nothing about
/// path validation consults the running OS directly — that is what lets the Windows rules,
/// which are the intricate and security-critical ones, be exercised in full on a Linux
/// build agent rather than only on the one leg of the matrix that runs Windows.
/// </para>
/// <para>
/// The consequence is that this is a syntax selector, not a portability shim: parsing a
/// path with <see cref="Windows"/> on Linux tells you whether Windows would accept it, and
/// says nothing about whether the local filesystem would.
/// </para>
/// </remarks>
public enum CapPathSyntax
{
    /// <summary>
    /// POSIX rules. <c>/</c> is the only separator; every byte except <c>/</c> and
    /// <c>U+0000</c> is a legal filename character, including backslashes, newlines and
    /// characters Windows reserves.
    /// </summary>
    Unix,

    /// <summary>
    /// Windows rules. Both <c>/</c> and <c>\</c> separate components, and a component is
    /// additionally subject to reserved device names, forbidden characters, and the trailing
    /// dot and space stripping that happens below the Win32 API.
    /// </summary>
    Windows,
}
