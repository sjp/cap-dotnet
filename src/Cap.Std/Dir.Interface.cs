using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Cap.Primitives;

namespace Cap.Std;

/// <summary>
/// The members of <see cref="IDir"/> whose shape differs from the handle's own.
/// </summary>
/// <remarks>
/// Each one forwards to the public member of the same name, which keeps its concrete return
/// type so that code holding a <see cref="Dir"/> is handed a <see cref="Dir"/> back. C# has no
/// covariant returns for an interface's members, so the two cannot be one method. Members
/// whose signatures already match the interface's implement it directly and do not appear
/// here.
/// </remarks>
public sealed partial class Dir
{
    /// <inheritdoc/>
    IDir IDir.OpenDir(string path, bool noFollow) => OpenDir(path, noFollow);

    /// <inheritdoc/>
    bool IDir.TryOpenDir(string path, [NotNullWhen(true)] out IDir? dir) =>
        Widen(TryOpenDir(path, out Dir? concrete), concrete, out dir);

    /// <inheritdoc/>
    bool IDir.TryOpenDir(string path, bool noFollow, [NotNullWhen(true)] out IDir? dir) =>
        Widen(TryOpenDir(path, noFollow, out Dir? concrete), concrete, out dir);

    /// <inheritdoc/>
    IDir IDir.CreateDir(string path) => CreateDir(path);

    /// <inheritdoc/>
    bool IDir.TryCreateDir(string path, [NotNullWhen(true)] out IDir? dir) =>
        Widen(TryCreateDir(path, out Dir? concrete), concrete, out dir);

    /// <inheritdoc/>
    IDir IDir.OpenOrCreateDir(string path) => OpenOrCreateDir(path);

    /// <inheritdoc/>
    bool IDir.TryOpenOrCreateDir(string path, [NotNullWhen(true)] out IDir? dir) =>
        Widen(TryOpenOrCreateDir(path, out Dir? concrete), concrete, out dir);

    /// <inheritdoc/>
    ICapFile IDir.OpenFile(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options,
        long preallocationSize,
        bool append,
        bool noFollow) =>
        OpenFile(path, mode, access, share, options, preallocationSize, append, noFollow);

    /// <inheritdoc/>
    bool IDir.TryOpenFile(string path, [NotNullWhen(true)] out ICapFile? file) =>
        Widen(TryOpenFile(path, out CapFile? concrete), concrete, out file);

    /// <inheritdoc/>
    bool IDir.TryOpenFile(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options,
        long preallocationSize,
        bool append,
        bool noFollow,
        [NotNullWhen(true)] out ICapFile? file) =>
        Widen(
            TryOpenFile(path, mode, access, share, options, preallocationSize, append, noFollow, out CapFile? concrete),
            concrete,
            out file);

    /// <inheritdoc/>
    ICapOpened IDir.OpenAny(string path, FileShare share, FileOptions options, bool noFollow) =>
        OpenAny(path, share, options, noFollow);

    /// <inheritdoc/>
    bool IDir.TryOpenAny(string path, [NotNullWhen(true)] out ICapOpened? opened) =>
        Widen(TryOpenAny(path, out CapOpened? concrete), concrete, out opened);

    /// <inheritdoc/>
    bool IDir.TryOpenAny(string path, bool noFollow, [NotNullWhen(true)] out ICapOpened? opened) =>
        Widen(TryOpenAny(path, noFollow, out CapOpened? concrete), concrete, out opened);

    /// <inheritdoc/>
    ICapFile IDir.CreateFile(string path) => CreateFile(path);

    /// <inheritdoc/>
    ICapFile IDir.CreateNewFile(string path) => CreateNewFile(path);

    /// <inheritdoc/>
    bool IDir.TryCreateFile(string path, [NotNullWhen(true)] out ICapFile? file) =>
        Widen(TryCreateFile(path, out CapFile? concrete), concrete, out file);

    /// <inheritdoc/>
    bool IDir.TryCreateNewFile(string path, [NotNullWhen(true)] out ICapFile? file) =>
        Widen(TryCreateNewFile(path, out CapFile? concrete), concrete, out file);

    /// <inheritdoc/>
    IEnumerable<IDirEntry> IDir.EnumerateEntries()
    {
        // Called here rather than inside the iterator, so that a disposed handle is reported
        // when the enumeration is asked for, as it is through the handle's own member.
        IEnumerable<DirEntry> entries = EnumerateEntries();
        return Boxed(entries);

        static IEnumerable<IDirEntry> Boxed(IEnumerable<DirEntry> entries)
        {
            foreach (DirEntry entry in entries)
            {
                yield return entry;
            }
        }
    }

    /// <inheritdoc/>
    IAsyncEnumerable<IDirEntry> IDir.EnumerateEntriesAsync(CancellationToken cancellationToken)
    {
        IAsyncEnumerable<DirEntry> entries = EnumerateEntriesAsync(cancellationToken);
        return Boxed(entries, CancellationToken.None);

        // A token given to WithCancellation on the result arrives here, and is passed on to
        // the handle's own enumeration, which combines it with the one it was created with.
        static async IAsyncEnumerable<IDirEntry> Boxed(
            IAsyncEnumerable<DirEntry> entries,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (DirEntry entry in entries.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return entry;
            }
        }
    }

    /// <inheritdoc/>
    IDir IDir.Clone() => Clone();

    /// <inheritdoc/>
    bool IDir.TryClone([NotNullWhen(true)] out IDir? clone) =>
        Widen(TryClone(out Dir? concrete), concrete, out clone);

    /// <inheritdoc/>
    IDir IDir.Restrict(SymlinkPolicy policy) => Restrict(policy);

    /// <inheritdoc/>
    bool IDir.TryRestrict(SymlinkPolicy policy, [NotNullWhen(true)] out IDir? restricted) =>
        Widen(TryRestrict(policy, out Dir? concrete), concrete, out restricted);

    /// <summary>
    /// Hands a <c>Try</c> member's concrete result out as the interface it implements.
    /// </summary>
    private static bool Widen<TConcrete, TInterface>(
        bool succeeded,
        TConcrete? concrete,
        [NotNullWhen(true)] out TInterface? widened)
        where TConcrete : class, TInterface
        where TInterface : class
    {
        widened = concrete;
        return succeeded;
    }
}
