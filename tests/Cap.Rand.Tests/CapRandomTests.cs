using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Cap.Primitives;

namespace Cap.Rand.Tests;

/// <summary>
/// The operating system's generator handed out behind a token, and the type-level separation
/// between it and the seeded sequence.
/// </summary>
/// <remarks>
/// The bytes themselves are not tested for quality: they come straight from the platform's
/// cryptographic generator, and a statistical test here would say more about the test than
/// about the generator. What is tested is the part this library adds: that the only way in
/// is through the token, and that nothing predictable can be passed where the secure type
/// is asked for.
/// </remarks>
public sealed class CapRandomTests
{
    /// <summary>A value nobody acquired is not a token, and gets nobody any entropy.</summary>
    [Fact]
    public void The_system_generator_requires_an_acquired_token()
    {
        Assert.Throws<ArgumentException>("authority", () => CapRandom.System(default));
    }

    [Fact]
    public void The_system_generator_is_one_stateless_instance()
    {
        Assert.Same(
            CapRandom.System(AmbientAuthority.Acquire()),
            CapRandom.System(AmbientAuthority.Acquire()));
    }

    /// <summary>
    /// Sixty-four bytes that all came out zero would mean the buffer was never written. The
    /// chance of it happening honestly is 2^-512.
    /// </summary>
    [Fact]
    public void Fill_writes_the_whole_buffer()
    {
        var random = CapRandom.System(AmbientAuthority.Acquire());

        byte[] buffer = new byte[64];
        random.Fill(buffer);

        Assert.Contains(buffer, b => b != 0);
        Assert.Contains(buffer[^8..], b => b != 0);
    }

    [Fact]
    public void Fill_accepts_an_empty_buffer()
    {
        CapRandom.System(AmbientAuthority.Acquire()).Fill([]);
    }

    /// <summary>
    /// The deterministic type cannot be passed, cast or converted to the secure one, so
    /// a parameter of the secure type is a guarantee rather than a convention.
    /// </summary>
    [Fact]
    public void The_deterministic_source_cannot_stand_in_for_the_secure_one()
    {
        Assert.True(typeof(CapRandom).IsSealed);
        Assert.False(typeof(CapRandom).IsAssignableFrom(typeof(InsecureDeterministicRandom)));
        Assert.False(typeof(InsecureDeterministicRandom).IsAssignableFrom(typeof(CapRandom)));

        // No conversion operator in either direction, which would otherwise make the
        // assignment compile despite the types being unrelated.
        Assert.DoesNotContain(
            typeof(CapRandom).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Concat(typeof(InsecureDeterministicRandom).GetMethods(BindingFlags.Public | BindingFlags.Static)),
            m => m.Name is "op_Implicit" or "op_Explicit");
    }

    /// <summary>
    /// Neither type offers the <c>System.Random</c> shape, whose <c>Next(int)</c> is where
    /// remainder bias usually creeps in.
    /// </summary>
    [Fact]
    public void Neither_source_is_a_System_Random()
    {
        Assert.False(typeof(Random).IsAssignableFrom(typeof(CapRandom)));
        Assert.False(typeof(Random).IsAssignableFrom(typeof(InsecureDeterministicRandom)));
        Assert.DoesNotContain(typeof(CapRandom).GetMethods(), m => m.Name.StartsWith("Next", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every public way of getting hold of the secure generator asks for ambient authority.
    /// The type has no accessible constructor, so the only candidates are members that return
    /// it, and each of those must take the token.
    /// </summary>
    [Fact]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The subject of the test is the whole exported surface, including members " +
                        "nobody has written yet, which is exactly what cannot be named statically. " +
                        "The suite is not trimmed.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070:UnrecognizedReflectionPattern",
        Justification = "As above: the types being reflected over are whatever the assembly exports.")]
    public void Every_public_path_to_the_secure_generator_takes_the_token()
    {
        const BindingFlags Everything =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        Assert.All(
            typeof(CapRandom).GetConstructors(Everything),
            c => Assert.True(c.IsPrivate, $"{c} is reachable from outside the type."));

        var producers = typeof(CapRandom).Assembly.GetExportedTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => typeof(CapRandom).IsAssignableFrom(m.ReturnType)
                     || typeof(IRandomSource).IsAssignableFrom(m.ReturnType))
            .ToList();

        Assert.NotEmpty(producers);
        Assert.All(producers, m => Assert.Contains(
            m.GetParameters(),
            p => p.ParameterType == typeof(AmbientAuthority)));
    }
}
