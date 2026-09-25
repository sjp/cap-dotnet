using System.Diagnostics.CodeAnalysis;
using Cap.Primitives;

namespace Cap.Std.Testing;

/// <summary>
/// Makes <see cref="IDirEntry"/> values for test doubles.
/// </summary>
/// <remarks>
/// <para>
/// <strong>For doubles only.</strong> A stub of <see cref="IDir.EnumerateEntries"/> has to
/// yield entries, and <see cref="DirEntry"/> has no public constructor, so this is where a
/// test gets one. Production code gets entries by enumerating a handle.
/// </para>
/// <para>
/// An entry made here behaves as <see cref="DirEntry"/> does: it holds a name and never a
/// path, and opening it or asking for its description is a call on the directory it names
/// as its owner, with its name. Given the stub that yields it as the owner, an entry's opens
/// go wherever the stub sends them, and a test can check them as calls on that stub.
/// </para>
/// </remarks>
public static class TestEntries
{
    /// <summary>Makes an entry named <paramref name="name"/> in <paramref name="owner"/>.</summary>
    /// <param name="name">The entry's name, a single component.</param>
    /// <param name="type">What the entry reports it holds.</param>
    /// <param name="fileId">The identity the entry reports; <see cref="TestFileIds"/> makes one.</param>
    /// <param name="owner">The directory that opens and describes the entry by name.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="owner"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is not one the enumeration defines.</exception>
    /// <remarks>
    /// The type and identity are reported as given, as a real entry reports what the
    /// directory said when it was read. They are not checked against what
    /// <paramref name="owner"/> describes, so a test can model an entry that changed after it
    /// was listed.
    /// </remarks>
    public static IDirEntry Create(string name, CapFileType type, CapFileId fileId, IDir owner)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(owner);
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "The file type is not one the enumeration defines.");
        }

        return new Entry(name, type, fileId, owner);
    }

    private sealed class Entry(string name, CapFileType type, CapFileId fileId, IDir owner) : IDirEntry
    {
        public string Name => name;

        public CapFileType Type => type;

        public CapFileId FileId => fileId;

        public IDir OpenDir() => owner.OpenDir(name);

        public bool TryOpenDir([NotNullWhen(true)] out IDir? dir) => owner.TryOpenDir(name, out dir);

        public ICapFile OpenFile(
            FileMode mode = FileMode.Open,
            FileAccess access = FileAccess.Read,
            FileShare share = FileShare.Read,
            FileOptions options = FileOptions.None,
            long preallocationSize = 0,
            bool append = false) =>
            owner.OpenFile(name, mode, access, share, options, preallocationSize, append);

        public bool TryOpenFile([NotNullWhen(true)] out ICapFile? file) => owner.TryOpenFile(name, out file);

        public bool TryOpenFile(
            FileMode mode,
            FileAccess access,
            FileShare share,
            FileOptions options,
            long preallocationSize,
            bool append,
            [NotNullWhen(true)] out ICapFile? file) =>
            owner.TryOpenFile(name, mode, access, share, options, preallocationSize, append, noFollow: false, out file);

        public CapMetadata GetMetadata() => owner.GetMetadata(name);

        public bool TryGetMetadata(out CapMetadata metadata) => owner.TryGetMetadata(name, out metadata);

        public override string ToString() => name;
    }
}
