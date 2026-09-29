using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Cap.Primitives;
using Cap.Primitives.Interop;

namespace Cap.Std.Testing;

/// <summary>
/// A copy of everything in an <see cref="InMemoryFileSystem"/> at one instant, from
/// <see cref="InMemoryFileSystem.Snapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by build path, spelled as each name was created and joined with <c>/</c>, with no
/// leading <c>/</c>: <c>config/app.json</c>. The top of the tree itself has no entry. A key is
/// found under any spelling the filesystem would take for it, so on a filesystem that ignores
/// case <c>Config/App.json</c> finds the same entry. The keys are enumerated in ordinal order,
/// so a directory comes before what is in it.
/// </para>
/// <para>
/// An object with several names appears once under each, with the same
/// <see cref="CapMetadata.FileId"/>.
/// </para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1710:Identifiers should have correct suffix",
    Justification = "Named for what it is, a snapshot; that it can be read as a dictionary is secondary.")]
public sealed class InMemorySnapshot : IReadOnlyDictionary<string, InMemoryEntry>
{
    private readonly Dictionary<string, InMemoryEntry> _entries;
    private readonly string[] _paths;

    internal InMemorySnapshot(List<KeyValuePair<string, InMemoryEntry>> entries, StringComparer names)
    {
        _entries = new Dictionary<string, InMemoryEntry>(entries, names);
        _paths = [.. _entries.Keys];
        Array.Sort(_paths, StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public int Count => _paths.Length;

    /// <inheritdoc/>
    public IEnumerable<string> Keys => _paths;

    /// <inheritdoc/>
    public IEnumerable<InMemoryEntry> Values => _paths.Select(path => _entries[path]);

    /// <summary>What was at <paramref name="path"/>.</summary>
    /// <param name="path">A build path, with or without a leading <c>/</c>.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="KeyNotFoundException">Nothing was there.</exception>
    public InMemoryEntry this[string path] =>
        TryGetValue(path, out InMemoryEntry? entry)
            ? entry
            : throw new KeyNotFoundException($"Nothing was at '{path}' when the snapshot was taken.");

    /// <inheritdoc/>
    public bool ContainsKey(string key) => TryGetValue(key, out _);

    /// <inheritdoc/>
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out InMemoryEntry value)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _entries.TryGetValue(key.Trim('/'), out value);
    }

    /// <summary>What changed between this snapshot and a <paramref name="later"/> one.</summary>
    /// <param name="later">A snapshot taken after this one, of the same filesystem.</param>
    /// <returns>The paths added, removed and changed, each in ordinal order.</returns>
    /// <remarks>
    /// <para>
    /// A path is changed when the two entries differ in anything but the last-access time:
    /// the kind, the identity, the contents or link target, the length, the permissions, the
    /// link count, or the write, change or creation time. The last-access time is left out so
    /// that reading something never counts as changing it.
    /// </para>
    /// <para>
    /// A rename is a removal of the old path and an addition of the new one; the entry under
    /// the new path keeps the identity the old one had. Adding or removing a name in a
    /// directory stamps the directory's times, so under a clock that moves the directory is
    /// reported changed too; under the default stopped clock only a Unix directory's link
    /// count, which a subdirectory changes, can.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="later"/> is null.</exception>
    public InMemorySnapshotDiff Diff(InMemorySnapshot later)
    {
        ArgumentNullException.ThrowIfNull(later);

        List<string> added = [];
        List<string> removed = [];
        List<string> changed = [];
        foreach (string path in _paths)
        {
            if (!later._entries.TryGetValue(path, out InMemoryEntry? after))
            {
                removed.Add(path);
            }
            else if (!_entries[path].SameAs(after))
            {
                changed.Add(path);
            }
        }

        foreach (string path in later._paths)
        {
            if (!_entries.ContainsKey(path))
            {
                added.Add(path);
            }
        }

        return new InMemorySnapshotDiff(added, removed, changed);
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, InMemoryEntry>> GetEnumerator()
    {
        foreach (string path in _paths)
        {
            yield return new(path, _entries[path]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>One name in an <see cref="InMemorySnapshot"/> and what it named.</summary>
public sealed class InMemoryEntry
{
    private readonly CapNodeStat _stat;
    private readonly byte[] _contents;

    internal InMemoryEntry(in CapNodeStat stat, byte[] contents, string? linkTarget)
    {
        _stat = stat;
        _contents = contents;
        LinkTarget = linkTarget;
    }

    /// <summary>What the object was.</summary>
    public CapFileType Type => _stat.Type;

    /// <summary>Its description, as a handle would have reported it.</summary>
    public CapMetadata Metadata => new(_stat);

    /// <summary>A file's contents; empty for anything else.</summary>
    public ReadOnlyMemory<byte> Contents => _contents;

    /// <summary>A symbolic link's target, exactly as stored; null for anything else.</summary>
    public string? LinkTarget { get; }

    /// <summary>Whether two entries agree in everything but the last-access time.</summary>
    internal bool SameAs(InMemoryEntry other) =>
        _stat.Type == other._stat.Type &&
        _stat.VolumeId == other._stat.VolumeId &&
        _stat.NodeId == other._stat.NodeId &&
        _stat.Length == other._stat.Length &&
        _stat.LastWriteTime == other._stat.LastWriteTime &&
        _stat.CreationTime == other._stat.CreationTime &&
        _stat.ChangeTime == other._stat.ChangeTime &&
        _stat.LinkCount == other._stat.LinkCount &&
        _stat.UnixMode == other._stat.UnixMode &&
        _stat.WindowsAttributes == other._stat.WindowsAttributes &&
        string.Equals(LinkTarget, other.LinkTarget, StringComparison.Ordinal) &&
        _contents.AsSpan().SequenceEqual(other._contents);
}

/// <summary>What changed between two snapshots, from <see cref="InMemorySnapshot.Diff"/>.</summary>
public sealed class InMemorySnapshotDiff
{
    internal InMemorySnapshotDiff(List<string> added, List<string> removed, List<string> changed)
    {
        Added = added;
        Removed = removed;
        Changed = changed;
    }

    /// <summary>Paths in the later snapshot and not the earlier one, in ordinal order.</summary>
    public IReadOnlyList<string> Added { get; }

    /// <summary>Paths in the earlier snapshot and not the later one, in ordinal order.</summary>
    public IReadOnlyList<string> Removed { get; }

    /// <summary>Paths in both whose entries differ, in ordinal order.</summary>
    public IReadOnlyList<string> Changed { get; }

    /// <summary>True when nothing was added, removed or changed.</summary>
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;

    /// <summary>Lists the paths, one kind of change per line, for an assertion message.</summary>
    /// <returns>The description.</returns>
    public override string ToString() =>
        IsEmpty
            ? "No changes."
            : $"Added: [{string.Join(", ", Added)}]\n" +
              $"Removed: [{string.Join(", ", Removed)}]\n" +
              $"Changed: [{string.Join(", ", Changed)}]";
}
