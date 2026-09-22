namespace Cap.Primitives.Interop;

/// <summary>
/// Everything an open of a file decides, once resolution has decided which file.
/// </summary>
/// <remarks>
/// <para>
/// Directories are opened for one of two authorities and nothing else, which is why they
/// are described by <see cref="CapAccess"/>. A file open has more to say: whether the name
/// may be created, whether what is there is to be kept, how large the file should be made
/// to start with, and whether the handle is to be capable of overlapped operations. None of
/// that belongs in a walk — a resolution that could create things while it was still
/// deciding whether it was allowed to look at them would be answering two questions at
/// once — so it is carried separately and applied to the last component alone.
/// </para>
/// <para>
/// The vocabulary is the framework's own rather than a second one invented here. Every
/// field means exactly what it means to <c>System.IO</c>, a caller writing against these
/// APIs is already holding the values, and a private set of near-synonyms would have to be
/// translated at both ends — which is two chances to translate one of them wrongly for no
/// gain. What this layer adds is not a different vocabulary but a narrower one: a request
/// is validated once where it enters the library, and what reaches a platform here has
/// already been checked for the combinations that cannot mean anything.
/// </para>
/// <para>
/// Not every field is honoured everywhere, and the ones that are not are refused rather
/// than dropped. A platform that has no way to remove a file when its last handle closes
/// reports that it cannot, instead of opening a handle that behaves differently from the
/// one that was asked for. The difference matters most for exactly the options a caller
/// reaches for when they are being careful.
/// </para>
/// </remarks>
internal readonly struct FileOpenRequest
{
    /// <summary>Describes an open.</summary>
    public FileOpenRequest(
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options,
        long preallocationSize)
    {
        Mode = mode;
        Access = access;
        Share = share;
        Options = options;
        PreallocationSize = preallocationSize;
    }

    /// <summary>Whether the name may be created, and what happens to what is already there.</summary>
    public FileMode Mode { get; }

    /// <summary>The data access the handle carries.</summary>
    public FileAccess Access { get; }

    /// <summary>
    /// What other openers may do while this handle is open.
    /// </summary>
    /// <remarks>
    /// Enforced only where the platform enforces sharing at all, which is Windows. It is not
    /// a containment control anywhere: a caller that can open a file can read it, and a share
    /// mode denied to somebody else says nothing about what this handle may reach.
    /// </remarks>
    public FileShare Share { get; }

    /// <summary>The flags and hints the open carries.</summary>
    public FileOptions Options { get; }

    /// <summary>
    /// How much space to reserve for a file this open creates, or zero to reserve none.
    /// </summary>
    public long PreallocationSize { get; }

    /// <summary>Whether the handle is to be capable of overlapped operations.</summary>
    public bool IsAsynchronous => (Options & FileOptions.Asynchronous) != 0;

    /// <summary>Whether the open may bring the file into existence.</summary>
    public bool Creates => Mode is not (FileMode.Open or FileMode.Truncate);

    /// <summary>
    /// Whether the open discards what the file already holds, which is the only case in
    /// which reserving space for a file that was already there is meaningful.
    /// </summary>
    public bool Truncates => Mode is FileMode.Create or FileMode.Truncate;

    /// <summary>
    /// An open of a file that must already exist, sharing as widely as the platform allows.
    /// </summary>
    /// <remarks>
    /// What resolution itself asks for. A walk opens the thing a path names and never makes
    /// it, and it holds the handle only long enough to hand it back, so denying anybody else
    /// access for the duration would make ordinary concurrent use of a sandbox fail for
    /// reasons that have nothing to do with the sandbox.
    /// </remarks>
    public static FileOpenRequest Existing(FileAccess access) => new(
        FileMode.Open,
        access,
        FileShare.ReadWrite | FileShare.Delete,
        FileOptions.None,
        preallocationSize: 0);
}
