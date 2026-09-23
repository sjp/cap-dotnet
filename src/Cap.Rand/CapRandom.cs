using System.Security.Cryptography;
using Cap.Primitives;

namespace Cap.Rand;

/// <summary>
/// The operating system's cryptographic random number generator, as a capability.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Thin on purpose.</strong> Every byte comes from
/// <c>RandomNumberGenerator</c>, which is already correct and needs no help. The only
/// thing this type adds is that it has to be handed to you: the one way to get an instance is
/// <see cref="System"/>, which requires ambient authority. A component that needs
/// unpredictable bytes takes a <see cref="CapRandom"/> as a parameter, and the place the
/// entropy entered the program can be found by searching for that call.
/// </para>
/// <para>
/// <strong>Not interchangeable with the deterministic source.</strong> The type is sealed,
/// and <see cref="InsecureDeterministicRandom"/> is unrelated to it apart from the shared
/// <see cref="IRandomSource"/> interface. Code that must not be given a predictable sequence
/// takes this type rather than the interface, and then cannot be given one.
/// </para>
/// <para>
/// <strong>No <c>System.Random</c> surface.</strong> This type does not derive from it and has
/// no <c>Next</c> methods. Its integer helpers are the extension methods in
/// <see cref="RandomSourceExtensions"/>, which are uniform over the range asked for.
/// </para>
/// <para>
/// <strong>This is an audit, not a lock.</strong> The operating system's generator is
/// reachable from anywhere in the process through <c>RandomNumberGenerator</c>'s static
/// members, and nothing here changes that for code outside this library. What the token buys
/// is one searchable place where entropy enters a codebase that takes it from here and bans
/// the ambient members, recorded like every other acquisition of ambient authority.
/// </para>
/// <para>
/// Safe to use from any number of threads at once.
/// </para>
/// </remarks>
public sealed class CapRandom : IRandomSource
{
    private static readonly CapRandom Instance = new();

    private CapRandom()
    {
    }

    /// <summary>
    /// The operating system's cryptographic random number generator.
    /// </summary>
    /// <param name="authority">
    /// Ambient authority, because the operating system's entropy is not derived from anything
    /// the caller holds.
    /// </param>
    /// <returns>
    /// The same instance every time. It holds no state, so nothing is gained by caching it and
    /// nothing is lost by calling this more than once.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="authority"/> is <c>default</c> rather than a token that was acquired.
    /// </exception>
    public static CapRandom System(AmbientAuthority authority)
    {
        authority.Demand(nameof(authority));
        return Instance;
    }

    /// <inheritdoc/>
    public void Fill(Span<byte> destination)
    {
        // The one place in this library that is allowed to name the ambient generator: this
        // is where entropy enters, and the only way to reach it is through the token above.
#pragma warning disable CAP0007
        RandomNumberGenerator.Fill(destination);
#pragma warning restore CAP0007
    }
}
