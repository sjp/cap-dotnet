namespace Cap.Rand;

/// <summary>
/// Something that fills a buffer with bytes, for code that has no reason to care where they
/// come from.
/// </summary>
/// <remarks>
/// <para>
/// Two types in this library implement it: <see cref="CapRandom"/>, which is the operating
/// system's cryptographic generator, and <see cref="InsecureDeterministicRandom"/>, which is a
/// seeded sequence for tests and simulations. The integer helpers in
/// <see cref="RandomSourceExtensions"/> are written against this interface and so work on
/// either.
/// </para>
/// <para>
/// <strong>Taking this interface is a statement that security does not matter.</strong> A
/// component that shuffles a playlist, picks a jittered retry delay or samples one request in
/// a thousand can accept an <see cref="IRandomSource"/>, and a test can then hand it a
/// deterministic sequence and get the same run every time. A component whose output must be
/// unpredictable to someone else — a key, a token, a nonce, a name another account must not
/// guess — takes <see cref="CapRandom"/> itself. Because the deterministic type does not
/// derive from it and cannot be converted to it, nothing can hand such a component a
/// predictable sequence, by mistake or otherwise.
/// </para>
/// <para>
/// The interface is deliberately a single method. It carries none of the shape of
/// <c>System.Random</c>, whose <c>Next(int)</c> invites the reduction by remainder that makes
/// small values more likely than large ones; the helpers here reject and redraw instead.
/// </para>
/// </remarks>
public interface IRandomSource
{
    /// <summary>
    /// Overwrites every byte of <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The buffer to fill. An empty buffer is allowed and draws nothing.</param>
    /// <remarks>
    /// Whether this may be called from several threads at once is the implementation's to
    /// say. <see cref="CapRandom"/> allows it; <see cref="InsecureDeterministicRandom"/> does
    /// not. Code written against the interface that shares one source between threads should
    /// assume it does not.
    /// </remarks>
    void Fill(Span<byte> destination);
}
