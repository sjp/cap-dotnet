using System.Diagnostics.CodeAnalysis;
using Cap.Primitives;

namespace Cap.Std;

/// <summary>
/// The members of <see cref="DirEntry"/>, as an interface a test can substitute.
/// </summary>
/// <remarks>
/// <para>
/// What <see cref="IDir.EnumerateEntries"/> and <see cref="IDir.EnumerateEntriesAsync"/> yield.
/// <see cref="DirEntry"/> is the one type in this library that implements it, and a
/// <see cref="Dir"/> enumerated through <see cref="IDir"/> yields its own entries, boxed. A
/// stub can yield fabricated entries instead, whose opens go wherever the stub sends them.
/// </para>
/// <para>
/// The entry carries a name and never a path, as <see cref="DirEntry"/> does, and opens what
/// it names only through the handle it came from. Code that enumerates a <see cref="Dir"/>
/// directly still gets <see cref="DirEntry"/> itself, with no boxing.
/// </para>
/// <para>
/// <strong>Versioning.</strong> As for <see cref="IDir"/>: a member added to
/// <see cref="DirEntry"/> is added here in the same release, without a default
/// implementation.
/// </para>
/// </remarks>
public interface IDirEntry
{
    /// <inheritdoc cref="DirEntry.Name"/>
    string Name { get; }

    /// <inheritdoc cref="DirEntry.Type"/>
    CapFileType Type { get; }

    /// <inheritdoc cref="DirEntry.FileId"/>
    CapFileId FileId { get; }

    /// <inheritdoc cref="DirEntry.OpenDir()"/>
    IDir OpenDir();

    /// <inheritdoc cref="DirEntry.TryOpenDir(out Dir)"/>
    bool TryOpenDir([NotNullWhen(true)] out IDir? dir);

    /// <inheritdoc cref="DirEntry.OpenFile(FileMode, FileAccess, FileShare, FileOptions, long, bool)"/>
    ICapFile OpenFile(
        FileMode mode = FileMode.Open,
        FileAccess access = FileAccess.Read,
        FileShare share = FileShare.Read,
        FileOptions options = FileOptions.None,
        long preallocationSize = 0,
        bool append = false);

    /// <inheritdoc cref="DirEntry.TryOpenFile(out CapFile)"/>
    bool TryOpenFile([NotNullWhen(true)] out ICapFile? file);

    /// <inheritdoc cref="DirEntry.TryOpenFile(FileMode, FileAccess, FileShare, FileOptions, long, bool, out CapFile)"/>
    bool TryOpenFile(
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options,
        long preallocationSize,
        bool append,
        [NotNullWhen(true)] out ICapFile? file);

    /// <inheritdoc cref="DirEntry.GetMetadata()"/>
    CapMetadata GetMetadata();

    /// <inheritdoc cref="DirEntry.TryGetMetadata(out CapMetadata)"/>
    bool TryGetMetadata(out CapMetadata metadata);
}
