namespace Cap.Fs.Ext;

/// <summary>
/// What a recursive copy does with what it finds.
/// </summary>
public sealed class CopyOptions
{
    /// <summary>
    /// The ordinary settings: files and directories only, nothing overwritten, permissions
    /// left to the destination.
    /// </summary>
    public static CopyOptions Default { get; } = new();

    /// <summary>What the copy does with a symbolic link.</summary>
    /// <remarks>
    /// Refusing by default is the conservative reading and the one that cannot surprise: a
    /// caller who copies a tree containing links and is told about it can decide whether the
    /// links belong in the copy, whereas a caller whose links silently became copies of their
    /// targets has a destination that is a different shape from the source and no way to know.
    /// </remarks>
    public CopyAction Symlinks { get; init; } = CopyAction.Fail;

    /// <summary>
    /// What the copy does with a named pipe, a socket, a device node, or anything else the
    /// filesystem will not classify.
    /// </summary>
    /// <remarks>
    /// <see cref="CopyAction.Recreate"/> is not a value this accepts: there is no way here to
    /// create any of them, and approximating one with an empty file would put an object of the
    /// wrong kind under the right name.
    /// </remarks>
    public CopyAction OtherKinds { get; init; } = CopyAction.Fail;

    /// <summary>
    /// Whether a name already taken in the destination is written over.
    /// </summary>
    /// <remarks>
    /// Off, so that the destructive reading is never the one a caller gets without asking for
    /// it. With it off, a name already present in the destination stops the copy; with it on,
    /// a file is replaced, a directory is used as it is and copied into, and a name holding
    /// one kind where the source has the other still stops the copy — replacing a directory
    /// with a file, or the reverse, is not something a copy should decide to do.
    /// </remarks>
    public bool Overwrite { get; init; }

    /// <summary>
    /// Whether each copied object is given the permissions the source object had.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off, so that copies get whatever the destination directory and the process's own
    /// settings would give a newly created object. With it on, each file and directory is
    /// given the source's permissions as that platform records them: the mode bits on Unix,
    /// the attribute flags on Windows. A value recorded by one platform is not translated into
    /// the other's.
    /// </para>
    /// <para>
    /// A symbolic link's own permissions are never carried across, on any platform. Most
    /// systems do not record any, and the ones that do offer no way to set them without
    /// following the link — which is the one thing a copy of a link must not do.
    /// </para>
    /// </remarks>
    public bool PreservePermissions { get; init; }

    /// <summary>
    /// How many levels below the source the copy will descend.
    /// </summary>
    /// <remarks>
    /// The same limit and the same reason as a walk's: the copy holds one open directory per
    /// level on each side for as long as it is inside that level. A source deeper than this
    /// stops the copy rather than being quietly cut short.
    /// </remarks>
    public int MaxDepth { get; init; } = WalkOptions.DefaultMaxDepth;
}
