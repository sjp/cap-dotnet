using System.Text;
using Cap.Primitives.Interop;

namespace Cap.Fuzz.Targets;

/// <summary>What one entry planted in the simulated tree is.</summary>
internal enum EntryKind : byte
{
    Directory,
    File,
    SymbolicLink,

    /// <summary>A directory on a volume of its own, as a mount point would be.</summary>
    MountPoint,

    /// <summary>A reparse point whose tag is not a link, which resolution must refuse.</summary>
    OpaqueReparsePoint,
}

/// <summary>Which resolution entry point the path is handed to.</summary>
internal enum ResolutionOperation : byte
{
    OpenDirectory,
    OpenFile,

    /// <summary>An open that creates the file when it is missing.</summary>
    CreateFile,

    /// <summary>Stopping one component short, as every operation that acts on a name does.</summary>
    ResolveParent,
}

/// <summary>One entry planted in the tree before the path is resolved.</summary>
/// <param name="Kind">What the entry is.</param>
/// <param name="Parent">
/// Which directory it goes in, as an index into the directories known so far: the sandbox is
/// 0, the directory beside it is 1, the one inside that is 2, the directory every sandbox
/// starts with is 3, and each directory or mount point planted after that takes the next
/// number. An index past the end wraps around.
/// </param>
/// <param name="Name">The entry's name in that directory.</param>
/// <param name="Target">What a symbolic link stores, and nothing for any other kind.</param>
internal sealed record TopologyEntry(EntryKind Kind, int Parent, string Name, string? Target = null);

/// <summary>
/// A simulated tree, a path into it, and what to do with the path.
/// </summary>
/// <remarks>
/// <para>
/// The same scenario comes from two places. The fuzzer produces bytes and
/// <see cref="Decode"/> turns them into one; the property tests build one directly, which
/// lets a failure shrink to a small tree and a short path that can be read. Running either
/// is <see cref="ResolutionWalkTarget.Check"/>.
/// </para>
/// <para>
/// Names and link targets are mostly drawn from a short list of words, so that the name a
/// path asks for is often one the tree has. A fuzzer choosing names a byte at a time would
/// almost never make the two meet, and a walk that never finds anything tests only the
/// refusal of a missing name. Any other text can still be spelled out in full, which is how
/// the escape corpus's own trees are carried over as starting inputs.
/// </para>
/// </remarks>
internal sealed record ResolutionScenario(
    IReadOnlyList<TopologyEntry> Entries,
    string Path,
    ResolutionOperation Operation,
    ConfinedResolveOptions Options)
{
    /// <summary>The directory that stands for the sandbox root.</summary>
    public const string SandboxName = "sandbox";

    /// <summary>The directory beside it, which nothing inside may reach.</summary>
    public const string OutsideName = "outside";

    /// <summary>A file in the directory beside the sandbox.</summary>
    public const string OutsideFileName = "outside-secret";

    /// <summary>A directory in the directory beside the sandbox.</summary>
    public const string OutsideSubdirectoryName = "outside-sub";

    /// <summary>A directory every sandbox starts with.</summary>
    public const string PlainDirectoryName = "plain";

    /// <summary>A file in <see cref="PlainDirectoryName"/>.</summary>
    public const string PlainFileName = "marker";

    /// <summary>
    /// The words names and targets are drawn from. The first three are what a separator, a
    /// <c>.</c> and a <c>..</c> leave between them.
    /// </summary>
    public static IReadOnlyList<string> Words => s_words;

    private static readonly string[] s_words =
    [
        "", ".", "..", "a", "b", "c",
        SandboxName, OutsideName, OutsideFileName, OutsideSubdirectoryName, PlainDirectoryName, PlainFileName,
    ];

    /// <summary>Most entries one input can plant.</summary>
    public const int MaxEntries = 24;

    /// <summary>Most words one path or link target can hold.</summary>
    public const int MaxWords = byte.MaxValue;

    /// <summary>How many directories there are before any entry is planted.</summary>
    public const int InitialDirectories = 4;

    /// <summary>
    /// Whether <paramref name="name"/> can be an entry in a directory. Empty, <c>.</c>,
    /// <c>..</c> and anything holding a separator cannot, and an entry with such a name is
    /// left out of the tree rather than stored under a name no lookup could ask for.
    /// </summary>
    public static bool IsEntryName(string name) =>
        name is not ("" or "." or "..") && !name.Contains('/', StringComparison.Ordinal);

    /// <summary>A byte that introduces a word spelled out rather than drawn from the list.</summary>
    private const byte SpelledOut = 0xFF;

    /// <summary>Reads a scenario from a fuzzer's bytes. Every input reads as one.</summary>
    /// <remarks>
    /// One byte chooses the operation in its low two bits and the resolution options in the
    /// next two. One byte gives the number of entries, and each entry is a byte for its kind,
    /// one for its directory, its name, and for a link its target. Last comes the path. A name
    /// is one word; a path or a target is a byte saying whether it starts at a root, a byte
    /// counting its words, and the words, which are joined with <c>/</c>.
    /// </remarks>
    public static ResolutionScenario Decode(ReadOnlySpan<byte> data)
    {
        FuzzInput input = new(data);
        byte settings = input.NextByte();
        ResolutionOperation operation = (ResolutionOperation)(settings & 3);
        ConfinedResolveOptions options = (ConfinedResolveOptions)((settings >> 2) & 3);

        int count = input.NextChoice(MaxEntries + 1);
        List<TopologyEntry> entries = new(count);
        for (int i = 0; i < count; i++)
        {
            EntryKind kind = (EntryKind)input.NextChoice(5);
            int parent = input.NextByte();
            string name = NextWord(ref input);
            string? target = kind == EntryKind.SymbolicLink ? NextText(ref input) : null;
            entries.Add(new TopologyEntry(kind, parent, name, target));
        }

        return new ResolutionScenario(entries, NextText(ref input), operation, options);
    }

    /// <summary>
    /// Writes the bytes that <see cref="Decode"/> reads back as this scenario, or nothing when
    /// the scenario is too large to be written.
    /// </summary>
    public byte[]? Encode()
    {
        if (Entries.Count > MaxEntries)
        {
            return null;
        }

        List<byte> data = [(byte)((byte)Operation | ((int)Options << 2)), (byte)Entries.Count];
        foreach (TopologyEntry entry in Entries)
        {
            if (entry.Parent > byte.MaxValue)
            {
                return null;
            }

            data.Add((byte)entry.Kind);
            data.Add((byte)entry.Parent);
            if (!TryWriteWord(data, entry.Name) ||
                (entry.Kind == EntryKind.SymbolicLink && !TryWriteText(data, entry.Target ?? string.Empty)))
            {
                return null;
            }
        }

        return TryWriteText(data, Path) ? [.. data] : null;
    }

    private static string NextWord(ref FuzzInput input)
    {
        byte choice = input.NextByte();
        return choice == SpelledOut
            ? input.NextUtf8(input.NextByte())
            : s_words[choice % s_words.Length];
    }

    private static string NextText(ref FuzzInput input)
    {
        bool rooted = (input.NextByte() & 1) != 0;
        int count = input.NextByte();
        StringBuilder text = new(rooted ? "/" : string.Empty);
        for (int i = 0; i < count; i++)
        {
            _ = text.Append(i == 0 ? string.Empty : "/").Append(NextWord(ref input));
        }

        return text.ToString();
    }

    private static bool TryWriteWord(List<byte> data, string word)
    {
        int index = Array.IndexOf(s_words, word);
        if (index >= 0)
        {
            data.Add((byte)index);
            return true;
        }

        byte[] spelled = Encoding.UTF8.GetBytes(word);
        if (spelled.Length > byte.MaxValue || Encoding.UTF8.GetString(spelled) != word)
        {
            return false;
        }

        data.Add(SpelledOut);
        data.Add((byte)spelled.Length);
        data.AddRange(spelled);
        return true;
    }

    private static bool TryWriteText(List<byte> data, string text)
    {
        bool rooted = text.StartsWith('/');
        string body = rooted ? text[1..] : text;

        // No words at all is written as none rather than as one empty word. Both read back as
        // the same text, and the first leaves the count free for real ones.
        string[] words = body.Length == 0 ? [] : body.Split('/');
        if (words.Length > MaxWords)
        {
            return false;
        }

        data.Add((byte)(rooted ? 1 : 0));
        data.Add((byte)words.Length);
        foreach (string word in words)
        {
            if (!TryWriteWord(data, word))
            {
                return false;
            }
        }

        return true;
    }
}
