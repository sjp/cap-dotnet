using System.Buffers.Binary;
using System.Numerics;

namespace Cap.Rand;

/// <summary>
/// A fixed sequence of bytes chosen by a seed, for tests and simulations. It is not random and
/// not secure.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The name is the warning.</strong> Anyone who knows or guesses the seed knows every
/// byte this will ever produce, and anyone who sees a few of its outputs can recover its state
/// and so every later byte. It must never produce anything that has to be unpredictable to
/// someone else. The type is not related to <see cref="CapRandom"/> and cannot be converted to
/// it, so code that asks for <see cref="CapRandom"/> cannot be handed one of these. It can
/// only go to code that accepts an <see cref="IRandomSource"/>, which says by doing so that it
/// does not need security.
/// </para>
/// <para>
/// <strong>No token required.</strong> Nothing here comes from outside the process. The
/// output is a pure function of the seed the caller supplied, so there is no ambient
/// authority to demand.
/// </para>
/// <para>
/// <strong>The sequence is part of the contract.</strong> For a given seed, the bytes this
/// produces will not change from one release to the next, so a seed written into a test or a
/// saved simulation stays valid after an upgrade. The bytes are one continuous stream: how the
/// calls to <see cref="Fill"/> divide it up does not change which bytes come out, so filling
/// three bytes and then five gives the same eight as filling eight at once. The integer
/// helpers in <see cref="RandomSourceExtensions"/> are specified in terms of that stream, and
/// are stable in the same way.
/// </para>
/// <para>
/// The generator is xoshiro256** by Blackman and Vigna. Its 256-bit state is set from the
/// seed by drawing four successive outputs of SplitMix64 started at the seed, as the
/// algorithm's authors recommend. Each step of xoshiro256** yields one 64-bit value, and the
/// stream is those values written out in little-endian order, one after the other. Both
/// algorithms are simple, public and have reference implementations, so the stream can be
/// reproduced outside .NET from this description alone.
/// </para>
/// <para>
/// Not safe to use from more than one thread at once. Each instance is a single position in a
/// single stream, and sharing one would make the order of draws, and so the sequence each
/// caller sees, depend on scheduling. Give each thread its own instance, with its own seed.
/// </para>
/// </remarks>
public sealed class InsecureDeterministicRandom : IRandomSource
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    /// <summary>
    /// The unused tail of the last 64-bit output, kept so that the stream does not depend on
    /// how calls divide it.
    /// </summary>
    private ulong _pending;

    /// <summary>How many bytes of <see cref="_pending"/>, from the low end, are still unused.</summary>
    private int _pendingCount;

    /// <summary>
    /// Starts the stream that <paramref name="seed"/> selects.
    /// </summary>
    /// <param name="seed">
    /// Any value, zero included. Two instances made with the same seed produce the same bytes.
    /// </param>
    public InsecureDeterministicRandom(ulong seed)
    {
        // SplitMix64 cannot produce four zeros in a row, since each output is a bijection of a
        // distinct counter value, so the all-zero state that would stall xoshiro is unreachable.
        ulong x = seed;
        _s0 = SplitMix64(ref x);
        _s1 = SplitMix64(ref x);
        _s2 = SplitMix64(ref x);
        _s3 = SplitMix64(ref x);
    }

    /// <inheritdoc/>
    public void Fill(Span<byte> destination)
    {
        while (_pendingCount > 0 && !destination.IsEmpty)
        {
            destination[0] = (byte)_pending;
            _pending >>= 8;
            _pendingCount--;
            destination = destination[1..];
        }

        while (destination.Length >= sizeof(ulong))
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination, Next());
            destination = destination[sizeof(ulong)..];
        }

        if (!destination.IsEmpty)
        {
            ulong value = Next();
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = (byte)value;
                value >>= 8;
            }

            _pending = value;
            _pendingCount = sizeof(ulong) - destination.Length;
        }
    }

    /// <summary>One step of xoshiro256**.</summary>
    private ulong Next()
    {
        ulong result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;

        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);

        return result;
    }

    /// <summary>One step of SplitMix64, advancing <paramref name="x"/>.</summary>
    private static ulong SplitMix64(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }
}
