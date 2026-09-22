# Randomness as a capability

`Cap.Rand` hands out the operating system's entropy the way everything else in this library
hands out authority from outside the process: only against an `AmbientAuthority` token, so
that the place it was taken can be found.

```csharp
using Cap.Primitives;
using Cap.Rand;

CapRandom random = CapRandom.System(AmbientAuthority.Acquire());

Span<byte> key = stackalloc byte[32];
random.Fill(key);
```

The library is deliberately thin. Every byte `CapRandom` produces comes from .NET's
`RandomNumberGenerator`, which is already correct. The only thing added is that the generator
has to be handed to you.

## Three types

| Type | What it is | Needs a token |
|---|---|---|
| `IRandomSource` | One method, `Fill(Span<byte>)`. Accepted by code that does not need security. | — |
| `CapRandom` | The operating system's cryptographic generator. Sealed; the only way to get one is `CapRandom.System`. | yes |
| `InsecureDeterministicRandom` | A fixed stream of bytes chosen by a seed, for tests and simulations. | no |

Both concrete types implement `IRandomSource`, and nothing else connects them.
`InsecureDeterministicRandom` does not derive from `CapRandom`, cannot be cast to it and
has no conversion to it. The seeded type does not need a token because it takes nothing from
outside the process: its output depends only on the seed the caller passed in.

## Choosing what to accept

The parameter type is how a component states what it needs:

- **Take `CapRandom`** when the output must be unpredictable to someone else: keys, session
  tokens, nonces, names another account must not guess. Nothing predictable can be passed in,
  whether by mistake or on purpose.
- **Take `IRandomSource`** when it does not matter: jittered retry delays, sampling,
  shuffling a list for display, picking a load-balancing target. A test can then pass a seed
  and get the same run every time.

```csharp
sealed class Retry(IRandomSource random)
{
    public TimeSpan NextDelay(int attempt) =>
        TimeSpan.FromMilliseconds(random.GetInt32(100, 200) << attempt);
}

// Production
var retry = new Retry(CapRandom.System(AmbientAuthority.Acquire()));

// Test
var retry = new Retry(new InsecureDeterministicRandom(seed: 42));
```

So a secure generator can go anywhere a source is accepted, but a seeded sequence can never
go where a secure generator is required. That one-way rule is the reason the interface
exists at all.

## The helpers

`Fill` is the primitive. On top of it, extension methods on `IRandomSource` give:

- `GetBytes(int count)` returns a new array.
- `GetInt32(int toExclusive)` and `GetInt32(int fromInclusive, int toExclusive)` return an
  `int`.
- `GetInt64(long fromInclusive, long toExclusive)` returns a `long`.

There is no `System.Random`-shaped surface, and in particular no `Next(int)`. The usual
misuse of that shape is to take a large random integer modulo `n`, which makes small values
slightly more likely than large ones whenever `n` does not divide the range evenly. The
helpers here never reduce a draw by remainder. Each draw is masked to the smallest power of
two covering the range, and a result that falls outside the range is thrown away and drawn
again. The results are exactly uniform, and fewer than half of all draws are discarded even
in the worst case.

## The seeded stream will not change

For a given seed, `InsecureDeterministicRandom` produces the same bytes in every release, so
a seed written into a test or a saved simulation keeps its meaning after an upgrade. The
stream is fully specified here, and can be reproduced outside .NET:

1. **Seeding.** Run SplitMix64 starting from the seed and take its first four outputs as the
   256-bit state of xoshiro256** (Blackman and Vigna), in the order `s0, s1, s2, s3`.
2. **Output.** Each step of xoshiro256** yields one 64-bit value. The byte stream is those
   values written out in little-endian order, one after another.
3. **Continuity.** The stream does not depend on how `Fill` calls divide it. Filling three
   bytes and then five gives the same eight bytes as filling eight at once. A partly used
   64-bit value is kept for the next call, not thrown away.
4. **Integers.** For a range of width `w`, if `w` is 1 the only value is returned and nothing
   is read. Otherwise each attempt reads four bytes (`GetInt32`) or eight (`GetInt64`) from
   the stream as a little-endian unsigned integer and masks it to its low `k` bits, where
   `2^k` is the smallest power of two not below `w`. The first attempt below `w` is used, and
   the result is the lower bound plus that value.

The test suite pins reference outputs for this whole description, produced by a separate
implementation, so a change to any of these steps fails the build.

### What the seeded type is not

The name is meant as a warning. Anyone who knows the seed knows every byte, and anyone who
sees a few outputs can recover the state and predict everything after it. An instance is also
not safe to share between threads: it is one position in one stream, and sharing it would make
each caller's sequence depend on scheduling. Give each thread its own instance with its own
seed.

`CapRandom` can be used from any number of threads at once.

## What this gives up

**It is an audit, not a lock.** The operating system's generator can be reached from anywhere
in the process through `RandomNumberGenerator`, `Guid.NewGuid` and their relatives, and
nothing here changes that for code outside this library. The token makes the place where
entropy enters searchable, and it is recorded like every other acquisition (see
[ambient-authority.md](ambient-authority.md)). Keeping everything else from reaching around
it takes a build rule like the one below.

## The rule inside this repository

Every assembly under `src/` is built with ambient entropy banned
(`build/BannedSymbols.Common.txt`): the `RandomNumberGenerator` type, `Guid.NewGuid`,
`Guid.CreateVersion7` and `Path.GetRandomFileName`, alongside the existing ban on
`System.Random`. Two places are exempt.

- **`CapRandom`** is where entropy enters, behind the token.
- **The scratch-name generator in `Cap.Std`**, which picks names for `CapTempDir` and
  `CapTempFile`, does not take a token. A scratch name grants nothing. The caller already
  holds the directory the object is created in, and the random bytes only choose its name
  inside that directory, never where it can reach. Requiring ambient authority there would
  log as an escape something that is not one.
