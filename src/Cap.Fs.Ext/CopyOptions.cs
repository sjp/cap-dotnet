namespace Cap.Fs.Ext;

/// <summary>
/// What a recursive copy does with what it finds.
/// </summary>
/// <remarks>
/// <strong>Immutable once constructed.</strong> Every property is set only by an object
/// initializer, so one instance — <see cref="Default"/> included — can be shared by any number
/// of copies on any number of threads.
/// </remarks>
public sealed class CopyOptions
{
    /// <summary>
    /// The ordinary settings: files and directories only, nothing overwritten, permissions
    /// left to the destination.
    /// </summary>
    public static CopyOptions Default { get; } = new();

    /// <summary>What the copy does with a symbolic link.</summary>
    /// <remarks>
    /// <para>
    /// Refusing by default is the conservative reading and the one that cannot surprise: a
    /// caller who copies a tree containing links and is told about it can decide whether the
    /// links belong in the copy, whereas a caller whose links silently became copies of their
    /// targets has a destination that is a different shape from the source and no way to know.
    /// </para>
    /// <para>
    /// Applied to every link found anywhere in the source tree, whatever its target is — a
    /// file, a directory, nothing, or somewhere outside. A link is recognised by describing
    /// the name without following it, and under none of the three settings is it followed,
    /// read through or descended into.
    /// </para>
    /// <para>
    /// <see cref="CopyAction.Recreate"/> stores the same target text in the new link, which
    /// cannot be done for a rooted target: a link beneath a handle may not store one. Such a
    /// link stops the copy with <see cref="Cap.Std.SandboxEscapeException"/>, before anything
    /// already at its name in the destination is touched.
    /// </para>
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
    /// <para>
    /// Off, so that the destructive reading is never the one a caller gets without asking for
    /// it. With it off, a name already present in the destination stops the copy; with it on,
    /// a file is replaced, a directory is used as it is and copied into, and a name holding
    /// one kind where the source has the other still stops the copy — replacing a directory
    /// with a file, or the reverse, is not something a copy should decide to do.
    /// </para>
    /// <para>
    /// <strong>A file is replaced as a name, not rewritten in place.</strong> The copy is
    /// written under a scratch name in the same directory and then moved onto the name, so
    /// what was there is swapped for a new file rather than truncated and refilled. A copy
    /// that fails part of the way through a file leaves the old file as it was. The new file
    /// is a new object: it does not keep the old one's permissions (it gets the source's, with
    /// <see cref="PreservePermissions"/>, or what a new file gets otherwise), and any other
    /// hard link to the old file still holds the old contents.
    /// </para>
    /// <para>
    /// <strong>Symbolic links already in the destination.</strong> With this off, a link at a
    /// name the copy needs stops the copy like any other taken name, whatever it points at.
    /// With it on, a link where the source has a file is replaced by the copied file, like any
    /// other file: the link is gone afterwards, and whatever it pointed at, inside the
    /// destination's subtree or not, is left untouched. A link where the source has a link is
    /// removed as a name and the new link made in its place. A link where the source has a
    /// directory still stops the copy, because the directory is opened in a way that refuses
    /// to follow one; the copy never descends into a directory a link chose, and never removes
    /// a link to make room for one. Nothing already in the destination is ever written
    /// through a link.
    /// </para>
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
    /// stops the copy rather than being quietly cut short. A symbolic link is never descended
    /// into, so it adds no depth, whatever it points at.
    /// </remarks>
    public int MaxDepth { get; init; } = WalkOptions.DefaultMaxDepth;
}
