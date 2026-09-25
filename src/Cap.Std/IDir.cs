using System.Diagnostics.CodeAnalysis;
using Cap.Primitives;

namespace Cap.Std;

/// <summary>
/// The members of <see cref="Dir"/>, as an interface a test can substitute.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Dir"/> is the one type in this library that implements it. It exists so that a
/// component can be written against the shape of a directory handle and a test can hand it a
/// stub from a mocking library: one that fails the third write, or records that a file was
/// read exactly once. For most tests the better double is a real <see cref="Dir"/> on a
/// filesystem held in memory, from the <c>Cap.Std.Testing</c> package, because it runs the
/// same resolution the disk does and a stub runs none.
/// </para>
/// <para>
/// <strong>Taking this interface gives up the guarantee's proof.</strong> Everything
/// <see cref="Dir"/> promises about containment is a property of that type's implementation,
/// not of this interface. A component that takes an <see cref="IDir"/> is confined to a subtree
/// only if the code that built it handed it a <see cref="Dir"/>, or something that forwards
/// to one; any other implementation can resolve a name however it likes, the whole host
/// filesystem included. The interface is a seam for tests, not a second implementation of the
/// guarantee. Code that must be certain what it holds — because it is the check that keeps
/// untrusted input inside a tree, say — takes <see cref="Dir"/>, which cannot be derived from
/// or implemented by anything else.
/// </para>
/// <para>
/// <strong>Two handles never mix across implementations.</strong> A move or a second name
/// joins an entry beneath one handle to a name beneath another, and a <see cref="Dir"/> can
/// only do that with another <see cref="Dir"/> on the same filesystem. Given anything else as
/// the destination, <see cref="Dir"/> refuses the operation as a move across devices before
/// either name is resolved, so a stub can never be the other end of a real rename.
/// </para>
/// <para>
/// The members that hand out the operating-system handle, <see cref="Dir.UnsafeGetHandle"/>
/// and <see cref="CapFile.UnsafeGetHandle"/>, are not part of this interface or of
/// <see cref="ICapFile"/>. A stub has no handle to give, and code that needs one needs a real
/// <see cref="Dir"/> anyway.
/// </para>
/// <para>
/// <strong>Versioning.</strong> This interface follows <see cref="Dir"/>: a member added to
/// the handle is added here in the same release, without a default implementation. That is
/// no burden on a stub made by a mocking library, which implements whatever the interface
/// has at the time, and it keeps the interface a faithful description of the handle rather
/// than one that drifts. It does mean that a hand-written implementation of this interface
/// outside this library can stop compiling on an upgrade, including a minor one; such an
/// implementation should expect to change with every release it is built against.
/// </para>
/// <para>
/// Each member is documented as <see cref="Dir"/> implements it. Another implementation is
/// under no obligation to behave the same way, and that is exactly the point made above.
/// </para>
/// </remarks>
public interface IDir : IDisposable
{
    /// <inheritdoc cref="Dir.SymlinkPolicy"/>
    SymlinkPolicy SymlinkPolicy { get; }

    /// <inheritdoc cref="Dir.Backend"/>
    ResolutionBackend Backend { get; }

    /// <inheritdoc cref="Dir.OpenDir(string, bool)"/>
    IDir OpenDir(string path, bool noFollow = false);

    /// <inheritdoc cref="Dir.TryOpenDir(string, out Dir)"/>
    bool TryOpenDir(string path, [NotNullWhen(true)] out IDir? dir);

    /// <inheritdoc cref="Dir.TryOpenDir(string, bool, out Dir)"/>
    bool TryOpenDir(string path, bool noFollow, [NotNullWhen(true)] out IDir? dir);

    /// <inheritdoc cref="Dir.CreateDir(string)"/>
    IDir CreateDir(string path);

    /// <inheritdoc cref="Dir.TryCreateDir(string, out Dir)"/>
    bool TryCreateDir(string path, [NotNullWhen(true)] out IDir? dir);

    /// <inheritdoc cref="Dir.OpenOrCreateDir(string)"/>
    IDir OpenOrCreateDir(string path);

    /// <inheritdoc cref="Dir.TryOpenOrCreateDir(string, out Dir)"/>
    bool TryOpenOrCreateDir(string path, [NotNullWhen(true)] out IDir? dir);

    /// <inheritdoc cref="Dir.OpenFile(string, FileMode, FileAccess, FileShare, FileOptions, long, bool, bool)"/>
    ICapFile OpenFile(
        string path,
        FileMode mode = FileMode.Open,
        FileAccess access = FileAccess.Read,
        FileShare share = FileShare.Read,
        FileOptions options = FileOptions.None,
        long preallocationSize = 0,
        bool append = false,
        bool noFollow = false);

    /// <inheritdoc cref="Dir.TryOpenFile(string, out CapFile)"/>
    bool TryOpenFile(string path, [NotNullWhen(true)] out ICapFile? file);

    /// <inheritdoc cref="Dir.TryOpenFile(string, FileMode, FileAccess, FileShare, FileOptions, long, bool, bool, out CapFile)"/>
    bool TryOpenFile(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options,
        long preallocationSize,
        bool append,
        bool noFollow,
        [NotNullWhen(true)] out ICapFile? file);

    /// <inheritdoc cref="Dir.OpenAny(string, FileShare, FileOptions, bool)"/>
    ICapOpened OpenAny(
        string path,
        FileShare share = FileShare.Read,
        FileOptions options = FileOptions.None,
        bool noFollow = false);

    /// <inheritdoc cref="Dir.TryOpenAny(string, out CapOpened)"/>
    bool TryOpenAny(string path, [NotNullWhen(true)] out ICapOpened? opened);

    /// <inheritdoc cref="Dir.TryOpenAny(string, bool, out CapOpened)"/>
    bool TryOpenAny(string path, bool noFollow, [NotNullWhen(true)] out ICapOpened? opened);

    /// <inheritdoc cref="Dir.CreateFile(string)"/>
    ICapFile CreateFile(string path);

    /// <inheritdoc cref="Dir.CreateNewFile(string)"/>
    ICapFile CreateNewFile(string path);

    /// <inheritdoc cref="Dir.TryCreateFile(string, out CapFile)"/>
    bool TryCreateFile(string path, [NotNullWhen(true)] out ICapFile? file);

    /// <inheritdoc cref="Dir.TryCreateNewFile(string, out CapFile)"/>
    bool TryCreateNewFile(string path, [NotNullWhen(true)] out ICapFile? file);

    /// <inheritdoc cref="Dir.ReadAllBytes(string)"/>
    byte[] ReadAllBytes(string path);

    /// <inheritdoc cref="Dir.ReadAllText(string)"/>
    string ReadAllText(string path);

    /// <inheritdoc cref="Dir.WriteAllBytes(string, ReadOnlySpan{byte})"/>
    void WriteAllBytes(string path, ReadOnlySpan<byte> bytes);

    /// <inheritdoc cref="Dir.ReadAllBytesAsync(string, CancellationToken)"/>
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="Dir.ReadAllTextAsync(string, CancellationToken)"/>
    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="Dir.WriteAllBytesAsync(string, ReadOnlyMemory{byte}, CancellationToken)"/>
    Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="Dir.DeleteFile(string)"/>
    void DeleteFile(string path);

    /// <inheritdoc cref="Dir.TryDeleteFile(string)"/>
    bool TryDeleteFile(string path);

    /// <inheritdoc cref="Dir.DeleteDir(string)"/>
    void DeleteDir(string path);

    /// <inheritdoc cref="Dir.TryDeleteDir(string)"/>
    bool TryDeleteDir(string path);

    // The destination's parameter is called `to` here as it is on Dir, where it pairs with
    // `from`. Visual Basic reserves the word, but that language can still implement the
    // member by bracketing it, and a different name here would make named arguments differ
    // between the handle and the interface, and hide the argument from the analyzer rule
    // that recognises path parameters by name.
#pragma warning disable CA1716
    /// <inheritdoc cref="Dir.Rename(string, IDir, string, bool)"/>
    void Rename(string from, IDir toDir, string to, bool replaceExisting = false);

    /// <inheritdoc cref="Dir.TryRename(string, IDir, string, bool)"/>
    bool TryRename(string from, IDir toDir, string to, bool replaceExisting = false);

    /// <inheritdoc cref="Dir.CreateSymlink(string, string)"/>
    void CreateSymlink(string linkPath, string target);

    /// <inheritdoc cref="Dir.TryCreateSymlink(string, string)"/>
    bool TryCreateSymlink(string linkPath, string target);

    /// <inheritdoc cref="Dir.CreateDirSymlink(string, string)"/>
    void CreateDirSymlink(string linkPath, string target);

    /// <inheritdoc cref="Dir.TryCreateDirSymlink(string, string)"/>
    bool TryCreateDirSymlink(string linkPath, string target);

    /// <inheritdoc cref="Dir.CreateHardLink(string, IDir, string, bool)"/>
    void CreateHardLink(string path, IDir toDir, string to, bool followLink = false);

    /// <inheritdoc cref="Dir.TryCreateHardLink(string, IDir, string, bool)"/>
    bool TryCreateHardLink(string path, IDir toDir, string to, bool followLink = false);
#pragma warning restore CA1716

    /// <inheritdoc cref="Dir.Exists(string)"/>
    bool Exists(string path);

    /// <inheritdoc cref="Dir.ReadLink(string)"/>
    string ReadLink(string path);

    /// <inheritdoc cref="Dir.TryReadLink(string, out string)"/>
    bool TryReadLink(string path, [NotNullWhen(true)] out string? target);

    /// <inheritdoc cref="Dir.GetMetadata()"/>
    CapMetadata GetMetadata();

    /// <inheritdoc cref="Dir.GetMetadata(string, bool)"/>
    CapMetadata GetMetadata(string path, bool followLink = false);

    /// <inheritdoc cref="Dir.TryGetMetadata(string, out CapMetadata)"/>
    bool TryGetMetadata(string path, out CapMetadata metadata);

    /// <inheritdoc cref="Dir.TryGetMetadata(string, bool, out CapMetadata)"/>
    bool TryGetMetadata(string path, bool followLink, out CapMetadata metadata);

    /// <inheritdoc cref="Dir.SetTimes(CapFileTime, CapFileTime)"/>
    void SetTimes(CapFileTime lastAccess = default, CapFileTime lastWrite = default);

    /// <inheritdoc cref="Dir.SetTimes(string, CapFileTime, CapFileTime, bool)"/>
    void SetTimes(
        string path,
        CapFileTime lastAccess = default,
        CapFileTime lastWrite = default,
        bool followLink = false);

    /// <inheritdoc cref="Dir.TrySetTimes(string, CapFileTime, CapFileTime, bool)"/>
    bool TrySetTimes(
        string path,
        CapFileTime lastAccess = default,
        CapFileTime lastWrite = default,
        bool followLink = false);

    /// <inheritdoc cref="Dir.Flush(bool)"/>
    bool Flush(bool toDisk);

    /// <inheritdoc cref="Dir.EnumerateEntries()"/>
    IEnumerable<IDirEntry> EnumerateEntries();

    /// <inheritdoc cref="Dir.EnumerateEntriesAsync(CancellationToken)"/>
    IAsyncEnumerable<IDirEntry> EnumerateEntriesAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc cref="Dir.Clone()"/>
    IDir Clone();

    /// <inheritdoc cref="Dir.TryClone(out Dir)"/>
    bool TryClone([NotNullWhen(true)] out IDir? clone);

    /// <inheritdoc cref="Dir.Restrict(SymlinkPolicy)"/>
    IDir Restrict(SymlinkPolicy policy);

    /// <inheritdoc cref="Dir.TryRestrict(SymlinkPolicy, out Dir)"/>
    bool TryRestrict(SymlinkPolicy policy, [NotNullWhen(true)] out IDir? restricted);

    /// <inheritdoc cref="Dir.TryGetPath(AmbientAuthority, out string)"/>
    bool TryGetPath(AmbientAuthority authority, [NotNullWhen(true)] out string? path);
}
