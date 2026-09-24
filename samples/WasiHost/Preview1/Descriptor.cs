using Cap.Primitives;
using Cap.Std;

namespace WasiHost.Preview1;

/// <summary>
/// What one guest file descriptor number stands for.
/// </summary>
/// <remarks>
/// Rights are kept with the descriptor, as the ABI describes them, and can only ever be
/// narrowed. What a descriptor can actually reach is decided by the capability it wraps, not
/// by its rights: the rights are the guest's own bookkeeping, and the <see cref="Dir"/> or
/// <see cref="CapFile"/> is the authority.
/// </remarks>
internal abstract class Descriptor : IDisposable
{
    protected Descriptor(Rights rightsBase, Rights rightsInheriting)
    {
        RightsBase = rightsBase;
        RightsInheriting = rightsInheriting;
    }

    public Rights RightsBase { get; set; }

    public Rights RightsInheriting { get; set; }

    public FdFlags Flags { get; set; }

    public abstract FileType Type { get; }

    public abstract void Dispose();
}

/// <summary>A directory: a <see cref="Dir"/>, which is all a directory descriptor needs to be.</summary>
internal sealed class DirectoryDescriptor : Descriptor
{
    private Dir? _nofollow;

    public DirectoryDescriptor(Dir dir, Rights rightsBase, Rights rightsInheriting, string? preopenName = null)
        : base(rightsBase, rightsInheriting)
    {
        Dir = dir;
        PreopenName = preopenName;
    }

    public Dir Dir { get; }

    /// <summary>The name the guest was told this directory has, when the host preopened it.</summary>
    public string? PreopenName { get; }

    public override FileType Type => FileType.Directory;

    /// <summary>
    /// The same directory, refusing every symbolic link, for a lookup the guest asked not to
    /// follow one.
    /// </summary>
    /// <remarks>
    /// WASI's no-follow asks only that a link at the last component not be followed.
    /// <see cref="SymlinkPolicy.Deny"/> is the nearest thing the library offers, and it is
    /// stricter: a link before the last component is refused as well. Stricter is the safe
    /// direction to be wrong in, since it can refuse something the guest was entitled to but
    /// cannot reach something it was not. Made on first use and kept, since a restricted
    /// handle is a second open of the same directory.
    /// </remarks>
    public Dir NoFollow => _nofollow ??= Dir.Restrict(SymlinkPolicy.Deny);

    public override void Dispose()
    {
        _nofollow?.Dispose();
        Dir.Dispose();
    }
}

/// <summary>An open file, and the position the guest's unpositioned reads and writes use.</summary>
internal sealed class FileDescriptor : Descriptor
{
    public FileDescriptor(CapFile file, FileType type, Rights rightsBase, Rights rightsInheriting)
        : base(rightsBase, rightsInheriting)
    {
        File = file;
        Type = type;
    }

    public CapFile File { get; }

    /// <summary>
    /// Where the next <c>fd_read</c> or <c>fd_write</c> starts. <see cref="CapFile"/> has no
    /// position of its own — every read and write names its offset — so the descriptor
    /// keeps one, the way a kernel keeps one per open file description.
    /// </summary>
    public long Position { get; set; }

    public override FileType Type { get; }

    public override void Dispose() => File.Dispose();
}

/// <summary>Standard input, output or error, as the host supplied them.</summary>
internal sealed class StreamDescriptor : Descriptor
{
    public StreamDescriptor(Stream stream, Rights rights)
        : base(rights, Rights.None) => Stream = stream;

    public Stream Stream { get; }

    public override FileType Type => FileType.CharacterDevice;

    /// <summary>Nothing to release: the streams belong to the host.</summary>
    public override void Dispose()
    {
    }
}
