using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Cap.Primitives.Interop;

namespace Cap.Std.Testing;

/// <summary>
/// The names in one directory held in memory, and what each refers to.
/// </summary>
/// <remarks>
/// <para>
/// A dictionary that also keeps count of the subdirectories it holds, so that a directory's
/// link count, which on Unix counts them, is reported without going through every entry. The
/// count is kept by every change made here, whoever makes it, and reads each entry's kind as
/// it was when the entry was added: a node whose kind changes while it has a name leaves the
/// count wrong.
/// </para>
/// <para>
/// A name can be looked up as a span as well as a string, so that a lookup by a component cut
/// from a longer path costs nothing. That needs a comparer that can compare a span with a
/// string, as <see cref="StringComparer.Ordinal"/> and
/// <see cref="StringComparer.OrdinalIgnoreCase"/> can.
/// </para>
/// </remarks>
internal sealed class MemoryEntries : IReadOnlyDictionary<string, MemoryNode>
{
    private readonly Dictionary<string, MemoryNode> _entries;
    private readonly Dictionary<string, MemoryNode>.AlternateLookup<ReadOnlySpan<char>> _bySpan;

    /// <summary>Creates an empty set of entries whose names compare as <paramref name="names"/> does.</summary>
    public MemoryEntries(StringComparer names)
    {
        _entries = new Dictionary<string, MemoryNode>(names);
        _bySpan = _entries.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>How many of the entries are directories.</summary>
    public int SubdirectoryCount { get; private set; }

    /// <inheritdoc/>
    public int Count => _entries.Count;

    /// <inheritdoc cref="Dictionary{TKey, TValue}.Keys"/>
    public Dictionary<string, MemoryNode>.KeyCollection Keys => _entries.Keys;

    /// <inheritdoc cref="Dictionary{TKey, TValue}.Values"/>
    public Dictionary<string, MemoryNode>.ValueCollection Values => _entries.Values;

    IEnumerable<string> IReadOnlyDictionary<string, MemoryNode>.Keys => _entries.Keys;

    IEnumerable<MemoryNode> IReadOnlyDictionary<string, MemoryNode>.Values => _entries.Values;

    /// <summary>
    /// What <paramref name="name"/> refers to; setting it adds the name, or points an existing
    /// one at something else.
    /// </summary>
    public MemoryNode this[string name]
    {
        get => _entries[name];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_entries.TryGetValue(name, out MemoryNode? replaced))
            {
                Tally(replaced, -1);
            }

            _entries[name] = value;
            Tally(value, +1);
        }
    }

    /// <inheritdoc/>
    public bool ContainsKey(string key) => _entries.ContainsKey(key);

    /// <inheritdoc/>
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out MemoryNode value) =>
        _entries.TryGetValue(key, out value);

    /// <summary>What <paramref name="name"/> refers to, looked up without making a string of it.</summary>
    public bool TryGetValue(ReadOnlySpan<char> name, [MaybeNullWhen(false)] out MemoryNode value) =>
        _bySpan.TryGetValue(name, out value);

    /// <summary>What <paramref name="name"/> refers to, or null when it is not here.</summary>
    public MemoryNode? GetValueOrDefault(string name) => _entries.GetValueOrDefault(name);

    /// <summary>
    /// What <paramref name="name"/> refers to, or null when it is not here, looked up without
    /// making a string of it.
    /// </summary>
    public MemoryNode? GetValueOrDefault(ReadOnlySpan<char> name) =>
        _bySpan.TryGetValue(name, out MemoryNode? value) ? value : null;

    /// <summary>Removes a name.</summary>
    /// <returns>Whether the name was here.</returns>
    public bool Remove(string name)
    {
        if (!_entries.Remove(name, out MemoryNode? removed))
        {
            return false;
        }

        Tally(removed, -1);
        return true;
    }

    /// <inheritdoc cref="Dictionary{TKey, TValue}.GetEnumerator"/>
    public Dictionary<string, MemoryNode>.Enumerator GetEnumerator() => _entries.GetEnumerator();

    IEnumerator<KeyValuePair<string, MemoryNode>> IEnumerable<KeyValuePair<string, MemoryNode>>.GetEnumerator() =>
        _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _entries.GetEnumerator();

    private void Tally(MemoryNode node, int change)
    {
        if (node.Type == CapNodeType.Directory)
        {
            SubdirectoryCount += change;
        }
    }
}
