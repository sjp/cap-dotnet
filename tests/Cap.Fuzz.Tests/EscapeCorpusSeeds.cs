using System.Buffers.Binary;
using Cap.Escape.Tests;
using Cap.Fuzz.Targets;
using Cap.Primitives;
using Cap.Primitives.Interop;
using Cap.Primitives.Interop.Windows;

namespace Cap.Fuzz.Tests;

/// <summary>One starting input for the fuzzer.</summary>
/// <param name="Target">The target it is for.</param>
/// <param name="Name">A name for it that is unique within the target and says where it came from.</param>
/// <param name="Data">The input.</param>
internal sealed record Seed(string Target, string Name, byte[] Data);

/// <summary>
/// The escape corpus, re-expressed as starting inputs for each fuzz target.
/// </summary>
/// <remarks>
/// <para>
/// A fuzzer started from nothing spends its first hours discovering that a path has
/// separators in it. Started from the attacks already known to matter, it begins at the
/// interesting places and mutates outwards. The corpus is where those attacks are written
/// down, so the seeds are derived from it rather than kept as a second list that would drift.
/// </para>
/// <para>
/// The paths go to the parser under both syntaxes. The trees go to the walk, over the
/// simulated filesystem, through each of its operations. The link targets go to the reparse
/// reader, written out as the structure that would store them.
/// </para>
/// </remarks>
internal static class EscapeCorpusSeeds
{
    /// <summary>The seeds for one case.</summary>
    public static IEnumerable<Seed> For(EscapeCase entry)
    {
        foreach (CapPathSyntax syntax in new[] { CapPathSyntax.Unix, CapPathSyntax.Windows })
        {
            yield return new Seed(
                CapPathTarget.Name,
                $"{entry.Name}-{syntax.ToString().ToLowerInvariant()}",
                CapPathTarget.Encode(WithOutside(entry.Path, syntax), syntax, ParentLinkPolicy.Preserve));
        }

        foreach ((SetupStep step, int index) in entry.Setup.Select((step, index) => (step, index)))
        {
            if (step.Target is null)
            {
                continue;
            }

            byte[]? link = step.Kind == SetupKind.Junction
                ? Junction(WithOutside(step.Target, CapPathSyntax.Windows))
                : SymbolicLink(WithOutside(step.Target, CapPathSyntax.Windows));
            if (link is not null)
            {
                yield return new Seed(ReparseDataTarget.Name, $"{entry.Name}-{index}", link);
            }
        }

        if (Topology(entry) is { } entries)
        {
            foreach (ResolutionOperation operation in Enum.GetValues<ResolutionOperation>())
            {
                ResolutionScenario scenario = new(
                    entries, WithOutside(entry.Path, CapPathSyntax.Unix), operation, ConfinedResolveOptions.None);
                if (scenario.Encode() is { } data)
                {
                    yield return new Seed(ResolutionWalkTarget.Name, $"{entry.Name}-{operation}", data);
                }
            }
        }
    }

    /// <summary>Every seed for every case.</summary>
    public static IEnumerable<Seed> All() => EscapeCorpus.Cases.SelectMany(For);

    /// <summary>
    /// Fills in the corpus's placeholder for the directory outside the sandbox. In the
    /// simulated tree that directory sits beside the sandbox at the root, so under POSIX
    /// rules the placeholder is simply its absolute path.
    /// </summary>
    private static string WithOutside(string text, CapPathSyntax syntax) =>
        text.Replace(
            "{outside}",
            syntax == CapPathSyntax.Windows ? @"C:\" + ResolutionScenario.OutsideName : "/" + ResolutionScenario.OutsideName,
            StringComparison.Ordinal);

    /// <summary>
    /// The case's tree as entries for the simulated one, or nothing when it holds something
    /// the simulation does not model.
    /// </summary>
    /// <remarks>
    /// Directories on the way to each entry are planted first, the way the corpus's own
    /// arena creates them. A junction has no counterpart in the simulation, which models the
    /// portable walk, so a case that needs one is left to the parser and reparse seeds.
    /// </remarks>
    private static List<TopologyEntry>? Topology(EscapeCase entry)
    {
        Dictionary<string, int> directories = new(StringComparer.Ordinal)
        {
            [string.Empty] = 0,
            [ResolutionScenario.PlainDirectoryName] = 3,
        };
        int nextDirectory = ResolutionScenario.InitialDirectories;
        List<TopologyEntry> entries = [];

        int ParentOf(string path)
        {
            string[] parts = path.Split('/');
            string parent = string.Empty;
            foreach (string part in parts[..^1])
            {
                string next = parent.Length == 0 ? part : $"{parent}/{part}";
                if (!directories.ContainsKey(next))
                {
                    entries.Add(new TopologyEntry(EntryKind.Directory, directories[parent], part));
                    directories[next] = ResolutionScenario.IsEntryName(part) ? nextDirectory++ : directories[parent];
                }

                parent = next;
            }

            return directories[parent];
        }

        foreach (SetupStep step in entry.Setup)
        {
            if (step.Kind == SetupKind.Junction)
            {
                return null;
            }

            int parent = ParentOf(step.Path);
            string name = step.Path.Split('/')[^1];
            switch (step.Kind)
            {
                case SetupKind.Directory:
                    entries.Add(new TopologyEntry(EntryKind.Directory, parent, name));
                    if (ResolutionScenario.IsEntryName(name))
                    {
                        directories[step.Path] = nextDirectory++;
                    }

                    break;

                case SetupKind.File:
                    entries.Add(new TopologyEntry(EntryKind.File, parent, name));
                    break;

                default:
                    entries.Add(new TopologyEntry(
                        EntryKind.SymbolicLink, parent, name, WithOutside(step.Target ?? string.Empty, CapPathSyntax.Unix)));
                    break;
            }
        }

        return entries.Count <= ResolutionScenario.MaxEntries ? entries : null;
    }

    private static byte[]? SymbolicLink(string target)
    {
        bool rooted = CapPath.IsRooted(target, CapPathSyntax.Windows);
        byte[] buffer = new byte[ReparseData.SymbolicLinkSize(target, rooted)];
        return ReparseData.TryBuildSymbolicLink(target, rooted, buffer, out _) ? buffer : null;
    }

    /// <summary>
    /// A junction's stored structure: no flags word, and a target the filesystem always
    /// resolves from a volume root.
    /// </summary>
    private static byte[]? Junction(string target)
    {
        string substitute = ReparseData.ObjectManagerPrefix + target;
        int substituteBytes = substitute.Length * sizeof(char);
        int printBytes = target.Length * sizeof(char);
        int dataLength = 8 + substituteBytes + sizeof(char) + printBytes + sizeof(char);
        if (8 + dataLength > ReparseData.MaximumBufferSize)
        {
            return null;
        }

        byte[] buffer = new byte[8 + dataLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, ReparseTags.MountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)dataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), (ushort)substituteBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), (ushort)(substituteBytes + sizeof(char)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), (ushort)printBytes);
        _ = System.Text.Encoding.Unicode.GetBytes(substitute, buffer.AsSpan(16));
        _ = System.Text.Encoding.Unicode.GetBytes(target, buffer.AsSpan(16 + substituteBytes + sizeof(char)));
        return buffer;
    }
}
