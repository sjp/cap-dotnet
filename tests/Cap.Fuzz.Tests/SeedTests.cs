using Cap.Escape.Tests;
using Cap.Fuzz.Targets;

namespace Cap.Fuzz.Tests;

/// <summary>
/// The fuzzer's starting inputs, derived from the escape corpus, each run through its target.
/// </summary>
/// <remarks>
/// A seed that faults is a fault the escape corpus already describes, found by a stricter
/// check, and it is found here on every change rather than overnight. Running the seeds also
/// shows that each case still converts: a case whose tree stopped fitting the simulation would
/// otherwise quietly stop seeding the fuzzer.
/// </remarks>
public sealed class SeedTests
{
    /// <summary>Every case's name, one row each.</summary>
    public static TheoryData<string> Cases => [.. EscapeCorpus.Cases.Select(entry => entry.Name)];

    /// <summary>A case's seeds pass every check their targets make.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void A_case_seeds_every_target_it_can_and_its_seeds_pass(string name)
    {
        EscapeCase entry = EscapeCorpus.Named(name);
        List<Seed> seeds = [.. EscapeCorpusSeeds.For(entry)];

        Assert.Contains(seeds, seed => seed.Target == CapPathTarget.Name);
        if (entry.Setup.Any(step => step.Target is not null))
        {
            Assert.Contains(seeds, seed => seed.Target == ReparseDataTarget.Name);
        }

        foreach (Seed seed in seeds)
        {
            FuzzTargets.All[seed.Target](seed.Data);
        }
    }

    /// <summary>
    /// The corpus's names for what lies inside and outside the sandbox are the simulation's
    /// too, so a seed that names them lands on the same things in the simulated tree.
    /// </summary>
    [Fact]
    public void The_simulated_tree_uses_the_corpus_names()
    {
        Assert.Equal(EscapeCorpus.OutsideDirectory, ResolutionScenario.OutsideName);
        Assert.Equal(EscapeCorpus.OutsideFile, ResolutionScenario.OutsideFileName);
        Assert.Equal(EscapeCorpus.OutsideSubdirectory, ResolutionScenario.OutsideSubdirectoryName);
        Assert.Equal(EscapeCorpus.PlainDirectory, ResolutionScenario.PlainDirectoryName);
        Assert.Equal(EscapeCorpus.PlainFile, ResolutionScenario.PlainFileName);
    }

    /// <summary>
    /// Writes every seed beneath the directory named by
    /// <see cref="PropertySettings.SeedDirectoryVariable"/>, one subdirectory per target, when
    /// one is named. This is how the scheduled fuzzing run gets its starting inputs.
    /// </summary>
    [Fact]
    public void Seeds_are_written_out_when_asked()
    {
        if (PropertySettings.SeedDirectory is not { } directory)
        {
            return;
        }

        foreach (Seed seed in EscapeCorpusSeeds.All())
        {
            string targetDirectory = Path.Join(directory, seed.Target);
            _ = Directory.CreateDirectory(targetDirectory);
            File.WriteAllBytes(Path.Join(targetDirectory, FileName(seed)), seed.Data);
        }

        foreach (string target in FuzzTargets.All.Keys)
        {
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Join(directory, target)));
        }
    }

    /// <summary>
    /// A name for a seed's file that every filesystem accepts. Case names hold quotes,
    /// separators and the characters Windows reserves, since those are what the cases are
    /// about; they are replaced here, and a digest of the contents keeps two names that come
    /// out the same apart.
    /// </summary>
    private static string FileName(Seed seed)
    {
        string readable = string.Concat(seed.Name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_'));
        string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(seed.Data))[..12];
        return $"{readable}-{digest}";
    }
}
