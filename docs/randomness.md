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
- `GetDouble()` returns a `double` from 0 up to, but not including, 1.
- `Shuffle<T>(Span<T> values)` puts the elements into a random order in place, every order
  equally likely. An array passes as it is, and a `List<T>` through
  `CollectionsMarshal.AsSpan`.

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
5. **Fractions.** `GetDouble` reads eight bytes from the stream as a little-endian unsigned
   integer, keeps its top 53 bits and divides them by `2^53`. Nothing is redrawn.
6. **Shuffling.** `Shuffle` is the classic Fisher–Yates shuffle: for each index `i` from the
   last down to 1, it draws `j` with `GetInt32(i + 1)` as in step 4 and swaps the elements at
   `i` and `j`. Fewer than two elements draw nothing. This is not the order of draws that
   `System.Random.Shuffle` uses, so the same seed does not give the same permutation there.

The test suite pins reference outputs for this whole description, produced by a separate
implementation, so a change to any of these steps fails the build.

### What the seeded type is not

The name is meant as a warning. Anyone who knows the seed knows every byte, and anyone who
sees a few outputs can recover the state and predict everything after it. An instance is also
not safe to share between threads: it is one position in one stream, and sharing it would make
each caller's sequence depend on scheduling. Give each thread its own instance with its own
seed.

`CapRandom` can be used from any number of threads at once.

Nothing stops a seeded instance from reaching production. Its constructor takes no token,
since it takes nothing from outside the process, and anything that accepts an `IRandomSource`
will take one, so `new InsecureDeterministicRandom(42)` in `Program.cs` compiles without a
diagnostic, even in a `[CapabilityStrict]` assembly. A project that wants the type kept to its
tests bans the constructor in its own `CapBannedSymbols.txt`, where it is reported as
`CAP0008` (see [analyzers.md](analyzers.md)):

```
M:Cap.Rand.InsecureDeterministicRandom.#ctor;Seeded and predictable. Only test code should construct this; production takes CapRandom.System or an IRandomSource passed in.
```

Only construction is reported. Naming the type, or calling an instance that a test handed in,
is not. To apply the ban to production projects and not to tests, put the line in its own
file, such as `src/CapBannedSymbols.Seeded.txt`, and add it from a `Directory.Build.props` in
the directory that holds the production projects.
That file stops MSBuild's search for one further up, so it imports the parent explicitly:

```xml
<Project>
  <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
  <ItemGroup>
    <AdditionalFiles Include="$(MSBuildThisFileDirectory)CapBannedSymbols.Seeded.txt" />
  </ItemGroup>
</Project>
```

## What this gives up

**It is an audit, not a lock.** The operating system's generator can be reached from anywhere
in the process through `RandomNumberGenerator`, `Guid.NewGuid` and their relatives, and
nothing here changes that for code outside this library. The token makes the place where
entropy enters searchable, and it is recorded like every other acquisition (see
[ambient-authority.md](ambient-authority.md)). Keeping everything else from reaching around
it takes a build rule like the one below.

## The rule inside this repository

Every assembly under `src/` is built with ambient entropy banned, by rule `CAP0007` of the
analyzer that ships in the `Cap.Std` package (see [analyzers.md](analyzers.md)): the
`RandomNumberGenerator` type, `Guid.NewGuid`, `Guid.CreateVersion7`,
`Path.GetRandomFileName` and `System.Random`, together with every class derived from
either type (the obsolete `RNGCryptoServiceProvider` among them). A consuming project gets
the same rule by turning `CAP0007` on, or by marking its assembly
`[assembly: CapabilityStrict]`. Two places in this library are exempt.

- **`CapRandom`** is where entropy enters, behind the token.
- **The scratch-name generator in `Cap.Std`**, which picks names for `CapTempDir` and
  `CapTempFile` and is exposed as `CapTempFile.RandomName` (a name shaped like
  `Path.GetRandomFileName`'s, which `DirFileSystem`'s `Path.GetRandomFileName` returns), does
  not take a token. A scratch name grants nothing. The caller already
  holds the directory the object is created in, and the random bytes only choose its name
  inside that directory, never where it can reach. Requiring ambient authority there would
  log as an escape something that is not one.

Key generation is deliberately not on the list, although it draws on the operating system's
entropy too: `SymmetricAlgorithm.GenerateKey` and `GenerateIV`, `RSA.Create(int)`,
`ECDsa.Create(ECCurve)`, `ECDiffieHellman.Create(ECCurve)`, and the same work done lazily
when `Aes.Create()`'s `Key` is first read or `RSA.Create()`'s key is first used. There is no
capability-shaped replacement to point at. Drawing key bytes from a `CapRandom` and assigning
them to `Aes.Key` is worse practice than letting the algorithm generate its own key, and a
list could catch only the explicit calls, not the lazy ones. A project that wants key
generation audited adds the symbols to its own `CapBannedSymbols.txt`, where they are
reported as `CAP0008` (see [analyzers.md](analyzers.md)):

```
M:System.Security.Cryptography.SymmetricAlgorithm.GenerateKey;Generate keys in the key service.
M:System.Security.Cryptography.SymmetricAlgorithm.GenerateIV;Generate keys in the key service.
M:System.Security.Cryptography.RSA.Create(System.Int32);Generate keys in the key service.
M:System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve);Generate keys in the key service.
M:System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve);Generate keys in the key service.
```

A nonce for `AesGcm` or `ChaCha20Poly1305` is the caller's to supply, and can come from a
`CapRandom`.
